# Spec: metadata-tokens

> Capability: 编译器 IR + zbc 1.0 二进制格式 + 运行期 metadata 三层一致 token 系统
> Sibling docs: ../../proposal.md, ../../design.md, ../../tasks.md
> 创建：2026-05-09

## ADDED Requirements

### Requirement: Token32 统一编码

#### Scenario: 同一 IR 引用站点写入 / 读取后值守恒

- **WHEN** 编译器为某 `Call.Func` 引用分配 `MethodId(42)`，写入 zbc
- **THEN** VM 加载该 zbc 后，`Module.functions[42].name` 与编译期该 Call 站点指向的函数名完全一致

#### Scenario: 高字节预留 0

- **WHEN** 当前版本编译器分配任何 token
- **THEN** 该 `u32` 的高 8 位（`token >> 24`）始终为 0；运行期写出诊断时若发现高字节非 0，应判定为 corrupted zbc

#### Scenario: UNRESOLVED 哨兵

- **WHEN** 某站点目标在编译期不可解析（cross-zpkg）
- **THEN** 该字段写入 `0xFFFF_FFFF`（`UNRESOLVED`），同时把目标名加入 import_table；VM 加载时识别哨兵即跳到 import_table 路径

### Requirement: 确定性 Token 分配

#### Scenario: 同源同 toolchain 双编译 byte-identical

- **WHEN** 用同一 `z42c` 版本编译同一份 `.z42` 源代码两次（`out1.zbc`、`out2.zbc`）
- **THEN** `sha256(out1) == sha256(out2)`，且 byte-level diff 为空

#### Scenario: MethodId 分配序

- **WHEN** 模块声明 `Foo.bar(int)`、`Foo.baz()`、`Aaa.zzz()` 三个方法
- **THEN** TokenAllocator 分配序为 `Aaa.zzz → Foo.bar → Foo.baz`（按 (FQ class, method, arity, params) 字典序）；`MethodId.0` 依次为 0/1/2

#### Scenario: TypeId 分配序

- **WHEN** 模块声明 `class Bar`、`class Aaa`、`class Foo`
- **THEN** TokenAllocator 分配 `Aaa→0, Bar→1, Foo→2`（按 FQ class name 字典序）

#### Scenario: StaticFieldId 分配序

- **WHEN** 多个 class 声明静态字段：`Foo.x`、`Aaa.y`、`Foo.a`
- **THEN** 分配序按 (declaring class FQ name, field name) 字典序：`Aaa.y → Foo.a → Foo.x`

#### Scenario: import_table 排序

- **WHEN** 当前模块使用 `Std.IO.Print`、`Std.Math.Abs`、`Std.IO.ReadLine` 三个 cross-zpkg 引用
- **THEN** import_table 内顺序为 `Std.IO.Print → Std.IO.ReadLine → Std.Math.Abs`（按 (kind tag, name) 字典序）

#### Scenario: 模块发现顺序无关

- **WHEN** 用相同源在不同 filesystem（如 case-insensitive HFS+ vs case-sensitive APFS）编译同一 zpkg
- **THEN** zpkg.modules / zpkg.files / zpkg.exports 排序均按 source 路径字典序，二进制相同（同主机范围）

### Requirement: zbc 1.0 格式

#### Scenario: 头部版本字段

- **WHEN** 编译器写出新版 zbc
- **THEN** 头部 `zbc_version` 字段为 `[1, 0]`；旧版 0.9 zbc 加载触发明确错误（"zbc 1.0 required, got 0.9"），不尝试 fallback

#### Scenario: IMPT 区段扩展

- **WHEN** 模块至少有 1 个 cross-zpkg 引用
- **THEN** zbc 含 IMPT 区段，每条 entry 为 `(kind: u8, name_str_idx: u32)`，按字典序排列；intra-module 引用不进入 IMPT

#### Scenario: 老 zbc 不兼容

- **WHEN** VM 试图加载 0.9 格式的 zbc
- **THEN** 立即报错（无 fallback 路径），提示用户重新编译

### Requirement: type_registry Vec-by-TypeId

#### Scenario: 按 TypeId 直查

- **WHEN** 运行期 ObjNew 携带 `TypeId(5)`
- **THEN** VM 通过 `module.type_registry_vec[5]` O(1) 拿到 `Arc<TypeDesc>`，无 HashMap 哈希

#### Scenario: 名字 fallback 仍可用

- **WHEN** 诊断 / 反射场景需按字符串名查类型
- **THEN** `module.type_registry_by_name[&name]` HashMap 仍可用，返回该 TypeDesc

#### Scenario: cross-zpkg 类型懒加载

- **WHEN** ObjNew 站点的 TypeId 为 `UNRESOLVED`
- **THEN** VM 通过 import_table 取出名字 → lazy_loader 解析 → 安装到 `type_registry_vec[next_id]` → 回写 IR 站点的 TypeId 哨兵为新 id

### Requirement: VM resolver 简化

#### Scenario: load 时只初始化 IC

- **WHEN** `metadata::resolver::resolve_module` 在 1.0 zbc 上运行
- **THEN** 不做 String→u32 解析（IR 字段已经是 token），仅为 VCall / FieldGet / FieldSet 站点初始化 IC slot 为 UNRESOLVED 默认值

#### Scenario: ResolvedTokens.method_tokens 字段移除

