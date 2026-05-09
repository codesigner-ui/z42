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

### Decision 2: IMPT 区段编码

**问题**：cross-zpkg refs 怎么编码进 IMPT？

**选项**：
- A. 单一 entry 列表 `[(kind: u8, name_str_idx: u32), ...]`，按字典序排
- B. 按 kind 拆 4 个子表（method_imports / type_imports / static_field_imports / builtin_imports），每个表内字典序
- C. 不要 IMPT，cross-zpkg 直接在 IR 字段里写完整字符串（pool idx）

**决定**：**选 A**。
- 简洁：单一表 + kind tag，不需 4 个子表 header
- B 的 separation 在 reader 端意义不大（仍要按 kind dispatch）
- C 退化到 0.9 状态，违背 Phase 3 目标

import_table[idx] 与 IR 字段中 token 的关系：用 **magic threshold** 而非 high-bit：

```
intra-module:    [0,             0x7FFF_FFFE]   (~2.1B 容量，远超需求)
IMPORT_BASE:     0x8000_0000
import indices:  [0x8000_0000,   0xFFFF_FFFE]
UNRESOLVED:      0xFFFF_FFFF
```

简单且与 UNRESOLVED 共存。

### Decision 3: TokenAllocator 算法

**问题**：怎么按确定性序分配 token？

**算法**：
1. 收集所有 declarations 进 list（by walking IR module structure）
2. 对每个 kind 用 `OrderBy(...)` 排序：
   - MethodId: `(declaring_class_fq, method_name, arity, param_type_repr)`
   - TypeId: `(class_fq)`
   - StaticFieldId: `(declaring_class_fq, field_name)`
3. 按排序后顺序分配 `0, 1, 2, ...`
4. cross-zpkg refs 收集进 import_table（去重 + 排序），分配 `IMPORT_BASE + import_idx`
5. emit 时把每个引用站点的 string name 替换为 lookup 出来的 token

边界 case：
- 函数重载：`Foo.bar(int) vs Foo.bar(str)` → param_type_repr 区分
- 泛型 instantiation：本次只处理 Phase 1+2 既有的类级 type_args（已存在），方法级泛型 instantiation 不在 scope（继续走 String）
- 同名 nested class：FQ 名包含 outer

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

### Decision 6: 实施分阶段（关键决策）

**问题**：60+ 文件大手术不能一次提交。怎么拆？

**决定**：分 5 个内部阶段（每个独立 commit，最终 1 个归档）。回退点设在 Stage 1 完成后。

| Stage | 内容 | 可独立测试 | 回退安全 |
|---|---|---|---|
| **S0** | Token32 wrapper + 类型骨架（C#+Rust） | ✅ | ✅ |
| **S1** | type_registry Vec restructure（仅 VM 内部，不动 zbc 格式） | ✅ cargo + VM golden | ✅（**回退点**） |
| **S2** | TokenAllocator + IR records 类型迁移（C# 端） | ✅ dotnet test | ✅ |
| **S3** | zbc 1.0 格式 bump：ZbcWriter / ZbcReader / Rust zbc_reader 同步 | ✅ round-trip | ⚠️（影响 stdlib） |
| **S4** | VM 加载路径切换：loader / merge / lazy_loader / resolver / interp / jit | ✅ regen stdlib + 全测试 | ⚠️ |
| **S5** | Reproducibility tests + CI gate + docs sync | ✅ | ✅ |

**回退条件触发**（Stage 3-4 中）：
- 任一 Stage 工作量超出估算 1.5x → 停下评估
- 某个核心架构决策需要重新讨论 → 停下评估
- pre-existing 测试出现非平凡回归 → 停下修复

**回退动作**：
- 把 Stage 0/1 的 type_registry Vec restructure 单独归档为独立 spec（`restructure-type-registry-by-typeid`）
- Stage 2-5 的成果回滚或保留为 WIP 分支
- 重新规划余下工作

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

### IrModule 字段迁移示例（C#）

```csharp
// Before
public sealed record CallInstr(TypedReg Dst, string Func, List<TypedReg> Args) : IrInstr;

// After
public sealed record CallInstr(TypedReg Dst, MethodId Func, List<TypedReg> Args) : IrInstr;
```

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
