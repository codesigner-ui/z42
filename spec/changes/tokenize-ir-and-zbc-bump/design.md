# Design: tokenize-ir-and-zbc-bump

> Sibling docs: ./proposal.md, ./specs/metadata-tokens/spec.md, ./tasks.md
> 创建：2026-05-09

## Architecture

数据流（pipeline 端到端）：

```
.z42 source
   │
   ▼
[Lexer] → [Parser] → AST
   │
   ▼
[TypeChecker] → BoundTree (按字符串名引用)
   │
   ▼
[IrGen + TokenAllocator]                        ←── NEW: TokenAllocator
   │   sort declarations → assign MethodId/TypeId/StaticFieldId
   │   build import_table for cross-zpkg refs
   ▼
IrModule (字段已是 Token32)                     ←── MODIFIED: string fields → token
   │
   ▼
[ZbcWriter 1.0] → .zbc + IMPT 区段             ←── MODIFIED: u32 fields + IMPT 扩展
   │
   ▼
.zbc on disk

──────────── boundary ────────────

.zbc / .zpkg
   │
   ▼
[VM ZbcReader 1.0] → Module                    ←── MODIFIED: 1.0 only, 无 fallback
   │
   ▼
[loader] →
   ├─ build type_registry_vec (by TypeId)      ←── NEW: Vec
   ├─ build type_registry_by_name (by name)    ←── NEW: HashMap → TypeId
   └─ merge cross-module import_tables
   │
   ▼
[metadata::resolver] → init VCall/Field IC     ←── SIMPLIFIED: 仅 IC，无 token resolve
   │
   ▼
[Vm::run]
   ├─ interp: 直接读 IR 字段 token                ←── SIMPLIFIED
   └─ JIT: codegen 直接 emit iconst <token>      ←── SIMPLIFIED
```

## Decisions

### Decision 1: Token32 binary layout

**问题**：IR 字段是 `Token32(u32)` 包装。binary 编码用纯 `u32` 还是结构化 `(kind:u8, body:u24)`？

**选项**：
- A. `#[repr(transparent)] u32`，高字节始终 0；newtype 区分 kind（`MethodId(u32)` / `TypeId(u32)` 等）
- B. `(kind: u8, body: u24)`，高字节即 kind tag；运行期解码后 dispatch
- C. `#[repr(C, packed)] { kind: u8, _pad: [u8;3], body: u32 }` 8-byte token

**决定**：**选 A**。
- 编译期类型系统已经区分 kind（newtype），不需要运行期 kind 标签
- 跟 IL `0x06...` 看齐但不引入解码代价
- 高字节预留 0 → 未来需要 B2（CLR-style 统一 token）时只需启用高字节，IR 字段类型无须改
- 验证简单：`assert!(token >> 24 == 0)`

### Decision 2: cross-zpkg ref 编码（**2026-05-09 S3 redesign**）

**问题**：cross-zpkg refs 在 wire format 里怎么表示？

**旧选项**（已废弃）：
- A. 新增 IMPT entry 格式 `[(kind, name_str_idx)*]`，IR token = `IMPORT_BASE + import_table_idx`，packed zpkg 还需 per-module IMPT
- B. 按 kind 拆 4 个子表 + per-module IMPT
- C. 不动，全部走 STRS 池

**实施尝试**（2026-05-09 旧 S3，commits 在 `wip/phase3-s3-broken`）：选 A。失败原因：per-module IMPT 在 packed zpkg + sort coordination + cross-module token resolution 三件事的复杂度叠加在一起，stdlib 加载链路在 lazy load 时 token → name 失败。

**新决定**（2026-05-09 redesign）：**复用 STRS 池，token 用 IMPORT_BASE 标志位区分**。

Token 编码语义（u32）：

```
intra-module:    [0,             0x7FFF_FFFE]   token = local index in module.Functions / module.Classes
IMPORT_BASE:     0x8000_0000
cross-zpkg:      [0x8000_0000,   0xFFFF_FFFE]   token - IMPORT_BASE = STRS pool index of FQ name
UNRESOLVED:      0xFFFF_FFFF
```

