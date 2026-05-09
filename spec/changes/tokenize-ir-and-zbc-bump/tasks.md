# Tasks: tokenize-ir-and-zbc-bump

> 状态：🟡 进行中 | 创建：2026-05-09
> 类型：lang / ir / vm —— 完整流程
> 体量：60+ 文件，1-2 周；分 5 个 Stage 独立 commit

## 进度概览

- [ ] **S0**：审计 + Token32 骨架（~半天）
- [ ] **S1**：type_registry Vec restructure（~半天，回退点）
- [ ] **S2**：TokenAllocator + IR records 迁移（C# 端，~2 天）
- [ ] **S3**：zbc 1.0 格式 bump（C# + Rust 同步，~2 天）
- [ ] **S4**：VM 加载路径切换 + stdlib regen（~3 天）
- [ ] **S5**：Reproducibility tests + 文档同步 + 归档（~半天）

---

## S0 — 审计 + Token32 骨架

### 0.1 审计（不改代码，只产出 task 备注）
- [x] 0.1.1 C# emit 路径审计：见"备注"——只有 `ZbcWriter.cs:387 imports HashSet` 一处需在 S3.1.2 加 sort
- [x] 0.1.2 Rust serialize 路径审计：见"备注"——`Module` 中 HashMap 字段标 `#[serde(skip)]`，bincode 路径全 Vec / scalar，无问题
- [x] 0.1.3 emit 路径无 Random/Time/Guid 调用，只有测试 + 运行期 stdlib 用

### 0.2 Token32 newtype（C# 端）
- [ ] 0.2.1 在 `src/compiler/z42.IR/` 新增 `Tokens.cs`：定义 `MethodId` / `TypeId` / `StaticFieldId` / `BuiltinId` 四个 `readonly record struct`，含 `Unresolved` / `ImportBase` 常量 + `IsResolved` / `IsImport` / `ImportIdx` 属性
- [ ] 0.2.2 加 xUnit 单测 `TokensTests.cs`：边界值 (0 / `ImportBase` / `Unresolved` / 高字节断言)

### 0.3 Token32 newtype（Rust 端，扩展现有）
- [ ] 0.3.1 `src/runtime/src/metadata/tokens.rs` 加 `IMPORT_BASE` 常量 + `is_import` / `import_idx` 方法（`MethodId` / `TypeId` / `StaticFieldId` / `BuiltinId` 各加）
- [ ] 0.3.2 `tokens.rs` 末尾加 `#[cfg(test)] mod tokens_tests;`
- [ ] 0.3.3 新建 `src/runtime/src/metadata/tokens_tests.rs`：边界单测

### 0.4 验证 + commit
- [ ] 0.4.1 `cargo build` + `cargo test` 全绿
- [ ] 0.4.2 `dotnet build` + `dotnet test` 全绿
- [ ] 0.4.3 commit: `feat(tokens): S0 — Token32 wrapper + ImportBase 常量 (Phase 3)`

---

## S1 — type_registry Vec restructure（**回退点**）

### 1.1 数据结构改造
- [ ] 1.1.1 `src/runtime/src/metadata/types.rs`：`Module` 字段从 `pub type_registry: HashMap<String, Arc<TypeDesc>>` 改 `pub type_registry_vec: Vec<Arc<TypeDesc>>` + `pub type_registry_by_name: HashMap<String, TypeId>`（均 `#[serde(skip)]`）
- [ ] 1.1.2 加 `Module::type_by_id(TypeId)` / `type_by_name(&str)` / `register_lazy_type(Arc<TypeDesc>) -> TypeId` 三个 API

### 1.2 加载路径调整
- [ ] 1.2.1 `metadata/loader.rs::build_type_registry`：按 classes 顺序填 vec + name_map
- [ ] 1.2.2 `metadata/lazy_loader.rs`：`try_lookup_type` 调用 `register_lazy_type` 接到 vec 末尾
- [ ] 1.2.3 `metadata/merge.rs`：merge 时合并双结构（重 mapping TypeId 到 global 空间）

