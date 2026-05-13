# Tasks: Add z42.time

> 状态：🟢 已完成 | 创建：2026-05-12 | 暂停：2026-05-13 | 恢复+完成：2026-05-14
> 类型：lang（完整流程）

## 暂停原因

实施期发现与并行进行中的 `add-std-process` spec 冲突：
- add-std-process 在 `src/runtime/src/corelib/` 加 `process.rs` + 改 `vm_context.rs`、扩 z42.io（+7 个 Process*.z42 / Directory.z42 / Exception 文件）
- 本 spec 的 Decision 2（重命名 `__bench_now_ns` → `__time_now_mono_ns`）+ Decision 3（删 `Environment.GetCurrentTimeMs`）触及 add-std-process 同改文件，三方面修改互相覆盖
- IncrementalBuildIntegrationTests 跟 add-std-process 的 z42.io 文件数同步（期望 12/12），任一方先 commit 都会让另一方测试失败

**裁决**：等 add-std-process 归档后再恢复本 spec。届时：
1. 重新评估 Decision 2 / 3 是否仍合理（add-std-process 落地后 corelib 结构可能已变）
2. 若仍合理，按本 spec 继续；否则 design.md 加修订段
3. 实施期前文档保持不动，proposal / spec / design 内容仍有效

## 已完成的工作（草稿，未 commit）

- proposal.md / spec.md / design.md / tasks.md 完整 4 文档
- z42.time 源码草稿：DateTime.z42 / TimeSpan.z42 / Stopwatch.z42 + tests / README + manifest
- 草稿在 implementation 过程中已被回退（与 add-std-process 并行修改冲突），需恢复时重新落到磁盘

## 实施期发现（保留供恢复时参考）

- ~~z42 当前**numeric cast 是 IR no-op**~~ — **2026-05-13 已修复**（[archive/2026-05-13-fix-numeric-cast-lowering](../../archive/2026-05-13-fix-numeric-cast-lowering/)）。恢复 z42.time 时可启用 `FromSeconds(double)` / `TotalSeconds() → double` 等 C# 风格 API；当前草案的整数版可保留为辅助 overload
- z42.test.Bencher 现状用 `__bench_now_ns` 直接调用，重命名时需同步更新（Bencher.z42 5 处）

## 进度概览

- [ ] 阶段 1: VM 原生重命名（`__bench_now_ns` → `__time_now_mono_ns`）【延后，见 time.md Deferred】
- [x] 阶段 2: z42.time 包 — TimeSpan / DateTime / Stopwatch
- [x] 阶段 3: 单元测试（[Test] dogfood）
- [ ] 阶段 4: z42.io / z42.test 同步迁移【延后，等阶段 1 完成后一并做】
- [x] 阶段 5: 文档同步
- [x] 阶段 6: GREEN 验证 + 归档

## 阶段 1: VM 原生重命名

- [ ] 1.1 [src/runtime/src/corelib/bench.rs](../../../../src/runtime/src/corelib/bench.rs) — `builtin_bench_now_ns` → `builtin_time_now_mono_ns`（rename only；逻辑不变）
- [ ] 1.2 [src/runtime/src/corelib/mod.rs](../../../../src/runtime/src/corelib/mod.rs) — 注册键 `"__bench_now_ns"` → `"__time_now_mono_ns"`
- [ ] 1.3 [src/runtime/src/corelib/bench_tests.rs](../../../../src/runtime/src/corelib/bench_tests.rs) — 调用名同步
- [ ] 1.4 cargo build --release 验证

## 阶段 2: z42.time 包

