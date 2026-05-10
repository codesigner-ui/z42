# Tasks: tokenize-ir-and-zbc-bump

> 状态：🟢 已完成 | 创建：2026-05-09 | 完成：2026-05-09
> 类型：lang / ir / vm —— 完整流程
> 体量：60+ 文件，1-2 周；分 5 个 Stage 独立 commit

## 进度概览

- [x] **S0**：审计 + Token32 骨架（commit `626beb8`）
- [x] **S1**：type_registry Vec restructure（commit `58f17b0`，**回退点**）
- [x] **S2 step 1**：TokenAllocator standalone（commit `3306659`）
- [x] **S2 step 2**：IrGen sibling output（commit `dca32ee`）
- [ ] **S3a**：Rust ZbcReader 双版本支持（v0.9 + v1.0）
- [ ] **S3b**：C# ZbcWriter/Reader 切 v1.0 + stdlib + golden regen
- [ ] **S3c**：清理 v0.9 fallback
- [ ] **S5**：Reproducibility tests + 文档同步 + 归档

> **2026-05-09 redesign**：原 S3 (per-module IMPT + sort 协调) 失败 → WIP `wip/phase3-s3-broken` (commit `833193a`)。
> 重设计后取消 IMPT 改造和 sort 协调，cross-zpkg 引用直接复用 STRS 池
> (`IMPORT_BASE + str_idx`)。原 S4 (Rust IR enum 字段改 token) 移入 Out of Scope。
> 新 S3a/b/c 三步替代旧 S3+S4，每步独立 GREEN。

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

## S2 — TokenAllocator standalone + IrGen sibling output（C# 端）

> **2026-05-09 实施期裁决（option β）**：原计划 IR records 字段类型 `string → token`
> 移入 Out of Scope。改为 TokenAllocator 作为 IrGen 的 sibling output 接到 ZbcWriter
> 消费侧。详见 spec.md / design.md / proposal.md 的对应说明。
> S2 拆成 step 1 / step 2 两个独立 commit。

### 2.0 step 1 — TokenAllocator standalone（已完成 commit `3306659`）
- [x] 2.0.1 `TokenAllocator.cs` (NEW)：register / DiscoverImport / Build / Resolve* + ImportTable 排序
- [x] 2.0.2 `ImportEntry` record (NEW)：(kind, name) 二元组
- [x] 2.0.3 `TokenAllocatorTests.cs` (NEW)：13 个单测覆盖 determinism / 排序 / lifecycle / import 空间

### 2.1 step 2 — IrGen sibling output
- [ ] 2.1.1 `IrGen.Generate.cs` / `IrGen.cs`：完成 IR module emit 后跑 TokenAllocator
  - Pass 1：注册 intra-module classes (`IrModule.Classes`) + methods (`IrModule.Functions.Name`) + 静态字段（扫描 IR 中所有 `StaticGetInstr.Field` / `StaticSetInstr.Field` 收集）
  - Pass 2：扫描 IR 中所有 cross-zpkg refs（`CallInstr.Func` 不属于本模块的；`ObjNewInstr.ClassName` / `IsInstanceInstr` / `AsCastInstr` 不属于本模块的；等）→ DiscoverImport
  - Build()
- [ ] 2.1.2 改 `IrGen` 公开返回类型：`(IrModule, TokenAllocator)` 或新 wrapper class，让下游消费者拿到 allocator
- [ ] 2.1.3 IrModule 不动；C# IR records 字段类型不动（裁决保持 String）

### 2.2 IrVerifier 增强（可选 / 推迟）
- [ ] 2.2.1 `IrVerifier.cs`：暂不改；token bounds 验证留到 S3（ZbcWriter 验证更直接）

### 2.3 测试
- [ ] 2.3.1 新增 `IrGenTokenAllocatorIntegrationTests.cs`（或挂在 `IrGenTests.cs`）：IrGen 输出的 allocator 含完整 decls + import_table

### 2.4 验证 + commit
- [ ] 2.4.1 `dotnet build` + `dotnet test` 全绿
- [ ] 2.4.2 commit: `feat(compiler): S2 step 2 — IrGen produces TokenAllocator alongside IrModule`

### 推迟到 S3 的项（原 S2 内容）
- ZbcWriter wire format 改 v1.0 + IMPT 区段 → S3
- ZbcReader 解码 v1.0 → S3
- `Opcodes.cs` 版本常量 → S3
- emit 期 sorted iteration（IMPT HashSet 修复）→ S3
- `ZasmWriter.cs` token 输出 → S3

---

## S3 — zbc 1.0 wire format（**2026-05-09 redesign**）

> 旧 S3 (per-module IMPT + sort 协调 + cross-module token 解析) 在
> `wip/phase3-s3-broken` (commit `833193a`) 失败。重设计后取消 IMPT 扩展 +
> 取消 sort 协调；cross-zpkg ref 直接复用 STRS 池 (`IMPORT_BASE + str_idx`)。
> S4 / S5 (Rust IR enum 字段改 token / VM 加载路径切换) 统统**移入 Out of Scope**——
> Rust 端 IR 字段保持 String，token 仅在 wire 边界进行编解码。

### S3a — Rust ZbcReader 双版本支持（unchanged main）

> **目标**：runtime 兼容 v0.9 + v1.0 同时；C# 端不变；现有 stdlib (v0.9) 继续工作。

