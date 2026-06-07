# Tasks: 标准库自动可用

> 状态：🟢 已完成 | 创建：2026-06-06 | 完成：2026-06-07 | 类型：lang/compiler

## 进度概览
- [x] 阶段 1: WS013 lint（冗余 stdlib 声明）—— 实现 + 4 单测 + 2 对齐测试，dotnet 1523/1524（唯一红=分离 crypto WIP）
- [x] 阶段 2: Std.* 命名空间保留（E0605 硬错误）—— 实现 + 10 单测；确认全树无非 z42.* 包声明 Std.*（零破坏）
- [x] 阶段 3: 文档 + 示例清理 —— project.md 约定子节 + overview.md(推广「不声明」+ 改保留前缀为 E0605 硬错误) + error-codes.md E0605 行 + 删 21 处 [tests.dependencies] z42.test（examples 本就无冗余声明）
- [x] 阶段 4: 测试与 GREEN —— dotnet 1533/1534✓(唯一红=分离 crypto WIP) · stdlib dogfood 265/265✓ · cross-zpkg 2/2✓ · cargo build✓

## 阶段 1: WS013 lint ✅
- [x] 1.1 `ManifestErrors.cs` WS013 常量 + `RedundantStdlibDep` 工厂；`WorkspaceCatalog.cs` 描述条目
- [x] 1.2 `ProjectManifest.cs` `ScanForRedundantStdlibDeps`：扫 `[dependencies]`/`[tests.dependencies]`/`[bench.dependencies]`，非 `z42.*` 声明方 + `z42.*` entry → WS013；z42.* declarer 豁免
- [x] 1.3 `TestBenchManifestTests.cs`：WS013 触发(deps/tests.deps) / inter-dep 豁免 / 第三方无警告 4 测；对齐 WS012_NormalDeps + ProjectManifest NoFalsePositives

## 阶段 2: Std.* 保留 ✅
- [x] 2.1 注入点 = `PackageCompiler.BuildTarget.cs::TryCompileSourceFiles` Phase-0 解析循环（拿到 `cu.Namespace`/`cu.Span`）；判定抽 `internal CheckReservedNamespaceDeclaration`
- [x] 2.2 非 `z42.*` 包声明 `namespace Std*` → 硬错误 **E0605**（E06xx namespace 段，紧挨 warn 对应物 W0603）；`Diagnostic.cs` + `DiagnosticCatalog.cs` 注册
- [x] 2.3 `ReservedNamespaceDeclarationTests.cs`：第三方 Std/Std.* 报错(3)、stdlib 内 Std.* 通过(3)、第三方非保留 ns 通过(3)、默认 ns 通过(1) —— 共 10 测全过

## 阶段 3: 文档 + 清理 ✅
- [x] 3.1 `docs/design/compiler/project.md`：stdlib 自动可用约定子节（Rust-std 模型 + WS013 + E0605 + 只第三方进 deps）
- [x] 3.2 `docs/design/stdlib/overview.md`：auto-load 政策推广到全 stdlib（不声明）；保留前缀段从「W0603 软警告」改为「E0605 硬错误 + W0603 软网」两层；诊断码列表加 E0605。`docs/design/compiler/error-codes.md` 同步加 E0605 行
- [x] 3.3 `examples/*.z42.toml`：核查无用户示例声明冗余 stdlib `[dependencies]`（本就干净，无需改）
- [x] 3.4 清理 21 处 `[tests.dependencies] z42.test`：21 个 stdlib manifest 的整个 `[tests.dependencies]` 块移除（仅含 z42.test，z42.test 自动可用）。验证：test 发现是 convention-based（`tests/` 目录），不依赖该 section；member 构建序用 `[dependencies]`，不受影响

## 阶段 4: 验证 ✅
- [x] 4.1 回归：空依赖 + `using Std.IO/Std.Math/Bencher` 编译通过（proposal 证据段已本地验，stdlib dogfood 再覆盖）
- [x] 4.2 dotnet test 全绿（含新 WS013 / E0605 单测）—— 1533/1534（唯一红=分离 crypto secp256k1 WIP，非本次）
- [x] 4.3 stdlib dogfood 265/265（22 lib，删 21 处 z42.test 零破坏）+ cross-zpkg 2/2 + cargo build release
- [x] 4.4 spec scenarios 逐条覆盖（空依赖用 stdlib / z42.test 自动可用 / WS013 触发+inter-dep 豁免 / E0605 第三方报错+stdlib 通过）

## 备注
- D5 关键：WS013 只对**非 z42.* 声明方**触发，保 stdlib 自身 inter-dep（build 排序）不误伤。
- 3.4 已与现状对齐：21 处 z42.test 整块删除；stdlib dogfood 265/265 验证零破坏。
- cross-zpkg 验证踩坑：xtask 的 `_z42vm()` 在无 `Z42_HOME`/`Z42_PORTABLE_VM` 时返回裸名 `z42vm`（靠 PATH）；独立 `z42vm xtask.zpkg -- test cross-zpkg` 调用因 z42vm 不在 PATH → 驱动 `new Process("z42vm")` 抛异常 → 空输出"失败"。设 `Z42_PORTABLE_VM=<debug z42vm>` 后 2/2 通过。这是 harness 环境前提，非代码问题。
- 当前树有分离的 crypto 测试 dir-mode 重构 WIP（未跟踪 secp256k1/），与本变更无关。
