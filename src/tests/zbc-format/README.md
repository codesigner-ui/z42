# zbc-format

## 职责

`.zbc` v1 wire format 的字节级 golden fixture 集合。固化 ZbcWriter 当前 emit 行为，防止 wire layout 在 minor bump 之间偷偷漂移。

每个 fixture 目录 = 一种代表性 zbc layout：

| Fixture | 覆盖 |
|---------|------|
| `empty/`              | 最小有效 module（`void Main() { }`），无类、无 stdlib 调用 |
| `strp-func-minimal/`  | 单类单方法 — STRP + TYPE + FUNC + DBUG 基础组合 |
| `multi-method/`       | 多方法 + 同类内 cross-method 调用 — 更密集的 line table |
| `with-tidx/`          | `[Test]` 注解触发 TIDX 段 |
| `cross-import-token/` | `Std.IO.Console` 调用触发 IMPT 段 + IMPORT_BASE 0x8000_0000 token |
| `with-frcs/`          | Method-group conversion 触发 FRCS（FuncRef cache slot）段 |

> BLID section 当前只在 stripped mode 出现（`--emit zbc` 默认不 strip），fixture 集合不覆盖；如果未来引入 stripped golden 测试，加 `stripped-with-blid/` 即可。

## 核心文件
| 文件 | 职责 |
|------|------|
| `<fixture>/source.z42`     | z42 源（check in）|
| `<fixture>/source.zbc`     | ZbcWriter 输出字节（check in；regen 后 git diff = 实际格式变化）|
| `<fixture>/expected.json`  | `z42c golden-json` 归一化输出（check in；语义层 invariant 比对）|
| `generate-fixtures.sh`     | 一键 regen — 跑 .z42 → .zbc → .json |

## 维护流程

正当 wire format 变化时（minor bump）：

```bash
./scripts/build-stdlib.sh           # 必要 — fixture 用 stdlib 解析
./src/tests/zbc-format/generate-fixtures.sh
git diff src/tests/zbc-format/      # review 哪些 fixture 受影响
```

每次 bump 必须把 fixture 一同 commit；不允许"代码改了 fixture 没 regen"或"fixture regen 了但 git diff 没 review"。CI `FormatGoldenTests` 跑 in-process 现场重生与 check-in 对账，diff 即 fail。

## 测试 harness

| 测试 | 检查内容 |
|------|---------|
| `FormatGoldenTests.ByteEqual` | 现场 ZbcWriter 输出 == 该 fixture 的 `source.zbc` |
| `FormatGoldenTests.JsonEqual` | 现场 ZbcGoldenJsonFormatter 输出 == 该 fixture 的 `expected.json` |

详见 [`src/compiler/z42.Tests/Zbc/FormatGoldenTests.cs`](../../../src/compiler/z42.Tests/Zbc/FormatGoldenTests.cs)。

## 入口点

- 维护命令：`./generate-fixtures.sh`
- 测试 harness：`dotnet test --filter FormatGoldenTests`

## 依赖关系

- 上游：`scripts/build-stdlib.sh` 产出（`artifacts/build/libs/release/*.zpkg`）—— `cross-import-token` / `with-tidx` 需要 stdlib 才能解析
- 下游：`FormatGoldenTests` harness（`src/compiler/z42.Tests/Zbc/`）
