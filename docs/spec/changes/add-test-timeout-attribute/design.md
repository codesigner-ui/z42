# Design: `[Timeout(ms)]` test attribute

## Architecture

```
.z42 source                       C# compiler                  zbc TIDX
─────────                         ───────                       ────
[Test]                            AttributeBinder ──►     {
[Timeout(5000)]                     parses arg list,             method_id,
void slow_test() { ... }            writes BoundTestAttr         kind=Test,
                                      .TimeoutMs = 5000          flags=...,
                                                              + timeout_ms=5000
                                  TestAttributeValidator     }
                                    enforces E0915
                                                                 │
                                  IrGen → TestEntry              ▼
                                                            ZbcWriter
                                                            (minor++)
                                                                 │
                                                                 ▼
                                Rust runtime
                                ────────────
                                zbc_reader.rs (minor sync)
                                  reads timeout_ms i32 → TestEntry
                                                                 │
                                                                 ▼
                                z42-test-runner
                                ────────────────
                                discover.rs
                                  DiscoveredTest.timeout_ms: Option<u32>
                                  (None when on-wire == 0)
                                                                 │
                                                                 ▼
                                exec.rs::run_one
                                  let budget_ms = test.timeout_ms
                                      .map(|m| m.min(2 * default_ms))
                                      .unwrap_or(default_ms);
                                  let deadline = Instant::now() +
                                      Duration::from_millis(budget_ms);
                                  // existing poll-with-deadline loop
```

## Decisions

### Decision 1: 唯一形态 `[Timeout(milliseconds: <int>)]`

**问题**：单参数 attribute 通常允许 positional 简写，但 z42 attribute
parser 当前**不支持** positional —— `ParseTestAttributeBody` 强制要求
`key: value` 形态（参 `TopLevelParser.Helpers.cs:325-332`）。

**选项**：
- A — 扩展 parser 支持 positional `[Timeout(5000)]`
- B — 复用现有 named-only 路径：`[Timeout(milliseconds: 5000)]`

**决定**：B。

**理由**：
- 整个 z42 attribute 体系（包括 `[Skip]`、未来的 `[TestCase]`）都是
  named-only — 这是 deliberate consistency
- positional 与 `milliseconds:` 二者都接受会让错误消息（"missing
  required arg" vs "wrong type for positional 0"）双倍维护
- `milliseconds:` 显式名参对单位的自文档性比裸 `5000` 强很多
- 不需要 parser 引入 positional 路径（独立维度的扩展，留给未来真正
  需要时的 spec）

### Decision 2: 单位选 ms / ns / TimeSpan

**问题**：每测试 budget 单位应是什么？

**选项**：
- A — `int milliseconds`
- B — `long nanoseconds`（与 `Bencher.MinNs` 一致）
- C — `TimeSpan`（typed wrapper，禁止 raw int 误用）

**决定**：A (`int milliseconds`)。

**理由**：
- 与 `Std.Threading.Thread.Sleep(long millis)` /
  `TimeSpan.FromMilliseconds(double)` 一致 — 用户写 sleep 用 ms，
  写 timeout 也用 ms，单位概念不切换
- Bencher.ns 是 internal "measure"，timeout 是 "budget"；两个不同
  use case 各用自然单位
- TimeSpan literal 需要先有 `TimeSpan` 字面量语法（不存在），attribute
  常量表达式收 TimeSpan 实例需要 const-eval（也不存在）；A 实施成本最低
- 24.8 天的 i32::MAX ms 远超任何合理 test budget；不需要 long

### Decision 3: sentinel `0` vs `Option<u32>` 写入磁盘

**问题**：zbc TIDX 怎么表达"无 override"？

**选项**：
- A — 写 `i32 timeout_ms`，`0` 表示无 override
- B — 写 `bool has_timeout` + `i32 timeout_ms`（标记位 + 值）
- C — 写 `i32 timeout_ms`，`-1` 表示无 override（用负数当 sentinel）

**决定**：A。

**理由**：
- "0 ms timeout" 没有合理 use case（编译期 E0915 已拒绝 `<= 0`），所以
  0 作为 sentinel 不冲突
- 单字段比双字段省 1 byte/entry，且无需读时 "if has_timeout then …"
  分支
- 现有 TestEntry 序列化模式（如 `expected_throw_type: String` 用 ""
  表示无）已用 sentinel；保持一致
- C 的 `-1` 哲学上对（signed sentinel），但 z42 自身 `Std.Threading.
  Thread.Sleep(-1)` saturate 到 0，类似数字 sentinel "魔术 -1" 容易
  跨语言文档失同步

### Decision 4: 2× hard ceiling clamp

**问题**：用户写 `[Timeout(86_400_000)]`（1 天）怎么办？

**选项**：
- A — 完全相信用户值
- B — 编译期 E0915 拒绝任何超过 `MAX_TIMEOUT_MS`（如 600 000）
- C — 运行时 clamp 到 `2 × runner_default`，print warning

**决定**：C。

