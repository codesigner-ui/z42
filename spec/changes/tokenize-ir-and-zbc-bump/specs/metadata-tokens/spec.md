# Spec: metadata-tokens

> Capability: 编译器 IR + zbc 1.0 二进制格式 + 运行期 metadata 三层一致 token 系统
> Sibling docs: ../../proposal.md, ../../design.md, ../../tasks.md
> 创建：2026-05-09

## ADDED Requirements

### Requirement: Token32 统一编码（**2026-05-09 S3 redesign**）

#### Scenario: 本地 token = module 内部位置

- **WHEN** 编译器为某 `Call.Func` 解析到本地函数（在 `module.Functions` 第 42 位）
- **THEN** 写入 zbc 的 token 值为 `42`（`< IMPORT_BASE`），VM 加载后 decode 时 `funcNames[42]` 与原函数名一致

#### Scenario: 跨包 token = STRS 池索引 + IMPORT_BASE

- **WHEN** 编译器为某 `Call.Func` 解析到非本地（cross-zpkg）函数 `Std.IO.Print`
- **THEN** 该名字 intern 到 STRS 池（设其 idx = `n`），token 写入 `IMPORT_BASE + n` (`= 0x8000_0000 + n`)；VM decode 时识别 `token >= IMPORT_BASE`，取 `pool[token - IMPORT_BASE]` = `"Std.IO.Print"` 作为 IR 字段值

#### Scenario: 高字节预留 0（仅本地区段）

- **WHEN** 本地 token（`< IMPORT_BASE`）
- **THEN** 该 `u32` 的高 8 位（`token >> 24`）始终为 0；这一约束只对 intra-module 部分有效，cross-zpkg 的 token 高位是 IMPORT_BASE 标志位

#### Scenario: UNRESOLVED 哨兵

- **WHEN** 某站点目标在编译期不可解析（错误条件 / 占位）
- **THEN** 该字段写入 `0xFFFF_FFFF`（`UNRESOLVED`）；VM 加载时识别哨兵即报错或回退（与 IMPORT_BASE 区段不重叠）

### Requirement: 确定性 Token 分配

#### Scenario: 同源同 toolchain 双编译 byte-identical

- **WHEN** 用同一 `z42c` 版本编译同一份 `.z42` 源代码两次（`out1.zbc`、`out2.zbc`）
- **THEN** `sha256(out1) == sha256(out2)`，且 byte-level diff 为空

#### Scenario: MethodId 分配序（源序）

- **WHEN** 模块按源码顺序声明 `Foo.bar`、`Foo.baz`、`Aaa.zzz` 三个方法（IrGen 收集到 `module.Functions` 即此序）
- **THEN** TokenAllocator 分配 `Foo.bar → 0`、`Foo.baz → 1`、`Aaa.zzz → 2`（直接用 `module.Functions` 的索引）

#### Scenario: TypeId 分配序（源序）

- **WHEN** 模块按源码顺序声明 `class Bar`、`class Aaa`、`class Foo`（`module.Classes` 即此序）
- **THEN** TokenAllocator 分配 `Bar→0, Aaa→1, Foo→2`（直接用 `module.Classes` 的索引）

#### Scenario: 改源即换 token（接受的取舍）

- **WHEN** 用户调换源码中两个函数的声明顺序
- **THEN** 这两个函数的 MethodId 互换；其他函数不受影响。Reproducible build 要求 = 同源同构产，源改即变属正常行为
- **WHY**：取消 Ordinal 排序换来 ZbcWriter / TIDX / SIGS-FUNC 全链不需要 sort 协调；deterministic 仍由 IrGen 的源码遍历保证

#### Scenario: 模块发现顺序无关

- **WHEN** 用相同源在不同 filesystem（如 case-insensitive HFS+ vs case-sensitive APFS）编译同一 zpkg
- **THEN** zpkg.modules / zpkg.files / zpkg.exports 排序均按 source 路径字典序，二进制相同（同主机范围）

### Requirement: zbc 1.0 格式

#### Scenario: 头部版本字段

- **WHEN** 编译器写出新版 zbc
- **THEN** 头部 `zbc_version` 字段为 `[1, 0]`；旧版 0.9 zbc 加载触发明确错误（"zbc 1.0 required, got 0.9"），不尝试 fallback

#### Scenario: IMPT 区段沿用旧语义（不扩展）

- **WHEN** 模块需要列举 cross-zpkg 函数 import 名字
- **THEN** IMPT 区段保持 v0.9 形态（仅 `[(name_str_idx)*]` 列表，无 kind tag），用于 namespace 提取等诊断 / 元数据用途；**IR 字段中的 cross-zpkg 引用直接以 `IMPORT_BASE + str_idx` 编码进 token，不依赖 IMPT 区段**

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

### Requirement: IR Instruction 字段类型（**2026-05-09 S3 redesign**）

**Before** (Phase 1+2)：所有 string-bearing 字段为 `String`（运行期通过 ResolvedTokens cache 加速）

**After** (Phase 3 redesigned)：**字段类型不变**——C# / Rust 双端 IR records 均保留 `String` 字段。Tokenization 仅在 wire format 边界发生（ZbcWriter encode + ZbcReader decode），不级联到 IR struct / instruction enum。

```csharp
// C# 不变
public sealed record CallInstr(TypedReg Dst, string Func, List<TypedReg> Args) : IrInstr;
```

```rust
// Rust 不变
Call { dst: Reg, func: String, args: Vec<Reg> }
```

> **设计取舍**（2026-05-09）：原 design 要求 IR 字段改 `MethodId` / `TypeId` / `StaticFieldId` 强类型 newtype。实施期发现：(1) C# IR records 是编译期中间表示，不持久化；(2) Rust 运行期 hot path 是 ResolvedTokens cache（Phase 1+2 已优化）— IR 字段类型对热路径无影响；(3) 跨字段类型改造级联面巨大（~15 文件）。**简化方案**：tokenization 严格在 zbc encode / decode 阶段做；IR 字段保持 String 在两端一致，运行期 dispatch 行为零变化。

> 注：`VCall.method` / `Field*.field_name` / `Builtin.name` / native interop 字段在 wire format 也**不 tokenize**（Builtin 是 closed set，receiver-type-dependent 字段不能编译期解析；这些字段的 wire 编码继续是 STRS 池索引，不走 IMPORT_BASE 编码路径）。

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

## IR Mapping（**2026-05-09 S3 redesign**）

- **Token 编码（u32，wire format only）**：
  - `[0, 0x7FFF_FFFE]` → 本地 module 内位置（intra-module index into Functions / Classes / etc.）
  - `[0x8000_0000, 0xFFFF_FFFE]` → 跨包字符串名（`token - IMPORT_BASE` = STRS 池索引）
  - `0xFFFF_FFFF` → UNRESOLVED 哨兵（错误状态）
- **不引入新 IMPT entry 格式**；IMPT 区段保持 v0.9 语义（namespace 提取用），不参与 IR 字段 token 解码
- **C# 端 `MethodId` / `TypeId` / `StaticFieldId` newtype**：保留作 IR 内部辅助类型 + 测试断言；**不进 C# IR records 字段**（C# IR records 字段类型保持 `string`，详见 design.md）
- **Rust 端 `Instruction` enum 字段类型**：仍为 `String`（与 C# 对齐）；token 仅在 zbc decode 时映射回 String 后注入

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