### 1.3 消费侧适配（同步 ObjNew TypeId 直查）
- [ ] 1.3.1 `interp/exec_object.rs::obj_new`：先尝试 `type_by_id(TypeId)`，miss → `type_by_name`
- [ ] 1.3.2 `interp/dispatch.rs::is_subclass_or_eq_td`：跟随 `type_registry` 接口名变化
- [ ] 1.3.3 `corelib/gc.rs` 等其他 `type_registry` 用户跟随
- [ ] 1.3.4 `jit/helpers/object.rs::jit_obj_new`：仍走 `module.type_registry` 字段（用别名 alias 维持兼容；JIT 的 TypeId 直查在 S4）

### 1.4 测试同步
- [ ] 1.4.1 `metadata/loader_tests.rs`：测试双结构构建
- [ ] 1.4.2 `metadata/lazy_loader_tests.rs`：测试 lazy register
- [ ] 1.4.3 `metadata/merge_tests.rs`：测试 merge 重 mapping
- [ ] 1.4.4 `metadata/constraint_tests.rs`：跟随接口

### 1.5 验证 + commit（回退点）
- [ ] 1.5.1 `cargo test` 全绿
- [ ] 1.5.2 `./scripts/test-vm.sh` 310/310 全绿
- [ ] 1.5.3 commit: `refactor(vm): S1 — type_registry Vec-by-TypeId restructure (Phase 3, absorbs Phase 2.D)`
- [ ] 1.5.4 push（这个 commit 即使回退方案也保留）

---

## S2 — TokenAllocator + IR records 迁移（C# 端）

### 2.1 IR records 字段类型迁移
- [ ] 2.1.1 `IrModule.cs::CallInstr`：`string Func` → `MethodId Func`
- [ ] 2.1.2 `IrModule.cs::BuiltinInstr`：`string Name` → `BuiltinId Name`
- [ ] 2.1.3 `IrModule.cs::ObjNewInstr`：`ClassName/CtorName` → `TypeId ClassId / MethodId CtorId`；`TypeArgs` 元素 → `TypeId`
- [ ] 2.1.4 `IrModule.cs::IsInstanceInstr / AsCastInstr`：`ClassName` → `TypeId ClassId`
- [ ] 2.1.5 `IrModule.cs::StaticGetInstr / StaticSetInstr`：`Field` → `StaticFieldId Field`
- [ ] 2.1.6 `IrModule.cs::LoadFnInstr / LoadFnCachedInstr / MkClosInstr`：`Func / FuncName` → `MethodId`
- [ ] 2.1.7 `IrModule.cs::FieldGetInstr / FieldSetInstr / LoadFieldAddrInstr`：`FieldName` 保持 `string`（receiver-type-dependent）
- [ ] 2.1.8 `IrModule.cs::VCallInstr`：`Method` 保持 `string`（同上）
- [ ] 2.1.9 `ExportedTypes.cs / DependencyIndex.cs`：跟随接口

### 2.2 TokenAllocator 实现
- [ ] 2.2.1 新建 `src/compiler/z42.IR/TokenAllocator.cs`：按 (FQ class, method, arity, params) 分配 MethodId；按 FQ class 分配 TypeId；按 (class, field) 分配 StaticFieldId
- [ ] 2.2.2 收集 cross-zpkg refs 进 import_table；`IMPORT_BASE + idx` 分配
- [ ] 2.2.3 提供 `Resolve(string fqName, TokenKind kind) -> uint` API 给 IrGen 用

### 2.3 IrGen 接入
- [ ] 2.3.1 `IrPassManager.cs`：在 IrGen 之前跑 TokenAllocator pass
- [ ] 2.3.2 `IrGen.cs / IrGen.Helpers.cs / IrGen.Functions.cs / IrGen.Classes.cs / IrGen.Generate.cs / IrGen.Tests.cs`：emit 时调 `allocator.Resolve(name)` 拿 token，写入 IR 字段
- [ ] 2.3.3 `FunctionEmitterExprs.cs / .Members.cs`：调用站点换 token