**关键简化**：
- ❌ 不引入新 IMPT entry 格式
- ❌ 不需要 packed zpkg per-module IMPT
- ❌ 不需要 sort coordination（TIDX remap / sorted SIGS-FUNC）
- ❌ 不需要 cross-module token resolution 专用逻辑
- ✅ Decoder 简单：`token < IMPORT_BASE ? local_table[token] : pool[token - IMPORT_BASE]`
- ✅ Encoder 简单：`if name in local: write index; else write IMPORT_BASE + pool.Idx(name)`

### Decision 3: TokenAllocator 算法（**2026-05-09 S3 redesign**）

**问题**：怎么生成 deterministic token？

**新算法**（替代旧 Ordinal 排序方案）：
1. TokenAllocator 直接用 `module.Functions` / `module.Classes` 的 List 索引作 token id
2. Resolve 时：
   - `name` 命中本地表 → 返回该索引（< IMPORT_BASE）
   - 否则 → 通过 StringPool intern 该 name，返回 `IMPORT_BASE + pool.Idx(name)`
3. emit 时把每个引用站点的 string name 替换为 lookup 出来的 token

无需"DiscoverImport pre-pass"——cross-zpkg 引用在 emit 期就地写 STRS 池。

**Determinism 保证**：
- IrGen 的 IR module 收集是源序遍历 → `module.Functions` / `module.Classes` 是源序确定的
- StringPool intern 顺序由 IrGen visit 顺序决定 → 同源同输入 → 同 pool layout
- 故同源同 toolchain → 同 zbc 字节级输出（reproducible build 满足）

**取舍**：用户改源即换 token（spec.md "改源即换 token" scenario 接受这个行为）。换得无 sort 协调 / 无 TIDX remap / 无 sorted SIGS。

### Decision 4: type_registry 重构策略

**问题**：从 `HashMap<String, Arc<TypeDesc>>` 变 `(Vec<Arc<TypeDesc>>, HashMap<String, TypeId>)`，怎么保证 `Vec` 索引等于 TypeId？

**算法**（loader 端）：
1. zbc 1.0 中 `Module.classes` 已按 TypeId 顺序序列化（编译期保证）
2. loader 顺序构建 `type_registry_vec`：`for (idx, cls) in classes.iter().enumerate() { vec.push(build_type_desc(cls, idx as TypeId)); name_map.insert(cls.name, idx); }`
3. cross-zpkg lazy 加载：lazy_loader 解析后调用 `module.register_lazy_type(td) -> TypeId`，append 到 vec 末尾，回写 `td.id = vec.len() - 1`，更新 name_map

边界 case：
- 多个 zpkg merge 后 TypeId 冲突：merge 期重 mapping，每个 module 内部 TypeId 经 `id_remap` 统一到 global TypeId 空间
- 这部分 merge 逻辑放进 `metadata::merge`

### Decision 5: ResolvedTokens 字段清理策略

**问题**：Phase 1 的 ResolvedTokens 大部分字段不再需要（method_tokens / builtin_tokens / type_tokens / static_field_tokens / site_index）。删除还是留空？

**选项**：
- A. 全部删除，只保留 `vcall_ic` + `field_ic`
- B. 保留字段名但 always-empty（兼容性）

**决定**：**选 A**。pre-1.0 不留兼容路径；删除更清爽。Resolver 简化为只 init IC。

### Decision 6: 实施分阶段（**2026-05-09 S3 redesign**）

**问题**：旧分阶段尝试（S3.A-D + S4 + S5）3 件事叠加复杂度过高，重设计后可以更细更安全。

