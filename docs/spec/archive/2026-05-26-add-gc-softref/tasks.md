# Tasks: add-gc-softref

> 状态：🟢 已完成 | 创建：2026-05-26 | 归档：2026-05-26 | 类型：vm

## 进度概览

- [x] 阶段 1-6: spec 文档
- [x] 阶段 6.5: User 确认
- [x] 阶段 7: 实施
- [x] 阶段 8: GREEN
- [x] 阶段 9: 归档

## P0: Rust GC 层

- [x] P0.1 NEW `src/runtime/src/gc/soft_registry.rs`:
       - `SoftEntry { id: u64, target: Option<GcRef<Object>> }`
       - `SoftRegistry` wrapping `Vec<SoftEntry>`
       - `insert(val) -> u64` / `upgrade(id) -> Value` / `clear_entries(ids)`
- [x] P0.2 MODIFY `src/runtime/src/gc/arc_heap.rs`:
       - `ArcMagrGcInner.soft_registry: SoftRegistry`
       - 压力判定：`used >= threshold * max`（`Z42_GC_SOFT_THRESHOLD` 默认 0.80）
       - sweep 前调 `clear_entries` 清除软引用目标
- [x] P0.3 MODIFY `src/runtime/src/corelib/process.rs` (new):
       - `builtin_soft_handle_create` / `builtin_soft_handle_get`
       - 注册 `__soft_handle_create` / `__soft_handle_get` 到 corelib
- [x] P0.4 `cargo build --release` GREEN

## P1: Corelib builtins

- [x] P1.1 MODIFY `src/runtime/src/corelib/gc.rs`:
       - `builtin_soft_handle_create(ctx, args)` — `soft_registry.insert(target)`
       - `builtin_soft_handle_get(ctx, args)` — `soft_registry.upgrade(id)`
- [x] P1.2 注册到 `corelib/mod.rs`

## P2: stdlib z42 层

- [x] P2.1 NEW `src/libraries/z42.core/src/GC/SoftHandle.z42`
- [x] P2.2 MODIFY `src/libraries/z42.core/z42.core.z42.toml` (sources 加 SoftHandle.z42)
- [x] P2.3 `./scripts/build-stdlib.sh` GREEN (61/61 files)

## P3: 测试

- [x] P3.1 NEW `src/tests/gc/gc_softhandle_basic/`
- [x] P3.2 NEW `src/tests/gc/gc_softhandle_pressure/`
- [x] P3.3 NEW `src/tests/gc/gc_softhandle_strong_wins/`
- [x] P3.4 `./scripts/test-all.sh --scope=full` GREEN

## P4: 文档 + 归档

- [x] P4.1 MODIFY `docs/design/runtime/gc.md`:
       - B2 条目状态 → ✅，加 Phase 表行，加 `SoftRef<T>` 泛型升级 Deferred 条目
- [x] P4.2 Archive → `docs/spec/archive/2026-05-26-add-gc-softref/`
- [x] P4.3 Commit + push

## 备注

- 测试源文件需用 `namespace Demo; void Main()` 顶层函数格式，不能用 `class Program { static void Main() }` — 后者会导致 VM 找不到入口点（"entry function 'Main' not found in module 'main'"）
- gc_softhandle_pressure 中需用独立帧函数 `IsAlive(h)` 检查存活状态；若在 Main 帧里调用 `h.Get()` 并保存到 local，GC mark 阶段扫 frame.regs 会把该局部变量当 root 保活目标对象
- IncrementalBuildIntegrationTests 与 StdlibSidecarPairingTests 需纳入同一 `[Collection("StdlibArtifacts")]` 防止并发删/读 artifacts 目录竞争
