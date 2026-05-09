# Proposal: tokenize-ir-and-zbc-bump

> 类型: lang / ir / vm（编译器+VM 双端联动 + zbc 格式 bump）
> Phase 3 of method-token series（接续 Phase 1 introduce-method-token + Phase 2 formalize-jit-method-token）
> 创建：2026-05-09

## Why

Phase 1 (`introduce-method-token`, 2026-05-08) + Phase 2 (`formalize-jit-method-token`, 2026-05-08) 让运行期 hot path 完全切到 token / IC 形态，但有三处架构债务残留：

1. **IR Instruction 字段仍是 `String`**（`Call.Func`、`ObjNew.ClassName/CtorName`、`VCall.Method`、`FieldGet.FieldName`、`StaticGet.Field`、`LoadFn.Func` 等十余处）。编译器 emit String，VM 加载时 `metadata::resolver` 再做一次 String→u32 解析 + 缓存——这层间接是 Phase 1 引入的过渡形态，最终应消除。
2. **`type_registry` 仍是 `HashMap<String, Arc<TypeDesc>>`**。Phase 2.D（ObjNew TypeId 热路径）因此被推迟。
3. **zbc 0.9 格式**仍按 `string_pool + idx` 编码 IR 字段；`IMPT` 区段当前只承载 import 函数签名，不区分 token kind。

Phase 4（compiler token-aware emit）依赖于 IR 已定义 token 字段格式。一次性把 IR 字段切到 u32、type_registry 切到 Vec、zbc 格式 bump 到 1.0、stdlib regen，能彻底关闭 method-token 系列设计债务。

## What Changes

- **IR Instruction 记录**：所有 `string` 引用字段改强类型 `Token32` newtype（包装 `MethodId` / `TypeId` / `StaticFieldId` / `BuiltinId`），高字节预留 0；intra-module 直接索引，cross-zpkg 用 `UNRESOLVED` 哨兵 + import_table。
- **新增 IMPT 区段语义扩展**：从单纯 import 函数签名扩展为通用 token import（kind tag + name str_idx）。
- **zbc 格式 bump**：`ZBC_VERSION 0.9 → 1.0`。pre-1.0 不留兼容路径，老 zbc 直接重新生成。
- **type_registry 重构**：`HashMap<String, Arc<TypeDesc>>` → 双结构 `(Vec<Arc<TypeDesc>>, HashMap<String, TypeId>)`，Vec 按 TypeId 索引，HashMap 仅用于按名查（lazy + 诊断）。
- **编译器 IrGen**：新增 `TokenAllocator` pass，按确定性序分配 MethodId / TypeId / StaticFieldId；emit 时直接写 u32。
- **VM resolver 极大简化**：load 时不再 String→u32 解析，仅初始化 VCall/Field IC；intra-module IR 字段已经是 token，cross-zpkg 走 import_table → lazy_loader。
- **stdlib + golden 全部 regen**：6 个 zpkg + 140 个 .zbc。
- **可重现构建（Reproducible Build）作为硬约束**：相同源 + 相同 toolchain → byte-identical 二进制产物（同主机范围；跨平台留独立 spec）。

### Determinism Guarantee（核心要求）

| 维度 | 规则 |
|---|---|
| Token 分配 | MethodId 按 (namespace, class FQ name, method name, arity, param types) 字典序；TypeId 按 class FQ name 字典序；StaticFieldId 按 (declaring class FQ name, field name) 字典序；BuiltinId 沿用 BUILTINS 表静态序；FieldId 按声明源序 |
| String pool | 必须按 IrGen 源序 intern；emit 期所有 dictionary iteration 改 sorted iteration |
| Import table | 按 `(token_kind, name)` 字典序写入 |
| zpkg 文件级排序 | `modules` / `files` / `exports` 按 `source` 路径字典序，独立于 filesystem 枚举 |
| 删除 nondeterministic 信息 | 审计 `Random` / `Guid` / `DateTime.Now`；当前未发现，需写入 reproducibility test 兜底 |
| CI 验证策略 | 同主机双编译比对核心 stdlib（`z42.core`）每次 PR；release tag 跑全 stdlib + 全 golden |

## Scope

### Compiler — IR 层（C#，13 个文件）