- **WHEN** 1.0 zbc 加载完成
- **THEN** `Function.resolved.method_tokens` / `builtin_tokens` / `type_tokens` / `static_field_tokens` 全部为空 Vec（不再需要——token 已直接在 IR 字段里）

### Requirement: stdlib + golden 全部按 1.0 重生

#### Scenario: 老 stdlib zpkg 不可读

- **WHEN** 当前已有的 0.9 stdlib zpkg 加载
- **THEN** 立即报错；用户需运行 `regen-golden-tests.sh` 或重编 stdlib

#### Scenario: regen 后全部测试通过

- **WHEN** `regen-golden-tests.sh` 跑完 + dotnet test + cargo test + ./scripts/test-vm.sh
- **THEN** 1109 dotnet + 310 VM golden 全绿

### Requirement: Reproducibility CI gate

#### Scenario: 核心 stdlib 双编译比对

- **WHEN** PR 推到 main
- **THEN** CI 跑 "compile z42.core twice; sha256 compare"；若不一致，PR 阻塞并打印 byte-diff 位置

#### Scenario: release tag 跑全（含 zpkg）

- **WHEN** 创建 release tag（v1.0.0+）
- **THEN** CI 跑 "compile all 6 stdlib zbc + zpkg twice + 140 golden zbc twice"，全部 byte-identical

## MODIFIED Requirements

### Requirement: IR Instruction 字段类型

**Before** (Phase 1+2):
```
Call { dst: Reg, func: String, args: Vec<Reg> }
ObjNew { dst: Reg, class_name: String, ctor_name: String, args: Vec<Reg>, type_args: Vec<String> }
VCall { dst: Reg, obj: Reg, method: String, args: Vec<Reg> }
FieldGet { dst: Reg, obj: Reg, field_name: String }
StaticGet { dst: Reg, field: String }
LoadFn { dst: Reg, func: String }
... (其他 string-bearing 字段同理)
```

**After** (Phase 3):
```
Call { dst: Reg, func: MethodId, args: Vec<Reg> }
ObjNew { dst: Reg, class_id: TypeId, ctor_id: MethodId, args: Vec<Reg>, type_args: Vec<TypeId> }
VCall { dst: Reg, obj: Reg, method: String, args: Vec<Reg> }   // method 不动 — receiver-type-dependent，IC 路径
FieldGet { dst: Reg, obj: Reg, field_name: String }            // field_name 不动 — 同理
StaticGet { dst: Reg, field: StaticFieldId }
LoadFn { dst: Reg, func: MethodId }
```

> 注：`VCall.method` 和 `Field*.field_name` 因为 receiver-type-dependent，无法编译期 token 化（与 receiver 实际类型一一对应）；继续保持 String，运行期通过 VCallIC / FieldIC 单态缓存。这是 Phase 1+2 既有设计，本次不动。

### Requirement: type_registry 接口

**Before**:
```rust
pub type_registry: HashMap<String, Arc<TypeDesc>>,
```

**After**:
```rust
pub type_registry_vec: Vec<Arc<TypeDesc>>,           // O(1) by TypeId
pub type_registry_by_name: HashMap<String, TypeId>,   // O(1) by name → TypeId
```

> 仍 `#[serde(skip)]`，由 loader 在 zbc 加载后构建。

### Requirement: ResolvedTokens 字段

**Before**:
```rust
pub struct ResolvedTokens {
    pub method_tokens: Vec<AtomicU32>,
    pub builtin_tokens: Vec<u32>,
    pub type_tokens: Vec<AtomicU32>,
    pub static_field_tokens: Vec<AtomicU32>,
    pub vcall_ic: Vec<VCallIC>,
    pub field_ic: Vec<FieldIC>,
    pub site_index: Vec<Vec<u32>>,
}
```

**After**:
```rust
pub struct ResolvedTokens {
    // method_tokens / builtin_tokens / type_tokens / static_field_tokens / site_index 全部移除
    // —— token 已在 IR 字段里直接编码
    pub vcall_ic: Vec<VCallIC>,   // VCall 站点单态 IC
    pub field_ic: Vec<FieldIC>,   // Field*  站点单态 IC
}
```

## IR Mapping

- **新 IR 字段**：`MethodId`、`TypeId`、`StaticFieldId` (newtype on `u32`，高字节预留 0)
- **新 IMPT 区段**条目格式（per entry）：`(kind: u8, name_str_idx: u32)`
- **import kind 标签**（C# `ImportKind` enum / Rust `ImportKind` enum；语义"哪个 token 空间被导入"）：`0x01 = Method`, `0x02 = Type`, `0x03 = StaticField`, `0x04 = Builtin (closed set, never imported but tag for completeness)`
- **UNRESOLVED 哨兵**：`0xFFFF_FFFF`，与 import_table 配合

## Pipeline Steps

受影响的 pipeline 阶段：

- [x] Lexer — 不变
- [x] Parser / AST — 不变
- [ ] TypeChecker — 不变（仍按名解析）
- [x] **IR Codegen** — IrGen 调用 TokenAllocator；emit 期写 token 而非 string
- [x] **Bytecode encoding (ZbcWriter)** — 写入 token + IMPT 扩展
- [x] **Bytecode decoding (ZbcReader / VM)** — 读 token + IMPT
- [x] **VM interp** — 直接用 token 分发
- [x] **VM JIT** — 简化，去掉 site_index 中介
- [ ] AOT — 保持 stub