| Stage | 内容 | 状态 |
|---|---|---|
| **S0** | Token32 wrapper + 类型骨架（C#+Rust） | 🟢 commit `626beb8` |
| **S1** | type_registry Vec restructure（VM 内部，吸收 Phase 2.D） | 🟢 commit `58f17b0`（**回退点**） |
| **S2 step 1** | TokenAllocator standalone | 🟢 commit `3306659` |
| **S2 step 2** | IrGen sibling output | 🟢 commit `dca32ee` |
| **S3 (旧)** | v1.0 wire format + IMPT 扩展 + per-module IMPT + sort 协调 | ❌ broken @ `833193a` (wip/phase3-s3-broken) |
| **S3a (新)** | Rust ZbcReader 接受 v1.0 + v0.9（双版本读，主写仍 v0.9） | 🟡 待实施 |
| **S3b (新)** | C# ZbcWriter / ZbcReader 默认改 v1.0；stdlib + golden regen；测试全绿 | 🟡 待实施 |
| **S3c (新)** | 清理 v0.9 fallback；Rust 拒绝 v0.9 zbc | 🟡 待实施 |
| **S4 (旧)** | Rust Instruction enum 字段 String → newtype | ❌ 移入 Out of Scope（IR 字段保持 String） |
| **S5** | Reproducibility tests + 文档同步 + 归档 | 🟡 待实施 |

**S3 三步骤 redesign 的关键**：
- 每步独立 GREEN（main 不破）
- S3a：runtime 兼容两版本 → 不破现有 v0.9 stdlib / golden
- S3b：编译器切 v1.0 + regen 全部 artifacts → runtime 已能读，所以一次 commit 通过
- S3c：清理（runtime 收紧到只读 v1.0）

**S3 + S4 + S5 的 BUILTIN/native 字段不变**：spec.md 已明确 BUILTIN.name / VCall.method / Field*.field_name / CallNative* 不 tokenize。

### Decision 7: 老 zbc 不留 fallback

**问题**：VM 遇到 0.9 zbc 怎么办？

**决定**：直接报错，无 fallback。CLAUDE.md "不为旧版本提供兼容" 规则适用。报错信息清晰指引：`"zbc 1.0 required, got 0.9. Run 'regen-golden-tests.sh' or rebuild your sources."`

## Implementation Notes

### Token32 newtype 设计（C# 端）

```csharp
namespace Z42.IR;

public readonly record struct MethodId(uint Value)
{
    public const uint Unresolved = 0xFFFF_FFFFu;
    public const uint ImportBase = 0x8000_0000u;
    public bool IsResolved => Value != Unresolved;
    public bool IsImport => Value >= ImportBase && Value != Unresolved;
    public uint ImportIdx => Value - ImportBase;
}

// 同理 TypeId / StaticFieldId / BuiltinId
```

### Token32 newtype 设计（Rust 端）

```rust
// metadata/tokens.rs
#[repr(transparent)]
#[derive(Copy, Clone, Eq, PartialEq, Hash)]
pub struct MethodId(pub u32);

impl MethodId {
    pub const UNRESOLVED: MethodId = MethodId(0xFFFF_FFFF);
    pub const IMPORT_BASE: u32 = 0x8000_0000;
    pub fn is_resolved(self) -> bool { self.0 != Self::UNRESOLVED.0 }
    pub fn is_import(self) -> bool { self.0 >= Self::IMPORT_BASE && self.0 != Self::UNRESOLVED.0 }
    pub fn import_idx(self) -> u32 { self.0 - Self::IMPORT_BASE }
}
// 同理 TypeId / StaticFieldId / BuiltinId
```

### IrModule 字段（C#）保持 String — 实施期裁决（2026-05-09）

C# IR records 不动；TokenAllocator 作为 IrGen 的 sibling output 与 IR module
一起产出。tokenization 只在 wire format 边界（ZbcWriter v1.0 输出 / ZbcReader
v1.0 输入 / Rust Instruction enum）落地。

```csharp
// 维持现状（不变）
public sealed record CallInstr(TypedReg Dst, string Func, List<TypedReg> Args) : IrInstr;
```

理由：C# IR records 不持久化；改字段类型不影响 zbc 二进制 + Rust runtime
hot path，但会级联到 ~15 个 C# 文件 + IrVerifier 诊断 / ZasmWriter 文本输出
等需要从 token 反查名字。投入不成正比。

### Rust Instruction enum 字段迁移示例

```rust
// Before
Call { dst: Reg, func: String, args: Vec<Reg> }

// After
Call { dst: Reg, func: MethodId, args: Vec<Reg> }
```

