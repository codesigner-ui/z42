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
| `<fixture>/source.zpkg`     | ZpkgWriter 输出字节（check in；regen 后 git diff = 实际格式变化）|
| `<fixture>/expected.json`   | `z42c golden-json` 归一化输出（check in；语义层 invariant 比对）|
| `generate-fixtures.sh`      | 一键 regen — 调用 C# test harness 的 regen mode |

## 维护流程

正当 wire format 变化时（minor bump）：

```bash
./scripts/build-stdlib.sh           # 必要 — fixture 用 stdlib 解析 namespace
./src/tests/zpkg-format/generate-fixtures.sh
git diff src/tests/zpkg-format/     # review 哪些 fixture 受影响
```

每次 bump 必须把 fixture 一同 commit。CI `FormatGoldenTests` 跑 in-process 现场重生与 check-in 对账，diff 即 fail。

## 测试 harness

| 测试 | 检查内容 |
|------|---------|
| `FormatGoldenTests.ByteEqual` | 现场 ZpkgWriter 输出 == 该 fixture 的 `source.zpkg` |
| `FormatGoldenTests.JsonEqual` | 现场 ZpkgGoldenJsonFormatter 输出 == 该 fixture 的 `expected.json` |
| `FormatGoldenTests.WriterDeterministic` | 同输入两次写入 == 字节等 |

详见 [`src/compiler/z42.Tests/Zpkg/FormatGoldenTests.cs`](../../../src/compiler/z42.Tests/Zpkg/FormatGoldenTests.cs)。

环境变量 `Z42_ZPKG_REGEN=1` 让 harness 进 regen 模式（在原地重写 fixture 文件而非断言）。

## 入口点

- 维护命令：`./generate-fixtures.sh`
- 测试 harness：`dotnet test --filter Z42.Tests.Zpkg.FormatGoldenTests`

## 依赖关系

- 上游：`scripts/build-stdlib.sh` 产出（`artifacts/build/libraries/dist/release/*.zpkg`）—— fixture compile 需要 stdlib 解析 namespace
- 下游：`FormatGoldenTests` harness + `FormatInvariantTests`