### 2.4 IrVerifier + ZasmWriter
- [ ] 2.4.1 `IrVerifier.cs`：验证 token bounds（intra-module < `IMPORT_BASE` ∪ `[IMPORT_BASE, UNRESOLVED)`）
- [ ] 2.4.2 `ZasmWriter.cs`：文本输出 `<token>:<resolved-name>` 形式（解引用 import_table / functions / classes）

### 2.5 emit 期 sorted iteration（reproducibility）
- [ ] 2.5.1 根据 0.1.1 审计结果，把 IrGen 内所有 Dictionary 迭代改 `OrderBy`
- [ ] 2.5.2 StringPool 的 emit 路径检查无 nondeterministic 排序

### 2.6 测试
- [ ] 2.6.1 新建 `TokenAllocatorTests.cs`：deterministic 分配 fixed-sample 验证
- [ ] 2.6.2 `IrGenTests.cs / IrVerifierTests.cs / IncrementalBuildTests.cs / CrossZpkgImplTests.cs`：跟随接口
- [ ] 2.6.3 `GoldenTests.cs`：暂时 skip（zbc 1.0 在 S3）

### 2.7 验证 + commit
- [ ] 2.7.1 `dotnet build` + `dotnet test`（除 zbc round-trip 类）全绿
- [ ] 2.7.2 commit: `feat(compiler): S2 — TokenAllocator + IR records token migration (Phase 3)`

---

## S3 — zbc 1.0 格式 bump（C# + Rust 同步）

### 3.1 C# 端 ZbcWriter
- [ ] 3.1.1 `Opcodes.cs`：版本常量 1.0；IMPT 区段语义注释更新
- [ ] 3.1.2 `ZbcWriter.cs`：写头部版本 1.0；IMPT 扩展为 `[(kind: u8, name_str_idx: u32)...]`，按字典序排
- [ ] 3.1.3 `ZbcWriter.Instructions.cs`：所有 `pool.Idx(name)` 改 `token.Value`
- [ ] 3.1.4 `StringPool.cs`：emit-time 排序检查（按 intern 顺序，不动）

### 3.2 C# 端 ZbcReader
- [ ] 3.2.1 `ZbcReader.cs`：读 IMPT 扩展区段；version mismatch 报错
- [ ] 3.2.2 `ZbcReader.Instructions.cs`：解码 token (u32)

### 3.3 Rust 端 zbc_reader
- [ ] 3.3.1 `metadata/formats.rs`：`ZBC_VERSION = [1, 0]`；老版加载报错信息更新
- [ ] 3.3.2 `metadata/bytecode.rs`：`Instruction` enum 字段类型 String → newtype（与 C# 端对称）
- [ ] 3.3.3 `metadata/bytecode_serde.rs`：字段 serde 同步
- [ ] 3.3.4 `metadata/bytecode.rs`：加 `ImportTable` / `ImportEntry` / `TokenKind` 类型
- [ ] 3.3.5 `metadata/zbc_reader.rs`：1.0 解码 + IMPT 扩展

### 3.4 round-trip 测试
- [ ] 3.4.1 C# `ZbcRoundTripTests` 跑通（如已存在），否则补单元测试
- [ ] 3.4.2 Rust `tests/zbc_compat.rs`：1.0 round-trip

### 3.5 验证 + commit
- [ ] 3.5.1 `dotnet build` + `cargo build` 全绿
- [ ] 3.5.2 `dotnet test` + `cargo test`（VM golden 暂未跑因为 stdlib 还是 0.9） 全绿
- [ ] 3.5.3 commit: `feat(zbc): S3 — zbc 1.0 format bump + IMPT extension (Phase 3)`

---

## S4 — VM 加载路径切换 + stdlib regen

### 4.1 加载路径
- [ ] 4.1.1 `metadata/loader.rs`：移除 String→u32 解析（IR 已是 token）
- [ ] 4.1.2 `metadata/merge.rs`：合并 import_tables + token remap
- [ ] 4.1.3 `metadata/lazy_loader.rs`：走 import_table → 名字 → 解析 → 回写 IR 字段 token
- [ ] 4.1.4 `metadata/resolver.rs`：删除 method_tokens / builtin_tokens / type_tokens / static_field_tokens / site_index；只保留 vcall_ic / field_ic 初始化

