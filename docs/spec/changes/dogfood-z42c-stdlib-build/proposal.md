# Proposal: dogfood-z42c-stdlib-build (replace-csharp-compiler S3)

## Why

replace-csharp-compiler 路线的 S3：让**生产 stdlib 构建由 z42c 接管**（此前 C#）。
前置已就位：
- z42c `build --workspace`（无 --output-dir）= M2 per-member drop-in 布局，与 C# 同布局
  （z42c-build-workspace 归档）。
- z42c-built stdlib **全 272 stdlib [Test] 通过**（本 change 实施前已验证）。

C# 仍全程是种子（铁律：S5 删 C# 前必须有 S4 种子）。本 change 只把 `_buildStdlibCore`
的产出端从 C# 换成 z42c，**不删 C#**。

## What Changes

`scripts/xtask_stdlib.z42` 的 `_buildStdlibCore`，bootstrap-safe 序：
1. **C# 种子 stdlib**（fresh，from source）—— z42c.driver 运行期依赖它。
2. **C# build z42c**（`_buildCompilerZ42`）—— 产 z42c.driver.zpkg。
3. **ensure z42vm**（`_buildRuntime`；z42c 跑在它上）。
4. **assemble run-libs**（C#-种子 stdlib + z42c 7 包，**copy 非 hard-link**）。
5. **z42c 重编 stdlib**（`z42vm z42c.driver.zpkg --mode interp -- build --workspace
   --release`，cwd=src/libraries，M2 per-member，Z42_LIBS=run-libs）→ 覆盖 canonical
   per-member 布局。
6. **verify** + **flat view**（z42c-built；不变）。

## Scope（允许改动的文件）

| 文件 | 类型 | 说明 |
|------|------|------|
| `scripts/xtask_stdlib.z42` | MODIFY | `_buildStdlibCore` 改 z42c 接管 + 私有 helper（C# 种子 build / run-libs 组装 / reset 目录）|
| `docs/design/compiler/self-hosting.md` | MODIFY | 记录生产 stdlib build 由 z42c 接管（S3 机制）|
| `docs/spec/changes/replace-csharp-compiler/tasks.md` | MODIFY | S3.1/S3.2 勾选 + 措辞对齐（per-member drop-in 替代原 flat+swap）|

**只读引用**：
- `scripts/xtask_compiler_z42.z42`（`_buildCompilerZ42` / `_copyAll` / `_resetDir` 复用）
- `scripts/xtask.z42`（`_buildRuntime` / `_regenCore` 调用序）
- `src/z42c/z42c.driver/src/Main.z42`（M2 `build --workspace` 行为）

## Out of Scope（后续阶段）
- S4 自举闭环 + committed/下载种子；S5 删 C#。
- z42c jit-mode 加速 stdlib 构建（perf 优化，留 Deferred）。
- 把 test-unit / cross-zpkg 编译切到 z42c（S2.3；被 z42c 缺 impl-block 阻塞，独立 workstream）。

## Open Questions
- [ ] perf：z42c interp 重编 stdlib ~30s 加到每次 `build stdlib`（dogfood 税）；jit 加速留 follow-up。