- [x] 2.1 [src/libraries/z42.time/z42.time.z42.toml](../../../../src/libraries/z42.time/z42.time.z42.toml) NEW — manifest，`name = "z42.time"`, depend on `z42.core`
- [x] 2.2 [src/libraries/z42.time/src/TimeSpan.z42](../../../../src/libraries/z42.time/src/TimeSpan.z42) NEW — 所有访问器为方法（z42 不支持命名 property getter）
- [x] 2.3 [src/libraries/z42.time/src/DateTime.z42](../../../../src/libraries/z42.time/src/DateTime.z42) NEW — 内嵌 `[Native("__time_now_ms")]`
- [x] 2.4 [src/libraries/z42.time/src/Stopwatch.z42](../../../../src/libraries/z42.time/src/Stopwatch.z42) NEW — 内嵌 `[Native("__bench_now_ns")]`（待 rename）
- [x] 2.5 实施期发现 z42 不支持命名 property getter；所有访问器全部改为方法
- [x] 2.6 [scripts/build-stdlib.sh](../../../../scripts/build-stdlib.sh) — 加 z42.time；index.json 加 `Std.Time`

## 阶段 3: 单元测试（[Test] dogfood）

- [x] 3.1 [src/libraries/z42.time/tests/timespan.z42](../../../../src/libraries/z42.time/tests/timespan.z42) NEW — 10 个 `[Test]` 函数，全覆盖
- [x] 3.2 [src/libraries/z42.time/tests/datetime.z42](../../../../src/libraries/z42.time/tests/datetime.z42) NEW — 7 个 `[Test]` 函数
- [x] 3.3 [src/libraries/z42.time/tests/stopwatch.z42](../../../../src/libraries/z42.time/tests/stopwatch.z42) NEW — 5 个 `[Test]` 函数，busy-wait 确认单调

## 阶段 4: z42.io / z42.test 同步迁移

- [ ] 4.1 [src/libraries/z42.io/src/Environment.z42](../../../../src/libraries/z42.io/src/Environment.z42) — 删除 `GetCurrentTimeMs` 方法 + `[Native("__time_now_ms")]` 行
- [ ] 4.2 [src/libraries/z42.io/tests/console.z42](../../../../src/libraries/z42.io/tests/console.z42) — `Environment.GetCurrentTimeMs()` → `DateTime.UtcNow.UnixMs`；加 `using Std.Time;`；z42.io tests manifest 加 `z42.time` 依赖（若需要）
- [ ] 4.3 [src/libraries/z42.test/](../../../../src/libraries/z42.test/) — 找到 `__bench_now_ns` 引用（Bencher）改为 `__time_now_mono_ns`
- [ ] 4.4 [src/libraries/z42.test/](../../../../src/libraries/z42.test/) 自测 — 如有 bencher 测试，确认通过

## 阶段 5: 文档同步

- [x] 5.1 [docs/design/stdlib/time.md](../../../design/stdlib/time.md) NEW — 包设计文档，含 Deferred 段
- [x] 5.2 [src/libraries/z42.time/README.md](../../../../src/libraries/z42.time/README.md) — 更新入口点（方法而非属性）
- [x] 5.3 [src/libraries/README.md](../../../../src/libraries/README.md) — 包列表加 z42.time 行
- [x] 5.4 [docs/design/stdlib/roadmap.md](../../../design/stdlib/roadmap.md) — P0 表移除 z42.time；已落地加 z42.time；Deferred Backlog Index 加新延后项
- [x] 5.5 [docs/design/stdlib/organization.md](../../../design/stdlib/organization.md) — 现状包列表加 z42.time
- [ ] 5.6 [src/libraries/z42.io/README.md](../../../../src/libraries/z42.io/README.md) — 移除 GetCurrentTimeMs 描述【延后，等阶段 4 完成后一并做】

## 阶段 6: GREEN + 归档

- [x] 6.1 ./scripts/test-stdlib.sh z42.time — 22/22 ✅；./scripts/test-all.sh — 预存失败不变（43 VM + 22 process stdlib）
- [x] 6.2 dotnet test — 1248/1248 ✅
- [x] 6.3 mv `docs/spec/changes/add-z42-time/` → `docs/spec/archive/2026-05-14-add-z42-time/`
- [ ] 6.4 commit + push

## 备注

- spec.md Scenario 使用 `a - b` / `a < b` 等中缀符号是**意图描述**；实施时落地为命名方法 `a.Subtract(b)` / `a.IsLessThan(b)`（z42 暂无 operator overloading）。spec 落地时同步更新文字。
- v0 不含日历分解 / parse / format / Sleep / DateTimeOffset — 全部入 time.md Deferred + roadmap Index。