### 4.2 Interp 简化
- [ ] 4.2.1 `interp/exec_instr.rs`：移除 `_site_idx` 参数；直接读 IR 字段 token
- [ ] 4.2.2 `interp/exec_call.rs::call`：token 直查 module.functions，import token 走 import_table
- [ ] 4.2.3 `interp/exec_call.rs::builtin`：BuiltinId 直查 BUILTINS
- [ ] 4.2.4 `interp/exec_object.rs::obj_new`：TypeId 直查 type_registry_vec；ctor MethodId 同理
- [ ] 4.2.5 `interp/exec_object.rs::is_instance / as_cast`：TypeId 直接走
- [ ] 4.2.6 `interp/exec_object.rs::static_get / static_set`：StaticFieldId 直查
- [ ] 4.2.7 `interp/exec_vcall.rs`：IC 输入参数变化（method 仍 string）

### 4.3 JIT 简化
- [ ] 4.3.1 `jit/translate.rs`：移除 method_id_at / static_field_id_at / vcall_ic_ptr_at / field_ic_ptr_at 的 site_index 中介；直接从 IR 字段读 token；emit `iconst.i32 <token.0>`
- [ ] 4.3.2 `jit/helpers/call.rs::jit_call`：删除 name fallback 入参（intra 直接 token，import 走 import_table 由 helper 处理）
- [ ] 4.3.3 `jit/helpers/object.rs::jit_obj_new`：TypeId / MethodId 直接入参
- [ ] 4.3.4 `jit/helpers/object.rs::jit_static_get/set`：（已 token 化，跟随接口微调）
- [ ] 4.3.5 `jit/helpers/vcall.rs`：method 仍 string，IC 输入跟随
- [ ] 4.3.6 `jit/helpers/closure.rs::jit_load_fn / jit_mk_clos`：MethodId 入参
- [ ] 4.3.7 `jit/helpers/registry.rs`：所有签名同步
- [ ] 4.3.8 `jit/frame.rs / jit/mod.rs`：fn_entries_by_id 适配（入参可能从 i32 改 token）
- [ ] 4.3.9 `corelib/mod.rs`：`builtin_id_of` 退化或保留作 import_table 名→id 反查

### 4.4 stdlib regen
- [ ] 4.4.1 `dotnet build src/compiler/z42.slnx -c Debug --no-incremental`
- [ ] 4.4.2 重编 6 个 stdlib zpkg：`./scripts/build-stdlib.sh`（或对应脚本）
- [ ] 4.4.3 重编 140 个 golden zbc：`./scripts/regen-golden-tests.sh`

### 4.5 验证 + commit
- [ ] 4.5.1 `cargo test` 全绿
- [ ] 4.5.2 `dotnet test` 全绿
- [ ] 4.5.3 `./scripts/test-vm.sh` 310/310 全绿
- [ ] 4.5.4 commit: `feat(vm+jit): S4 — token-based dispatch + stdlib 1.0 regen (Phase 3)`

---

## S5 — Reproducibility + 文档同步 + 归档

### 5.1 Reproducibility 测试
- [ ] 5.1.1 新建 `src/compiler/z42.Tests/ReproducibilityTests.cs`：双编译 z42.core + 任一 examples → byte-compare
- [ ] 5.1.2 新建 `src/runtime/tests/reproducibility_test.rs`：与 C# 端对称（cargo 跑）

### 5.2 CI gate
- [ ] 5.2.1 检查项目是否有 CI 配置（`.github/workflows/` 或类似）；若有，加 reproducibility job；若无，记录到 deferred backlog