> Serde：`MethodId` 实现 `Serialize`/`Deserialize` 为 `u32`（用 `serde(transparent)`）。

### import_table 数据结构

```rust
// metadata/bytecode.rs
#[derive(Debug, Default, Serialize, Deserialize)]
pub struct ImportTable {
    pub entries: Vec<ImportEntry>,
}

#[derive(Debug, Serialize, Deserialize)]
pub struct ImportEntry {
    pub kind: ImportKind,    // u8
    pub name: String,       // string pool idx in zbc; reified to String at load
}

#[derive(Debug, Copy, Clone, Serialize, Deserialize)]
#[repr(u8)]
pub enum ImportKind {
    Method = 0x01,
    Type = 0x02,
    StaticField = 0x03,
    Builtin = 0x04,  // 仅完备性，BUILTINS 是 closed set 不会进 imports
}
```

### type_registry 双结构 API（Rust）

```rust
// metadata/types.rs (Module struct 字段)
#[serde(skip)]
pub type_registry_vec: Vec<Arc<TypeDesc>>,
#[serde(skip)]
pub type_registry_by_name: HashMap<String, TypeId>,

// 公开方法
impl Module {
    pub fn type_by_id(&self, id: TypeId) -> Option<&Arc<TypeDesc>> {
        self.type_registry_vec.get(id.0 as usize)
    }
    pub fn type_by_name(&self, name: &str) -> Option<&Arc<TypeDesc>> {
        self.type_registry_by_name.get(name)
            .and_then(|id| self.type_by_id(*id))
    }
}
```

### Stage S1 单独可发布性

S1（type_registry Vec restructure）完成后立即跑 cargo + VM golden 验证。这个 stage 完全在 VM 内部，**不动 zbc 格式 / 不动编译器**，相当于 Phase 2.D 的延迟落地。即使最终回退到方案 B，S1 的成果也保留。

## Testing Strategy

### 单元测试

- **C# 端**：
  - `TokenAllocatorTests.cs` —— deterministic 分配验证（fix sample → fix tokens）
  - `IrVerifierTests.cs` —— token bounds 验证
  - `ReproducibilityTests.cs` —— 双编译相同源 → byte-compare
- **Rust 端**：
  - `tokens_tests.rs` —— Token32 newtype 边界（UNRESOLVED / ImportBase / 高字节断言）
  - `loader_tests.rs` —— Vec-by-TypeId 构建 + name_map 一致性
  - `lazy_loader_tests.rs` —— cross-zpkg lazy register 后 TypeId 回写正确
  - `merge_tests.rs` —— 多模块 merge 后 token 重映射正确
  - `tests/zbc_compat.rs` —— 1.0 round-trip
  - `tests/reproducibility_test.rs` —— 与 C# 端对称的双编译验证

### Golden test

- 全部 stdlib（6 个 zpkg + 140 .zbc）regen 后跑 `./scripts/test-vm.sh`
- 必须 310/310 全绿

### 集成验证

```bash
# Stage 验证脚本（每 stage 跑）
dotnet build src/compiler/z42.slnx -c Debug --no-incremental
cargo build --manifest-path src/runtime/Cargo.toml
dotnet test src/compiler/z42.Tests/z42.Tests.csproj
cargo test --manifest-path src/runtime/Cargo.toml
./scripts/test-vm.sh

# Reproducibility 验证（S5 加入 CI）
./scripts/regen-stdlib.sh && cp -r artifacts/libraries /tmp/round1
./scripts/regen-stdlib.sh && cp -r artifacts/libraries /tmp/round2
diff -r /tmp/round1 /tmp/round2  # must be empty
```

### 回归测试矩阵

| 验证项 | S1 | S2 | S3 | S4 | S5 |
|---|---|---|---|---|---|
| dotnet test | ✓ | ✓ | ✓ | ✓ | ✓ |
| cargo test | ✓ | - | ✓ | ✓ | ✓ |
| VM golden 310/310 | ✓ | - | - | ✓ | ✓ |
| stdlib regen | - | - | - | ✓ | ✓ |
| Reproducibility test | - | - | - | - | ✓ |