- [ ] 3a.1 `metadata/formats.rs`：保留 `ZBC_VERSION = [0, 9]` 不动（writer 仍是 0.9 来源）；新增常量 `ZBC_MAX_SUPPORTED = [1, 0]` 注释 reader 接受范围
- [ ] 3a.2 `metadata/zbc_reader.rs::read_zbc`：读 major.minor 后判断分支；major == 0 → 走旧路径；major == 1 → 走新路径（token decoder）
- [ ] 3a.3 `metadata/zbc_reader.rs`：实现 `decode_token_to_name(token, pool, local_funcs_or_classes) -> String`：
  - `token < IMPORT_BASE` → 取 `local_table[token]`
  - `token >= IMPORT_BASE` → 取 `pool[token - IMPORT_BASE]`
  - `token == UNRESOLVED` → "<unresolved>"
- [ ] 3a.4 `decode_instr` v1.0 分支：tokenizable opcodes (`Call/LoadFn/LoadFnCached/MkClos/ObjNew/IsInstance/AsCast`) 用新 decoder；其他不变
- [ ] 3a.5 `read_func` 接受 `local_funcs: &[String]` + `local_classes: &[String]` 参数，从 sigs / classes 提供
- [ ] 3a.6 packed-zpkg path 同步：`read_mods_section` 给 `read_func` 传 per-module local_funcs / local_classes（per-module 是 sigs slice + classes vec）
- [ ] 3a.7 `tests/zbc_compat.rs` 加 v1.0 round-trip 测试（用 v1.0 hand-crafted bytes）
- [ ] 3a.8 验证：现有 cargo test (260+) + VM golden (310/310) 全绿（reader 改动对 v0.9 输入零行为变化）
- [ ] 3a.9 commit: `feat(vm): tokenize-ir-and-zbc-bump S3a — Rust ZbcReader supports both v0.9 and v1.0 (forward compat for compiler switch)`

### S3b — C# ZbcWriter / ZbcReader 切换 v1.0 + 全 regen

> **目标**：编译器默认产 v1.0；stdlib + golden 全部 regen；测试全绿。Rust 已能读 v1.0 (S3a)。

- [ ] 3b.1 `Tokens.cs`：保留 newtype 但简化（去掉 ImportTable / DiscoverImport / 排序）
- [ ] 3b.2 `TokenAllocator.cs`：rewrite — 收 `module.Functions` / `module.Classes` 索引为 token；Resolve 时本地 → 索引，否则 → `IMPORT_BASE + pool.Idx(name)`
- [ ] 3b.3 `TokenAllocatorTests.cs`：rewrite 反映新语义（源序、IMPORT_BASE 编码 cross-zpkg）
- [ ] 3b.4 `ZbcWriter.cs`：版本常量 → `[1, 0]`；不动 IMPT 区段格式
- [ ] 3b.5 `ZbcWriter.Instructions.cs`：tokenizable 字段 emit u32（`allocator.ResolveX(name).Value`），其他保持 `pool.Idx(...)`
- [ ] 3b.6 `ZbcReader.cs` + `ZbcReader.Instructions.cs`：增加 v1.0 分支 + token decoder（与 Rust 对称）；同时仍能读 v0.9 直至 S3c 清理
- [ ] 3b.7 `ZpkgWriter.Sections.cs::BuildModsSection`：每个内嵌 module 用同一 TokenAllocator 模式编 token；不需要 per-module IMPT 改造
- [ ] 3b.8 `ZpkgReader.Sections.cs`：packed module decode 给 token decoder 提供 local_funcs (sigs slice) + local_classes (typeData decoded)
- [ ] 3b.9 `IrGen.Generate.cs`：`_allocator = TokenAllocator.FromModule(module)` 已存在；不动逻辑
- [ ] 3b.10 dotnet build 全绿
- [ ] 3b.11 `./scripts/build-stdlib.sh` 重编 6 个 stdlib zpkg → v1.0
- [ ] 3b.12 `./scripts/regen-golden-tests.sh` 重编 140 golden zbc → v1.0
- [ ] 3b.13 dotnet test 全绿（含 round-trip）
- [ ] 3b.14 cargo test 全绿
- [ ] 3b.15 `./scripts/test-vm.sh` 全绿
- [ ] 3b.16 commit: `feat(zbc): tokenize-ir-and-zbc-bump S3b — switch to zbc 1.0 wire format + stdlib + golden regen`

### S3c — 清理 v0.9 fallback（pre-1.0 不留兼容）

- [ ] 3c.1 Rust `metadata/zbc_reader.rs::read_zbc`：major == 0 时直接 `bail!("zbc 1.0+ required, got 0.{minor}")`；删除 v0.9 instr decoder 分支（如有）
- [ ] 3c.2 Rust `metadata/formats.rs`：`ZBC_VERSION = [1, 0]`（writer 不存在但常量更新作 reader 期望）
- [ ] 3c.3 C# `ZbcReader.cs`：major == 0 时抛 `InvalidDataException("zbc 1.0+ required")`
- [ ] 3c.4 dotnet test + cargo test + VM golden 全绿
- [ ] 3c.5 commit: `chore(zbc): tokenize-ir-and-zbc-bump S3c — drop v0.x fallback (CLAUDE.md 不为旧版本提供兼容)`

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
- [ ] 5.4.2 `git mv docs/spec/changes/tokenize-ir-and-zbc-bump → docs/spec/archive/2026-XX-XX-tokenize-ir-and-zbc-bump`
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