**理由**：
- 任何全局上限都会被未来"我有合法理由要 30 min budget"的场景打脸；
  hard reject 不灵活
- 但完全相信用户也危险 — 一个 typo `[Timeout(10_000_000)]`（想写 10s
  写成了 10000s）就把 hang 检测彻底废了；CI 6h job 限制内变成实质
  "无 timeout"
- 2× runner default = 600 s 是一个 "明显宽松但仍然有限" 的折衷；过
  上限的请求被 clamp 到 ceiling 并打 note，用户能看见误用
- 运行时 clamp 不是编译期 E（用户可能在 fast machine 上写代码 → CI
  slow runner 上 budget 不够），所以 ceiling 留运行时

### Decision 5: zbc minor bump（不可避免，按 strict-pin 政策）

**问题**：能否避免 minor bump（zbc 已 frozen v1）？

**决定**：不能。strict-pin 政策（reader 精确匹配 writer 的 major + minor）
意味着任何 TIDX 字段添加都必须 bump minor，否则旧 reader 解析新文件
会按旧 TIDX 形状错位。

**Bump checklist**（按 `.claude/rules/version-bumping.md`）：

1. `ZbcWriter.VersionMinor++`
2. `zbc_reader.rs ZBC_VERSION_MINOR` 同步
3. `docs/design/runtime/zbc.md` Minor changelog 加一行
4. `src/tests/zbc-format/generate-fixtures.sh` regen
5. `ZpkgWriter.VersionMinor++` （zpkg 与 zbc 联动）
6. `zbc_reader.rs ZPKG_VERSION_MINOR` 同步
7. `docs/design/runtime/zpkg.md` Minor changelog 加一行
8. `src/tests/zpkg-format/generate-fixtures.sh` regen

`./scripts/regen-golden-tests.sh` 把 stdlib zpkg 一并 regen（旧 zpkg
失效是预期 — pre-1.0 不留兼容路径）。

### Decision 6: runner 默认 vs CLI 覆盖

**问题**：要不要现在加 `--default-timeout-ms` CLI flag？

**决定**：不加。

**理由**：
- 当前 `TEST_TIMEOUT_SECS = 300` 常量在 source 唯一处，已足够
- 加 CLI flag 是另一维度（"全 testsuite 默认"）, 与"per-method 覆盖"
  正交；混到一个 spec 增加面板
- 若未来需要（如 wasm runner 默认 60 s 而 desktop 300 s），单独
  spec 加 flag；这条决策不阻塞

### Decision 7: 升级 `TestAttribute.NamedArgs` 到 discriminated `AttributeArg`

**问题**：z42 attribute 当前 named args 是 `Dictionary<string, string>`
（值必是 string literal）。`[Timeout(milliseconds: <int>)]` 需要 int
值；如何承载？

**选项**：
- A — 用字符串"5000"，AttributeBinder 处 parse 成 int，类型错误报
  E0916。零 parser 变更
- B — 引入 `AttributeArg` 抽象 record + `AttributeArgString` /
  `AttributeArgInt` 两个 sealed record；parser 在 named arg 值的
  词法位置 dispatch（StringLiteral → AttributeArgString；
  IntegerLiteral → AttributeArgInt）；`NamedArgs` 类型升级；所有
  消费侧加一个 `RequireStringArg(key)` 帮手

**决定**：B（与 user 确认）。

**理由**：
- A 的代价是永久把数字字面量伪装成字符串，每一处用户都得记得加引号
  写 `milliseconds: "5000"` — 反 ergonomic，且后续 `[TestCase(args)]`
  / `[Repeat(count: ...)]` 等参数化 attribute 都得继承这个 wart
- B 是 z42 attribute 系统的一次性升级，把 named-arg 值从 untyped
  string 升级到 typed discriminator —— 未来再加 `AttributeArgIdent`
  (for `[Skip(platform: ios)]` 形态) / `AttributeArgFloat` 都只是
  加 variant，不破现有签名
- 现有消费侧（`[Skip]` reason/platform/feature、`[Native]` lib/entry）
  统一通过 `RequireStringArg(key)` 读取；wrong-type 自动有清晰诊断
  消息

**`AttributeArg` 形态**（精简到当前真正用到的两种 variant）：

```csharp
public abstract record AttributeArg(Span Span);
public sealed record AttributeArgString(string Value, Span Span)
    : AttributeArg(Span);
public sealed record AttributeArgInt(long Value, Span Span)
    : AttributeArg(Span);
// Future: AttributeArgIdent (unquoted name)、AttributeArgFloat 等
// 加 variant 即可，无需破坏 record 层级
```

Parser 端（`ParseTestAttributeBody` 字面量位置）：

