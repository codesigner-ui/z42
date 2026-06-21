# Proposal: replace-csharp-compiler

## Why

z42c（自举编译器）已证实能完整替代 C# bootstrap 编译器：
- ✅ `--emit-zbc`（代码字节一致 C#）、`build`（整包）、`build --workspace`（拓扑序）
- ✅ 编全 22 个 stdlib 包 + z42c 自己 7 包，**功能正确**（z42c-built stdlib 跑测试通过）
- ✅ 性能：z42c interp 编译 0.199s < C# direct-DLL 0.245s

目标：删 `src/compiler/`（C#），z42c 成唯一编译器，全平台分发。

## 🔴 核心铁律：避免鸡生蛋（bootstrap 循环）

```
z42c（z42 写）→ import stdlib → 编 z42c 需 stdlib 先在
stdlib → 由 z42c 编 → 需 z42c 先在
```
**安全序：C# 全程保留作种子，直到 z42c 自举闭环 + committed/下载种子就位（S4），才删 C#（S5）。**
**S5 之前必须有一个能脱离 C# 重建 z42c 的种子。**

## 分阶段（bootstrap-safe，每阶段单独验证 + 提交）

| 阶段 | 内容 | 鸡生蛋安全 | 状态 |
|---|---|---|---|
| **S0** | `build --workspace`（前置能力）| — | ✅ 已完成（z42c-build-workspace 归档）|
| **S2** | 切**叶子编译点**到 z42c（test-unit compile / cross-zpkg / golden regen）——编用户/测试码，非 bootstrap 链。加 `_z42c` helper（z42vm + z42c.driver.zpkg）| C# 仍建 stdlib+z42c | 进行中 |
| **S3** | stdlib dogfood：C# 种子 stdlib → C# build z42c → z42c 重编 stdlib（覆盖/验证）| 有序，C# 种子在前 | 待 |
| **S1** | z42c apphost（原生 `z42c`，分发用）——SDK 安装布局 | 纯产物 | 待（分发阶段）|
| **S4** | z42c 自举闭环 + **committed/下载 z42c 种子**（Rust stage0 式）| 种子就位解除 C# 构建依赖 | 待 |
| **S5** | 删 `src/compiler/`，`src/z42c/` 迁新家（布局待定：src/compiler vs src/libraries）| 仅 S4 后 | 待 |

> S1（apphost）从分发链路看不阻塞管线替换（管线直接 `z42vm z42c.driver.zpkg`），故排在 S2/S3 后。

## Scope
逐阶段在各自 tasks 增量列出（避免一次性锁定大量文件）。本 change 作 roadmap 主索引；
每阶段可拆独立 change 或在此追加 tasks 段。

## Out of Scope（替换后/独立）
- C# driver 的 check/run/publish/query/scaffold 命令（不在关键路径；按需后补 z42c）
- 整包 zpkg byte-identical C#（功能正确已足够；DEPS/TSIG 对齐是独立大工程）

## Open Questions
- [ ] S5 新家布局：`src/z42c/`→`src/compiler/` vs 进 `src/libraries/`（memory project_libraries_scope：REPL 要编译器作 zpkg）
- [ ] S4 种子形态：committed prebuilt z42c.zpkg（离线）vs 下载 nightly