### 5.3 文档同步
- [ ] 5.3.1 `docs/design/ir.md`：IR 1.0 字段类型 + Token32 设计 + token kind 表
- [ ] 5.3.2 `docs/design/compilation.md`：zbc 1.0 格式 + IMPT 语义 + magic threshold 编码
- [ ] 5.3.3 `docs/design/vm-architecture.md`：Phase 化路线表 → Phase 3 🟢；Phase 2.D 吸收说明
- [ ] 5.3.4 `docs/roadmap.md`：进度更新
- [ ] 5.3.5 `.claude/rules/code-organization.md`：新增 "Reproducible build 规则"段落

### 5.4 归档
- [ ] 5.4.1 本 tasks.md 状态 `🟡 → 🟢 已完成`；填入完成日期
- [ ] 5.4.2 `git mv spec/changes/tokenize-ir-and-zbc-bump → spec/archive/2026-XX-XX-tokenize-ir-and-zbc-bump`
- [ ] 5.4.3 commit: `docs+spec: S5 — Phase 3 reproducibility tests + docs sync + archive (Phase 3)`
- [ ] 5.4.4 push

---

## 备注 / 实施期发现

（实施期遇到的 Scope 越界、新风险、决策变更记入此处）

### S0 顺手修复（2026-05-09）

发现 pre-existing 失败：`src/runtime/tests/zbc_compat.rs::hello_zbc_structure` 与 `::hello_load_artifact_full_path` 期望 `src/tests/basic/hello/source.zbc` 存在，但该路径在 commits `6fc6ccb` / `100c4f7` / `02a19bc`（Phase 2.E 之后、本 Phase 3 之前的 test refactor）中被删除（`basic/hello/` → 单文件 `basic/hello.z42`）。User 选 A：S0 commit 顺手清理这两个 stale 测试，删除 unused `load_artifact` import。`all_golden_zbc_decode` 仍是 backstop。Scope 表 `zbc_compat.rs` 已在 S3.4.2 列出，本次提前接触；不算越界。

### S0.1 审计结果（2026-05-09）

**0.1.1 C# emit 路径** — 1 处需修：

| 文件:行 | 问题 | 修复点 |
|---|---|---|
| `ZbcWriter.cs:387, 404` | `imports = new HashSet<string>(StringComparer.Ordinal)`（`:387`），后续 `foreach (var imp in imports)`（`:404`）写入 IMPT 区段 → hash-ordered 不确定 | S3.1.2：`imports.OrderBy(x => x, StringComparer.Ordinal)` 后写入 |

其他 `Dictionary` / `HashSet` 用法均为查找用途（`Contains` / 索引 lookup），不参与 emit 顺序。

`StringPool._index: Dictionary<string, int>` 仅作 intern 反查；STRS 区段写入是按 intern 序（即 IrGen visit 序），确定。

**0.1.2 Rust serialize** — 无问题：
- `Module.type_registry: HashMap` 标 `#[serde(skip)]`，不写入 zbc
- `Module.func_index: HashMap` 标 `#[serde(skip)]`，同上
- 其余 `Module` 字段均 `Vec<T>` / scalar；bincode 顺序由 Vec 元素序保证

**0.1.3 时间 / 随机源** — emit 路径全部干净：
- `Random` / `Guid.NewGuid` / `Path.GetRandomFileName`：仅 `src/compiler/z42.Tests/*` 测试 scaffold，不影响 zbc
- `Instant::now` / `SystemTime::now`：`src/runtime/src/corelib/{bench,fs}.rs` + `src/runtime/src/gc/rc_heap.rs` + `src/toolchain/test-runner/`，全部运行期，不写入 zbc
- `Std.Math.Random`：z42 stdlib 类，跟编译期 determinism 无关

---

## 回退条件 + 动作（design.md Decision 6）

**触发条件**（Stage 3-4 任一发生）：
1. 任一 Stage 工作量超出估算 1.5x → 停下评估
2. 某个核心架构决策需要重新讨论 → 停下评估
3. pre-existing 测试出现非平凡回归 → 停下修复

**动作**：
1. S0/S1 成果保留（已 commit）
2. S2-S5 工作回滚到 WIP 分支
3. 把 S1 单独归档为 `restructure-type-registry-by-typeid` spec
4. 重新规划余下 S2-S5 拆分成更小的独立 spec
