# zpkg-format

## 职责

`.zpkg` v0 wire format 的字节级 golden fixture 集合。固化 ZpkgWriter 当前 emit 行为，防止 wire layout 在 minor bump 之间偷偷漂移。

每个 fixture 目录 = 一种代表性 zpkg layout：

| Fixture | 覆盖 |
|---------|------|
| `packed-minimal/`     | 单 class 单模块；packed mode 基础形态（META + STRS + NSPC + EXPT + DEPS + SIGS + MODS）|
| `packed-multi-module/`| 多 .z42 → 同一 zpkg；MODS 多条目 + 共享 STRS pool |
| `indexed-minimal/`    | 增量编译 cache form；FILE section 替代 MODS |
| `sym-only-sidecar/`   | `FlagSymOnly` set；只含 META + STRS + MDBG + BLID（sym-only sidecar 形态）|

> Phase 0 不覆盖 `with-tsig` —— 需要 `ExportedModules` 设置非空，必须走 PackageCompiler.BuildTarget 完整 pipeline。留 follow-up。

## 核心文件
| 文件 | 职责 |
|------|------|
| `<fixture>/source.z42`（或 `mod_a.z42` + `mod_b.z42`） | z42 源（check in）|
| `<fixture>/source.zpkg`     | z42c 输出字节基线（check in；regen 后 git diff = 实际格式变化）|

## 维护流程

正当 wire format 变化时（minor bump）：用 z42c 重新 `build` 各 fixture 源、覆写 `source.zpkg`，`git diff` review 后连同 fixture 一起 commit。

> **TODO（regen 命令）**：尚无 z42-native 一键 regen（旧 C# harness regen 已随 dotnet 移除）。format bump 时暂需手工用 `z42c build` 重生每个 fixture 的 `source.zpkg`，或补一个 `xtask` 子命令统一处理（见 `.claude/rules/version-bumping.md`）。

## 测试 harness

| 测试 | 检查内容 |
|------|---------|
| `lazy_loader_tests.rs` | Rust 加载这些 check-in 的 `source.zpkg` 字节基线，验证 reader 兼容当前 wire format |

详见 [`src/runtime/src/metadata/lazy_loader_tests.rs`](../../../src/runtime/src/metadata/lazy_loader_tests.rs)。

## 入口点

- 测试 harness：`cargo test lazy_loader`

## 依赖关系

- 上游：`z42 xtask.zpkg build stdlib` 产出（`artifacts/build/libraries/dist/release/*.zpkg`）—— fixture compile 需要 stdlib 解析 namespace
- 下游：`FormatGoldenTests` harness + `FormatInvariantTests`
