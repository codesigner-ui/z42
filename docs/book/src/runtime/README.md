# 第三部分 · 运行时（Runtime / VM）

Rust 实现的 z42 虚拟机：执行模型（interp / jit / aot）、IR 与 zbc 格式、GC、嵌入与跨平台、native interop ABI。

## 章节规划

| 章节 | 涵盖 |
|------|------|
| 执行模型 | interp / jit / aot 分层、分级执行、safepoint、hot-reload |
| IR 与 zbc 二进制格式 | IR 指令集、zbc/zpkg 磁盘格式、IR 特化 |
| GC | 回收器设计、GC handle |
| 嵌入与跨平台 | 嵌入接口、PAL、load context、跨平台/stdlib 平台层 |
| native interop ABI | object ABI、native 扩展加载 |

## 迁移状态（旧 `docs/design/runtime/` → 本部分）

> ⬜ 待迁 · 🟡 迁移中 · ✅ 已迁并校对。

| 旧文档 | 目标章节 | 状态 |
|--------|---------|------|
| vm-architecture.md | 执行模型（主干） | ⬜ |
| execution-model.md / tiered-execution.md / jit.md / aot.md / safepoint.md / hot-reload.md | 执行模型 | ⬜ |
| ir.md / zbc.md / zpkg.md / ir-specialization.md | IR 与 zbc 二进制格式 | ⬜ |
| gc.md / gc-handle.md | GC | ⬜ |
| embedding.md / pal.md / cross-platform.md / load-context.md / stdlib-platform.md / launcher.md | 嵌入与跨平台 | ⬜ |
| object-abi.md / native-ext-loader.md / componentized-runtime.md | native interop ABI | ⬜ |
| concurrency.md / diagnostics.md | 执行模型 / 附录（按内容归位） | ⬜ |
