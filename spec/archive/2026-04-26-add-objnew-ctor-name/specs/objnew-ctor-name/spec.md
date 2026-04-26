# Spec: ObjNew ctor name dispatch

## ADDED Requirements

### Requirement: ObjNewInstr 携带具体 ctor 函数名

`ObjNewInstr` 的字段从 `(Dst, ClassName, Args)` 扩展为
`(Dst, ClassName, CtorName, Args)`。`CtorName` 是 fully-qualified ctor
函数名，含编译期 overload resolution 选定的 `$N` arity suffix（如有）。

#### Scenario: 单 ctor 类
- **GIVEN** `class Foo { Foo(int x) {} }`（仅一个 ctor）
- **WHEN** 用户代码 `new Foo(1)`
- **THEN** 生成 `ObjNewInstr(Dst, "Foo", "Foo.Foo", [arg])`
- **AND** VM `module.func_index["Foo.Foo"]` 命中（无 `$N` suffix）

#### Scenario: 双 ctor 类
- **GIVEN** `class Bar { Bar(int x) {} Bar(int x, string y) {} }`
- **AND** stdlib 编译产出函数 `Bar.Bar$1` 和 `Bar.Bar$2`
- **WHEN** 用户代码 `new Bar(1)` 和 `new Bar(1, "hi")`
- **THEN** 前者 emit `ObjNewInstr(Dst, "Bar", "Bar.Bar$1", [arg])`
- **AND** 后者 emit `ObjNewInstr(Dst, "Bar", "Bar.Bar$2", [arg1, arg2])`
- **AND** VM 各自直查命中

### Requirement: VM ObjNew 直查不推断

VM `interp/exec_instr.rs` 的 `Instruction::ObjNew` 处理路径用 `ctor_name`
字段直查 `module.func_index` / `lazy_loader::try_lookup_function`。**不再**
按 `${class}.${simple}` 推断 ctor 名。

#### Scenario: VM ObjNew 找到 ctor
- **WHEN** 执行 `ObjNew { class_name: "Foo", ctor_name: "Foo.Foo$2", args: [...] }`
- **THEN** VM 用 ctor_name 查表，命中 `Foo.Foo$2` 函数
- **AND** 调用该 ctor，args 正确传递

#### Scenario: VM ObjNew ctor 缺失
- **WHEN** ctor_name 在 module / lazy_loader 都查不到
- **THEN** VM 跳过 ctor 调用（保持现有"ctor 可选"语义，不报错）
- **AND** 对象按字段默认值（Null / 0）初始化

### Requirement: BoundNew 携带 CtorName

TypeChecker 处理 `NewExpr` 时做 ctor overload resolution，把选定 ctor 名
（含 `$N`）存入 `BoundNew.CtorName`。

#### Scenario: 重载选择匹配 args
- **GIVEN** Foo 有 ctor `Foo$1(int)` 和 `Foo$2(int, string)`
- **WHEN** TypeCheck `new Foo(1, "x")`
- **THEN** 选择 `Foo$2`，BoundNew.CtorName == `"Foo.Foo$2"`

#### Scenario: 单 ctor 名稳定
- **GIVEN** Foo 仅有 ctor `Foo(int)`（编译期不加 `$N` suffix）
- **WHEN** TypeCheck `new Foo(1)`
- **THEN** BoundNew.CtorName == `"Foo.Foo"`（无 suffix）

### Requirement: zbc 0.5 编/解码

zbc 版本 bump `[0, 4]` → `[0, 5]`。新版 OP_OBJ_NEW 编码：

```
OP_OBJ_NEW [type_tag:u8] [dst:u16] [class_name_idx:u32] [ctor_name_idx:u32] [args...]
```

#### Scenario: 0.5 zbc 写入 + 读取
- **WHEN** 写入 `ObjNewInstr(_, "Foo", "Foo.Foo$2", _)` 到 0.5 zbc
- **AND** 后续读取
- **THEN** 解码出的 `Instruction::ObjNew` 含 `ctor_name == "Foo.Foo$2"`

#### Scenario: 0.4 zbc 不兼容
- **WHEN** 加载 0.4 zbc（无 ctor_name 字段）
- **THEN** 解码器报版本错误，拒绝加载
- **AND** 用户通过 `regen-golden-tests.sh` 重生 0.5 zbc 解决

> 按 `.claude/rules/workflow.md "不为旧版本提供兼容"`，z42 pre-1.0 期间
> 不留兼容路径。残留旧 zbc 一次性 regen 即可。

### Requirement: ZasmWriter 显示 CtorName

`obj.new` 行在 ctor name 与 default `${class}.${simple}` **不同时**显示
ctor name 后缀，便于调试可见 overload 选择。

#### Scenario: ZasmWriter 显示重载
- **WHEN** ObjNewInstr CtorName == `"Foo.Foo$2"`
- **THEN** zasm 输出 `obj.new @Foo ctor=Foo.Foo$2 ...`

#### Scenario: ZasmWriter 单 ctor 不显示
- **WHEN** CtorName == `"Foo.Foo"`（与 default 一致）
- **THEN** zasm 输出 `obj.new @Foo ...`（保持现有简洁形式）

## MODIFIED Requirements

### Requirement: ObjNewInstr 字段

**Before:** `record ObjNewInstr(TypedReg Dst, string ClassName, List<TypedReg> Args)`
**After:** `record ObjNewInstr(TypedReg Dst, string ClassName, string CtorName, List<TypedReg> Args)`

**Before (Rust):** `Instruction::ObjNew { dst, class_name, args }`
**After (Rust):** `Instruction::ObjNew { dst, class_name, ctor_name, args }`

### Requirement: VM ObjNew 路径

**Before:** ctor 名按 `${class}.${simple}` 推断
**After:** ctor 名从指令直接读取（`ctor_name` 字段）

## IR Mapping

新签名:

```
ObjNew { dst, class_name, ctor_name, args }
```

opcode `0x70` 不变。线性编码新增 4 字节 ctor_name pool idx。

## Pipeline Steps

- [ ] Lexer — 不涉及
- [ ] Parser — 不涉及
- [x] **TypeChecker** — NewExpr 处理时做 ctor overload resolution，填 BoundNew.CtorName
- [x] **IR Codegen** — EmitBoundNew 把 CtorName 传给 ObjNewInstr
- [x] **zbc encoder/decoder** — 编/解码 ctor_name pool idx；旧版兼容
- [x] **VM interp** — exec_instr.rs ObjNew 用 ctor_name 直查
- [x] **VM JIT (若有 ObjNew 路径)** — 同步用 ctor_name