| 文件 | 类型 | 说明 |
|---|---|---|
| `src/compiler/z42.IR/IrModule.cs` | MODIFY | Instr records `string` 字段改 `Token32` 包装的 newtype |
| `src/compiler/z42.IR/IrVerifier.cs` | MODIFY | 验证 token bounds |
| `src/compiler/z42.IR/IrPassManager.cs` | MODIFY | 注入 `TokenAllocator` pass |
| `src/compiler/z42.IR/ExportedTypes.cs` | MODIFY | 跟随 IrModule 字段类型变化 |
| `src/compiler/z42.IR/DependencyIndex.cs` | MODIFY | 跟随接口变化 |
| `src/compiler/z42.IR/TokenAllocator.cs` | NEW | 按确定性序分配 token；含 import_table 构建 |
| `src/compiler/z42.IR/BinaryFormat/Opcodes.cs` | MODIFY | 版本常量 1.0；IMPT 扩展语义 |
| `src/compiler/z42.IR/BinaryFormat/StringPool.cs` | MODIFY | 与 import_table 配合；emit 期 sorted |
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs` | MODIFY | 写 IMPT 扩展区段 + 头部版本 1.0 |
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.Instructions.cs` | MODIFY | 字段从 `pool.Idx(name)` 改 `token.Value` |
| `src/compiler/z42.IR/BinaryFormat/ZbcReader.cs` | MODIFY | 读 IMPT 扩展区段 |
| `src/compiler/z42.IR/BinaryFormat/ZbcReader.Instructions.cs` | MODIFY | 解码 token (u32) |
| `src/compiler/z42.IR/BinaryFormat/ZasmWriter.cs` | MODIFY | 文本输出按 token + 解引用名字 |

### Compiler — Semantics 层（C#，8 个文件）

| 文件 | 类型 | 说明 |
|---|---|---|
| `src/compiler/z42.Semantics/Codegen/IrGen.cs` | MODIFY | 调用 `TokenAllocator`；emit 期 sorted iteration |
| `src/compiler/z42.Semantics/Codegen/IrGen.Helpers.cs` | MODIFY | emit token 而非 string |
| `src/compiler/z42.Semantics/Codegen/IrGen.Functions.cs` | MODIFY | 同上 |
| `src/compiler/z42.Semantics/Codegen/IrGen.Classes.cs` | MODIFY | 同上 |
| `src/compiler/z42.Semantics/Codegen/IrGen.Generate.cs` | MODIFY | 同上 |
| `src/compiler/z42.Semantics/Codegen/IrGen.Tests.cs` | MODIFY | 测试同步 |
| `src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs` | MODIFY | 调用站点换用 token |
| `src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.Members.cs` | MODIFY | 同上 |

### Compiler — Tests（C#，5 个明确 + 2 NEW + 实施期可能扩展）

| 文件 | 类型 | 说明 |
|---|---|---|
| `src/compiler/z42.Tests/IrGenTests.cs` | MODIFY | TokenAllocator 单元 + 字段类型同步 |
| `src/compiler/z42.Tests/IrVerifierTests.cs` | MODIFY | 新 token bounds |
| `src/compiler/z42.Tests/GoldenTests.cs` | MODIFY | golden zbc 1.0 |
| `src/compiler/z42.Tests/IncrementalBuildTests.cs` | MODIFY | 增量构建复现验证 |
| `src/compiler/z42.Tests/CrossZpkgImplTests.cs` | MODIFY | import_table 跨 zpkg 用例 |
| `src/compiler/z42.Tests/TokenAllocatorTests.cs` | NEW | 分配确定性单元测试 |
| `src/compiler/z42.Tests/ReproducibilityTests.cs` | NEW | 双编译 byte-compare 集成测试 |

> 实施期发现额外测试文件需要修改 → **立即停下，回阶段 3 更新 Scope** 后再继续。

### VM — Metadata（Rust，13 个文件）