```csharp
AttributeArg val;
if (cursor.Current.Kind == TokenKind.StringLiteral) {
    var raw = cursor.Current.Text;
    var s   = raw.Length >= 2 ? raw[1..^1] : raw;
    val = new AttributeArgString(s, cursor.Current.Span);
} else if (cursor.Current.Kind == TokenKind.IntegerLiteral) {
    // Parse using existing numeric-literal helper that handles _ separators
    // and decimal/hex prefixes (mirror ExprParser's integer-literal path).
    val = new AttributeArgInt(ParseIntegerLiteralValue(cursor.Current),
                              cursor.Current.Span);
} else {
    throw new ParseException(
        $"`[{name}(...)]`: value for `{keyTok.Text}` must be a string or integer literal",
        cursor.Current.Span,
        DiagnosticCodes.UnexpectedToken);
}
```

消费侧（in `TestAttributeValidator.cs` / `IrGen.Tests.cs`）：

```csharp
// In validator
static string? RequireStringArg(TestAttribute attr, string key, DiagnosticBag diags) {
    if (attr.NamedArgs?.TryGetValue(key, out var arg) != true) return null;
    if (arg is AttributeArgString s) return s.Value;
    diags.Error(/* attribute-specific code */, $"...{key}... must be string", arg.Span);
    return null;
}

// In IrGen
static int RequireIntArg(TestAttribute attr, string key) {
    if (attr.NamedArgs?.TryGetValue(key, out var arg) == true
        && arg is AttributeArgInt i) return checked((int)i.Value);
    return 0;
}
```

## Implementation Notes

### AttributeBinder

参考现有 `[Skip(string reason)]` 解析路径（`AttributeBinder.BindSkip`）。
`[Timeout(int)]` 是同样的"单 typed arg" 形态。差别：
- arg type 是 `int` 不是 `string`
- 错误码用 E0915 不是 E0914

`milliseconds:` named arg：复用现有 named-arg parsing（同 spec 顶部
说的"parser already accepts"）。AttributeBinder 接受 named OR
positional，二者 bind 到同一 `TimeoutMs` 槽。

### TestAttributeValidator

E0915 触发：
- 值 `<= 0` → "`milliseconds must be > 0 (got <v>)`"
- 值不是 int 字面量 → "`milliseconds must be int (got <type>)`"
- 值溢出 i32 → "`milliseconds must fit in i32 (got <v>)`"
- target 非 `[Test]/[Benchmark]` → "`[Timeout] requires [Test] or
  [Benchmark]`"
- 同方法多个 `[Timeout]` → "`applied more than once`"

### Runner exec.rs

把现有的 `TEST_TIMEOUT_SECS = 300` 改为接受 `test.timeout_ms`：

```rust
const DEFAULT_TIMEOUT_SECS: u64 = 300;
const TIMEOUT_HARD_CEILING_SECS: u64 = DEFAULT_TIMEOUT_SECS * 2;

let budget = test.timeout_ms
    .map(|ms| Duration::from_millis(ms as u64))
    .unwrap_or(Duration::from_secs(DEFAULT_TIMEOUT_SECS));
let ceiling = Duration::from_secs(TIMEOUT_HARD_CEILING_SECS);
let budget = if budget > ceiling {
    eprintln!("note: clamped requested timeout {:?} to ceiling {:?}",
              budget, ceiling);
    ceiling
} else { budget };
let deadline = Instant::now() + budget;
```

`reason` 字符串里改为打印 `budget.as_secs_f64()` 而非常量，让用户能
看到实际生效的 budget。

### Test fixtures (zbc / zpkg minor bump)

`src/tests/zbc-format/generate-fixtures.sh` 跑一遍会把 6 个 fixture
的 `source.zbc` + `expected.json` 全部 regen。`expected.json` 里会有
新的 `timeout_ms` 字段（应该是 0 / 缺省）。Git diff 应该只包含字节
变化（version field + 新 slot）+ expected.json 加一行字段。

zpkg fixture 同理。

## Testing Strategy

### C# 单元测试 (`TimeoutAttributeBinderTests.cs`)

5 个场景：

1. `[Test][Timeout(5000)]` → `BoundTestAttribute.TimeoutMs == 5000`
2. `[Test][Timeout(milliseconds: 5000)]` → 同上
3. `[Test][Timeout(0)]` → E0915 "must be > 0"
4. `[Timeout(1000)]` (无 `[Test]`) → E0915 "requires [Test]"
5. `[Test][Timeout(1000)][Timeout(2000)]` → E0915 "applied more than once"

### Rust runner integration (`tests/timeout_per_test_test.rs`)

单场景：写一个 inline z42 源（or 用预编译 .zbc fixture）含一个
`[Test][Timeout(50)]` 方法 sleep 200ms。`run_one` 应返回
`Outcome::Failed`，reason 含 "timed out after 0.05s"。

### 端到端 / golden

`src/libraries/z42.crypto/tests/ecdsa_secp256k1_vectors.z42` 给
`test_secp256k1_sign_verify_round_trip` 加 `[Timeout(60000)]` —
作为 in-tree 示范用法 + 验证 stdlib 集成。

### Format invariant

`dotnet test --filter "FullyQualifiedName~Z42.Tests.Zbc"` +
`Zpkg` 必须过。

### GREEN gate

`./scripts/test-all.sh` 全套必须过。
