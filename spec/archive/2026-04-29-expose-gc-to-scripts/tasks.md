# Tasks: Expose GC Operations to z42 Scripts (Phase 3d.2)

> 状态：🟢 已完成 | 创建：2026-04-29 | 完成：2026-04-29 | 类型：vm + stdlib

## 变更说明

让 z42 脚本能显式控制 GC：触发环检测、查询堆使用量、强制完整 collect。
端到端验证 Phase 3a–3d.1 的 GC 工作链路。

## 完成清单

### 阶段 1: corelib builtins ✅
- [x] 新建 `src/runtime/src/corelib/gc.rs` —— 3 个 builtin
  - `__gc_collect(ctx, _)` → Null（调 ctx.heap().collect_cycles）
  - `__gc_used_bytes(ctx, _)` → I64（ctx.heap().used_bytes）
  - `__gc_force_collect(ctx, _)` → I64 freed_bytes（ctx.heap().force_collect 提取）
- [x] `corelib/mod.rs` 加 `pub mod gc;` + dispatch table 3 个条目

### 阶段 2: z42 stdlib script ✅
- [x] 新建 `src/libraries/z42.core/src/GC.z42` ——
      `public static class GC` 在 `Std` namespace 下，3 个 extern static 方法
      （`Collect()` / `UsedBytes()` / `ForceCollect()`）+ `[Native(...)]` 绑定

### 阶段 3: 端到端 golden test ✅
- [x] 新建 `src/runtime/tests/golden/run/110_gc_cycle/`
  - `source.z42`：构造 a-b 环 → 出 scope（经典环泄漏）→ `GC.UsedBytes()` 测量 →
    `GC.ForceCollect()` 触发 → 再次 UsedBytes 验证 freed > 0 + used 减少
  - `expected_output.txt`：3 行 `true`（alloc grew / freed > 0 / used dropped）
  - `source.zbc`：编译产物

### 阶段 4: 测试基础设施更新 ✅
- [x] `src/compiler/z42.Tests/IncrementalBuildIntegrationTests.cs` ——
      z42.core 文件数从 33 → 34（新增 GC.z42），更新 `cached: 33/33` →
      `cached: 34/34`（两处）
- [x] 运行 `./scripts/build-stdlib.sh` 重建所有 zpkg
- [x] 运行 `regen-golden-tests.sh` 编译 110_gc_cycle.source.zbc

### 阶段 5: 验证 ✅

**编译状态**：
- ✅ `cargo build` 0 错误 0 警告
- ✅ `dotnet build` 0 错误

**测试结果**：
- ✅ Rust unit tests: **138/138 通过**（lib）
- ✅ Rust integration tests: **4/4**
- ✅ `dotnet test`: **741/741**（含 IncrementalBuild 修复）
- ✅ `./scripts/test-vm.sh`: **interp 102 + jit 102 = 204/204**（新增 110_gc_cycle）

**端到端验证**：z42 脚本里调用 `GC.ForceCollect()` 真实回收了 `Node` cycle，
freed_bytes > 0，used_bytes 在 collect 后下降。GC 整条链路在真实程序里 work。

### 结论：✅ 全绿，可以归档