| 文件 | 类型 | 说明 |
|---|---|---|
| `src/runtime/src/metadata/bytecode.rs` | MODIFY | Instruction enum: `String` → `u32` newtype 包装 |
| `src/runtime/src/metadata/bytecode_serde.rs` | MODIFY | 字段类型同步 |
| `src/runtime/src/metadata/zbc_reader.rs` | MODIFY | 1.0 解码 + IMPT 扩展 |
| `src/runtime/src/metadata/formats.rs` | MODIFY | `ZBC_VERSION = [1, 0]` |
| `src/runtime/src/metadata/types.rs` | MODIFY | `type_registry` 改双结构 |
| `src/runtime/src/metadata/loader.rs` | MODIFY | 不再做 String→u32 |
| `src/runtime/src/metadata/loader_tests.rs` | MODIFY | 测试同步 |
| `src/runtime/src/metadata/merge.rs` | MODIFY | 合并 import_tables |
| `src/runtime/src/metadata/merge_tests.rs` | MODIFY | 测试同步 |
| `src/runtime/src/metadata/lazy_loader.rs` | MODIFY | 走 import_table |
| `src/runtime/src/metadata/lazy_loader_tests.rs` | MODIFY | 测试同步 |
| `src/runtime/src/metadata/resolver.rs` | MODIFY | 简化（仅初始化 IC） |
| `src/runtime/src/metadata/tokens.rs` | MODIFY | 加 `Token32` 统一 wrapper |

### VM — Interp / JIT / Corelib（Rust，13 个文件）

| 文件 | 类型 | 说明 |
|---|---|---|
| `src/runtime/src/interp/exec_instr.rs` | MODIFY | 直接读 token |
| `src/runtime/src/interp/exec_call.rs` | MODIFY | 同上 |
| `src/runtime/src/interp/exec_object.rs` | MODIFY | ObjNew 走 TypeId Vec 直查 |
| `src/runtime/src/interp/exec_vcall.rs` | MODIFY | IC 输入参数变化 |
| `src/runtime/src/jit/translate.rs` | MODIFY | codegen 移除 site_index 中介 |
| `src/runtime/src/jit/helpers/call.rs` | MODIFY | 简化 |
| `src/runtime/src/jit/helpers/object.rs` | MODIFY | ObjNew 走 TypeId |
| `src/runtime/src/jit/helpers/vcall.rs` | MODIFY | 简化 |
| `src/runtime/src/jit/helpers/registry.rs` | MODIFY | Cranelift 签名 |
| `src/runtime/src/jit/helpers/closure.rs` | MODIFY | LoadFn / MkClos 走 token |
| `src/runtime/src/jit/frame.rs` | MODIFY | 适配 |
| `src/runtime/src/jit/mod.rs` | MODIFY | 适配 |
| `src/runtime/src/corelib/mod.rs` | MODIFY | builtin_id_of 退化为简单查表 |

### VM — Tests（Rust，2 个明确 + 1 NEW + 实施期可能扩展）

| 文件 | 类型 | 说明 |
|---|---|---|
| `src/runtime/tests/zbc_compat.rs` | MODIFY | 1.0 round-trip |
| `src/runtime/src/metadata/constraint_tests.rs` | MODIFY | TypeRegistry 接口变更 |
| `src/runtime/tests/reproducibility_test.rs` | NEW | 双编译 byte-compare（与 C# 端对称） |

### Stdlib regen（artifacts，6 个 zpkg + 140 golden zbc）

| 路径 | 类型 | 说明 |
|---|---|---|
| `artifacts/libraries/z42.collections/` | MODIFY (regen) | 重新编译 |
| `artifacts/libraries/z42.core/` | MODIFY (regen) | 同上 |
| `artifacts/libraries/z42.io/` | MODIFY (regen) | 同上 |
| `artifacts/libraries/z42.math/` | MODIFY (regen) | 同上 |
| `artifacts/libraries/z42.test/` | MODIFY (regen) | 同上 |
| `artifacts/libraries/z42.text/` | MODIFY (regen) | 同上 |
| `src/tests/**/*.zbc`（140 文件） | MODIFY (regen) | `regen-golden-tests.sh` 跑全 |

### 文档（5 个文件）

| 文件 | 类型 | 说明 |
|---|---|---|
| `docs/design/ir.md` | MODIFY | IR 1.0 字段类型 + Token32 设计 |
| `docs/design/compilation.md` | MODIFY | zbc 1.0 格式 + IMPT 语义 |
| `docs/design/vm-architecture.md` | MODIFY | Phase 化路线 → 🟢，Phase 2.D 吸收说明 |
| `docs/roadmap.md` | MODIFY | 进度更新 |
| `.claude/rules/code-organization.md` | MODIFY | 新增 "Reproducible build 规则"段落 |

