# Spec: `pinned` block syntax (C5)

## ADDED Requirements

### Requirement: Lexer recognises `pinned` as a keyword

#### Scenario: tokenises as Pinned not Identifier
- **WHEN** Lexer 扫描源码 `pinned p = s { }`
- **THEN** 第一个 token 是 `TokenKind.Pinned`，不是 `Identifier`

---

### Requirement: Parser builds `PinnedStmt` AST

#### Scenario: 基本形式
- **WHEN** 源码：
  ```z42
  fn f() : void {
      pinned p = s { let n : long = p.len; }
  }
  ```
- **THEN** Parser 生成 `PinnedStmt(name="p", source=NameExpr("s"), body=BlockStmt[VarDecl(...)])`

#### Scenario: 缺右括号 → ParseException
- **WHEN** `pinned p = s { let x : long = p.len;`（无 `}`）
- **THEN** 抛 ParseException 含 "expected `}`"

#### Scenario: 缺等号 → ParseException
- **WHEN** `pinned p s { ... }`
- **THEN** 抛 ParseException 含 "expected `=`"

---

### Requirement: TypeChecker validates pinned source type

#### Scenario: string source 通过
- **WHEN** `pinned p = s { ... }` 且 `s : string`
- **THEN** 无诊断；body 内 `p` 类型为 PinnedView

#### Scenario: 整型 source 报 Z0908
- **WHEN** `pinned p = 42 { ... }`
- **THEN** 报 Z0908 含 "source of `pinned` must be string"

#### Scenario: object source 报 Z0908
- **WHEN** `pinned p = someObj { ... }` 且 someObj 是用户类
- **THEN** 报 Z0908

---

### Requirement: TypeChecker forbids early control flow inside `pinned` body

#### Scenario: 块内 return 报 Z0908
- **WHEN** `pinned p = s { return 0; }`
- **THEN** Z0908 含 "return / break / continue / throw not allowed inside `pinned` body"

#### Scenario: 块内 break 报 Z0908
- **WHEN** 在 while 循环里 `pinned p = s { break; }`
- **THEN** Z0908

#### Scenario: 块内 throw 报 Z0908
- **WHEN** `pinned p = s { throw new Exception(""); }`
- **THEN** Z0908

#### Scenario: 块内仅顺序语句通过
- **WHEN** `pinned p = s { let a : long = p.len; let b : long = a + 1; }`
- **THEN** 无诊断

---

### Requirement: PinnedView field access

#### Scenario: `p.len` → long
- **WHEN** body 内 `let n : long = p.len;`
- **THEN** TypeChecker 接受；表达式类型 = long

#### Scenario: `p.ptr` → long
- **WHEN** body 内 `let q : long = p.ptr;`
- **THEN** TypeChecker 接受；类型 = long

#### Scenario: 未知字段报错
- **WHEN** body 内 `let x = p.unknown;`
- **THEN** TypeChecker 报字段不存在错误（普通 field-not-found 路径，不必 Z0908）

---

### Requirement: IR Codegen emits PinPtr/UnpinPtr around body

#### Scenario: 编译产物含 PinPtr ... UnpinPtr
- **WHEN** 编译 `pinned p = s { let n : long = p.len; }`
- **THEN** Function 的 IR 含按顺序：
  1. `CallExpr` / `LocalGet` 计算 source
  2. `PinPtrInstr` 生成 view 寄存器
  3. body 内的 IR（含 `FieldGetInstr` view, "len"）
  4. `UnpinPtrInstr` view

#### Scenario: 端到端运行返回长度
- **WHEN** 编译并运行 `examples/pinned_basic.z42` (`pinned p = "hello" { return p.len; }` 等价形式)
- **THEN** VM 输出字符串长度（5）

---

### Requirement: source 局部不可在 body 内重赋值

#### Scenario: body 内对 source 赋值报 Z0908
- **WHEN** ```z42
  let s = "hello";
  pinned p = s {
      s = "world";
  }
  ```
- **THEN** Z0908 含 "cannot reassign pinned source `s`"

> 注：source 是非 NameExpr 表达式（如 `pinned p = compute() { ... }`）时此规则不适用——没有可重赋值的 local。

## IR Mapping

C5 不新增 IR opcode；复用 C1 已声明、C4 已实现 runtime 的：
- `PinPtr <view> <source>`
- `UnpinPtr <view>`
- `FieldGet <dst> <view> "ptr"|"len"`

## Pipeline Steps

- [x] Lexer (Pinned keyword)
- [x] Parser / AST (PinnedStmt)
- [x] TypeChecker (source 类型 / 控制流 / source 不可变 / PinnedView 字段)
- [x] IR Codegen (PinPtr/Body/UnpinPtr)
- [ ] VM interp (C4 已 ready，无需改动)
- [ ] JIT (仍 bail，L3.M16)

## Documentation Sync

- `docs/design/grammar.peg` 加 pinned-stmt 产生式
- `docs/design/language-overview.md` 加 pinned 块语法描述
- `docs/design/interop.md` §10 C5 → ✅
- `docs/roadmap.md` C5 → ✅
- `docs/design/error-codes.md` Z0908 加 TypeChecker 三项抛出条件
