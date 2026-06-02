# Tasks: JIT↔VM trait abstraction Phase 1 (E1.P2)

> 状态：🟢 已完成 | 创建：2026-06-02 | 完成：2026-06-02 | 类型：refactor（contract introduction，行为不变）
> 来源：[`docs/review.md`](../../../review.md) Part 1 P0 / Part 5 E1.P2

## 变更说明

新建 `pub trait JitVm` 作为 JIT path 对 metadata 模块的**只读契约**。`Module`
实现该 trait。`compile_module` + 一个示范 helper（`jit_obj_new`）从直接
`module.field` 访问改走 trait 方法。其他 helpers 在 **Phase 2** 独立 spec 完成。

## 原因 + scope 真实性说明

review.md Part 1 P0 的原描述（"translate.rs 只 take `&dyn VmInterface`，不
import metadata 内部类型；helpers/* 同样改成走 trait；AOT 路径也走同一个
trait"）是**过度乐观**：

1. `translate.rs` 必须对 `Instruction` enum 做 100+ arm 模式匹配 —— IR 类型
   是 JIT 的输入语言，无法藏在 trait 后
2. helpers/* 通过 `(*ctx).module` raw pointer 访问 Module（extern "C" ABI 必要）。
   raw pointer 必须指向**具体类型**，把 `*const Module` 改成 `*const dyn JitVm`
   是 fat pointer，破坏 ABI

实际可做的 Phase 1：

- **定义契约**：`trait JitVm` 暴露 compile-time 期 JIT 需要的 metadata 查询
- **`compile_module` 改走 trait 方法**：5-6 处 `module.functions` /
  `module.string_pool` / `module.name` 改 `module.functions()` 等
- **一个 helper exemplar**：`jit_obj_new` (helpers/object.rs) 改走 trait
- **不动 compile_module signature**：仍 take `&Module`；raw pointer 存储仍是
  `*const Module`。Phase 2（独立 spec）再讨论是否变 generic / type-erased

Phase 1 不改外部行为；纯契约引入 + 一个 helper 示范。Phase 2 = 余下 9 个
helper 全部迁移 + 可能的 compile_module signature 改 generic。

## 文档影响

- `docs/design/runtime/vm-architecture.md` 加 JitVm trait 节
- `docs/review.md` Part 1 P0 / E1.P2 改 🟡 Phase 1 done

## Scope（允许改动的文件）

| 文件 | 变更类型 | 说明 |
|---|---|---|
| `src/runtime/src/jit/vm_interface.rs` | NEW | `pub trait JitVm` 定义 |
| `src/runtime/src/jit/vm_interface_tests.rs` | NEW | trait method 单元测试 + 最小 mock 演示 |
| `src/runtime/src/jit/mod.rs` | MODIFY | `pub(crate) mod vm_interface;`；`compile_module` 内部改走 trait 方法（signature 不变） |
| `src/runtime/src/metadata/bytecode.rs` | MODIFY | `impl JitVm for Module` |
| `src/runtime/src/jit/helpers/object.rs` | MODIFY | `jit_obj_new` exemplar 改走 trait |
| `docs/design/runtime/vm-architecture.md` | MODIFY | 加 JitVm trait section |
| `docs/review.md` | MODIFY | E1.P2 / Part 1 P0 标 🟡 Phase 1 done |

只读引用：
- `src/runtime/src/jit/translate.rs` — 不动（IR 形状直接处理）
- 其他 helpers/*.rs — Phase 2

## 设计要点

### `JitVm` trait 初版 surface

```rust
pub trait JitVm {
    /// All functions declared in this module, in declaration order.
    fn functions(&self) -> &[Function];

    /// String pool — shared across the module for `Instruction::ConstStr`,
    /// names, debug info.
    fn string_pool(&self) -> &[String];

    /// FQ module name (e.g. `"Demo.App"`).
    fn module_name(&self) -> &str;

    /// Look up a `TypeDesc` by FQ class name. Returns `None` if not found
    /// in the local module; cross-zpkg lookups must use `LazyLoader`
    /// (Phase 2 will expose that too).
    fn type_lookup(&self, class_name: &str) -> Option<&std::sync::Arc<TypeDesc>>;
}
```

### 为什么 Phase 1 **不** 改 compile_module signature

`JitModuleCtx.module: *const Module` 这个 raw pointer 必须是具体 `Module*`
因为：
1. helpers 用 `(*ctx).module` 做 fat-pointer 不友好的 raw deref
2. extern "C" ABI 要求 sized pointer

Phase 2 需要先解决 "helpers 用 trait 调用 vs raw pointer 兼容" 问题
（候选：generic compile_module + type-erased dispatch table；或单独的
runtime trait `JitRuntimeVm`，与 Phase 1 的 compile-time trait 分离）。

## 任务

- [x] 0.1 NEW `docs/spec/changes/add-jit-vm-trait/tasks.md`
- [x] 1.1 NEW `jit/vm_interface.rs` 定义 `trait JitVm` + `impl JitVm for Module`（同文件）
- [x] 1.2 ~~MODIFY `metadata/bytecode.rs`~~ → impl 移到 vm_interface.rs 同文件（更清爽，单 4-method impl 不值得跨文件）
- [x] 1.3 MODIFY `jit/mod.rs` `pub(crate) mod vm_interface;` + `compile_module` 内部 4 处 reads 改 trait 调用 + `jit::run` 的 `module.name.clone()` 改 `module.module_name().to_string()`
- [x] 1.4 MODIFY `jit/helpers/object.rs::jit_obj_new` `module.type_registry.get(name)` → `module.type_lookup(name)` 示范
- [x] 1.5 NEW `jit/vm_interface_tests.rs` 7 个测试（4 个 Module impl forward + 1 个 MockMetadata 通过 dyn 调用 + 2 个边界）
- [x] 1.6 VERIFY `cargo build --release` clean + `cargo test --lib jit::vm_interface` 7/7 pass + `cargo test --lib` 21/21 pass + `dotnet test (--filter exclude IncrementalBuildIntegrationTests)` 1453/1453 pass
- [x] 1.7 VERIFY `./scripts/test-all.sh` — 2 个 IncrementalBuildIntegrationTests fail，**根因**：working tree 里有 parallel session 的未 commit `DateTimeOffset.z42` WIP（type 错误 "expected TimeZone, got TimeZone"），stdlib 增量构建测试在本地 FS 上跑时撞上它。CI 在 clean checkout 上跑不会触发。**非本 spec 引入**。
- [x] 1.8 MODIFY `docs/design/runtime/vm-architecture.md` 加 JitVm 节（含 Phase 1 vs Phase 2 路径 + Mockability ROI）
- [x] 1.9 MODIFY `docs/review.md` 标 🟡 Phase 1 done
- [x] 1.10 归档 + commit + push

## 备注

### Pre-existing 失败（非本 spec 引入）

`./scripts/test-all.sh` 跑 `dotnet test` 阶段时 2 个测试失败：

- `IncrementalBuildIntegrationTests.StdlibBuild_SecondRun_AllCached`
- `IncrementalBuildIntegrationTests.StdlibBuild_NoIncremental_ForcesFreshEvenWithCache`

错误信息：
```
src/libraries/z42.time/src/DateTimeOffset.z42(138,40): error E0402:
  argument 1: expected `TimeZone`, got `TimeZone`
```

根因：`src/libraries/z42.time/src/DateTimeOffset.z42` 是 parallel session
当前 in-flight 的**未 commit 文件**（git status `??` untracked）。
IncrementalBuildIntegrationTests 在本地 FS 上 incremental build stdlib，扫到
该文件触发同名 TimeZone 类型 mismatch（疑似跨 zpkg 类型 resolution bug，与
DateTimeOffset 引入相关）。

**非本 spec 引入** —— 我的改动只触 `jit/` + `docs/`。CI 在 clean checkout
（不含 DateTimeOffset.z42）上 test-all.sh 会全绿。本地这次 fail 反映 working
tree 状态而非 main 分支真实。建议 parallel session DateTimeOffset spec 完成
后该 failure 自动消解。