### 只读引用（理解上下文用，不修改）

- `spec/archive/2026-05-08-introduce-method-token/` — Phase 1 设计参照
- `spec/archive/2026-05-08-formalize-jit-method-token/` — Phase 2 设计参照
- `docs/design/language-overview.md` — 类型系统底层
- `scripts/regen-golden-tests.sh` — golden 重生脚本
- ECMA-335 §II — token 编码风格参照

## Out of Scope

- **B2 完整 CLR-style metadata-table 系统**（coded indices / 多 heap / TypeSpec / MethodSpec / ~40 张表）—— 用 B3 形态保留升级空间，需求出现时独立 spec
- **跨平台 byte-identical**（macOS-arm64 vs Linux-x64）—— 同主机优先，跨平台留独立 spec
- **Phase 4（compiler token-aware emit 优化）**—— 本 spec 只交付"能 emit token"，不动分配算法 perf
- **JIT 机器码 inline IC check（Option B）**—— 仍 helper-call
- **AOT 后端**—— 保持 stub
- **Reflection / dynamic metadata API**—— 不引入
- **PDB / 调试 metadata 系统化**—— 当前 line_table 够用
- **C# IR records 字段类型改 token**（2026-05-09 实施期裁决移入 Out of Scope）—— C# IR records 是编译期中间表示，不持久化。tokenization 仅在 wire format 边界（ZbcWriter v1.0 / ZbcReader v1.0 / Rust Instruction enum）落地。`TokenAllocator` 作为 IrGen 的 sibling output 接到 ZbcWriter 消费侧，C# IR records 字段类型保持 `string`。详见 design.md "IrModule 字段（C#）保持 String"。
- **IMPT 区段扩展**（2026-05-09 S3 重设计移入 Out of Scope）—— 旧设计要求 zbc IMPT section 改为 `(kind, name_str_idx)` entries 列表 + packed zpkg per-module IMPT。**新设计取消 IMPT 改造**：cross-zpkg 引用直接复用 STRS 池，token 用 `IMPORT_BASE` bit 区分本地 vs 跨包。详见 design.md Decision 2 (revised) + tasks.md S3 重设计说明。
- **Token 排序 / TIDX remap / sorted SIGS-FUNC 协调**（2026-05-09 S3 重设计移入 Out of Scope）—— 旧设计要求 TokenAllocator 按 Ordinal 序分配 ID + ZbcWriter 按 sorted 序输出 TYPE/SIGS/FUNC + TIDX 重映射 method_id。**新设计改用源序**（`module.Classes` / `module.Functions` 的索引直接作 token）—— 仍 deterministic from source（用户 reproducible build 要求 = 同源同构产）；改源即换 token 是接受的代价。

## Implementation history & WIP reference

- 2026-05-09 旧 S3 实施尝试 → broken state（VM tests 0/310）。原因：per-module IMPT + sort coordination + cross-module token resolution 三重叠加复杂度过高。WIP commit `833193a` 保存在分支 `wip/phase3-s3-broken`，作为反例参考归档。
- 2026-05-09 S3 redesign（IMPORT_BASE bit on STRS pool）：取消 IMPT 改造与 sort 协调，cross-zpkg refs 直接复用 STRS 池。三个独立 GREEN commit 步进（reader-then-writer-then-cleanup）。

## Open Questions

- [ ] **Token32 binary layout**：`#[repr(C)] u32` vs `(kind: u8, body: u24)` —— 留给 design.md 阶段决定，初始倾向纯 `u32`，高字节始终 0
- [ ] **IMPT 区段编码**：`(kind tag u8, name str_idx u32)` 二元组 vs 按 kind 拆 4 个表 —— 留给 design.md 决定，初始倾向二元组（更简洁）
- [ ] **回退机制**：本次大手术超过 2 周仍未收敛 → 回退到方案 B（仅 type_registry Vec restructure），把 IR/zbc 部分留给独立 spec。回退点设在 type_registry 切完之后、IR 字段切之前
