# Proposal: `[Timeout(milliseconds)]` per-test method attribute

## Why

z42-test-runner has a single global `TEST_TIMEOUT_SECS = 300` cap
(commits c9fbdbe9 + fe6cb6b5). 300 s is the right floor for the
slowest legitimate test we ship (ECDSA secp256k1 sign-verify
round-trip on 3-vCPU CI runners), but it's a 30× over-cap for the
median test (millisecond-class).

Two concrete pains this causes today:

1. **Hung-test diagnosis lag** — a websocket race that previously
   sat for 5+ minutes per CI cycle now takes the full 300 s to
   surface. Fast tests should fail fast.
2. **No declarative way to mark "this one is genuinely slow"** — the
   global 300 s is hidden in Rust source. A user looking at a
   `[Test]` method has no signal whether the implicit budget is 1 s
   or 5 min. When they write a network / crypto / JIT-warmup test
   and CI says "timed out after 300 s", they have no idea whether
   that's a bug, a slow runner, or actually too tight.

What we want: each `[Test]` may declare its own budget via
`[Timeout(milliseconds: 10000)]` (or sugar `[Timeout(10000)]`). The
runner uses the per-method override if present, the default
otherwise. Document the default as "tight" (5–10 s) once override
exists so most tests fail fast and only the few outliers carry an
explicit budget.

Not doing → keep relying on the global cap. Every CI cycle pays
either the 300 s tax on hangs or the unsoundness of lowering the
cap and false-failing the legit-slow tests.

## What Changes

- Compiler — attribute argument syntax upgrade (prerequisite):
  - z42 attribute parser today (`ParseTestAttributeBody` in
    `TopLevelParser.Helpers.cs`) only accepts **named string-literal
    args** (`[Skip(reason: "x")]`). There is no positional form and
    no integer / numeric literal path. `[Timeout(milliseconds: 5000)]`
    would throw `ParseException: value must be a string literal`.
  - This spec lifts that restriction: attribute named args may be
    string-literal **OR** integer-literal. `TestAttribute.NamedArgs`
    changes from `IReadOnlyDictionary<string, string>?` to
    `IReadOnlyDictionary<string, AttributeArg>?` where
    `AttributeArg` is the discriminated `AttributeArgString(string)`
    / `AttributeArgInt(long)` pair (room left for future variants
    via the abstract record — e.g. `AttributeArgIdent` for
    `[Skip(platform: ios)]` once we want unquoted enum-like values).
  - Each existing consumer (`[Skip]` reason / platform / feature,
    `[Native]` lib / entry) reads the `AttributeArgString` form via a
    helper `RequireStringArg(key)` that reports a clear diagnostic
    if the arg is the wrong shape. Behaviour for existing string args
    is preserved byte-for-byte.
- Compiler — `[Timeout]` attribute:
  - New attribute name `Timeout`, only accepted form:
    `[Timeout(milliseconds: <int>)]`.
  - `TestAttributeValidator` enforces: `milliseconds > 0`,
    fits in `i32`, attribute requires `[Test]` or `[Benchmark]`,
    at most one `[Timeout]` per method.
  - New diagnostic **E0916 `TimeoutValueInvalid`** for `<= 0`,
    overflow, missing arg, wrong arg type, or wrong target.
    (E0915 is already taken by `SetupTeardownSignatureInvalid`.)
- zbc TIDX section:
  - Reserve a new `timeout_ms: i32` slot per `TestEntry`
    (`0` = "no override; use runner default"). Sentinel preserves
    the on-disk shape for entries that don't carry the attribute.
  - **zbc minor bump** (`VersionMinor++`) per
    `.claude/rules/version-bumping.md` strict-pin rule; sync to
    `ZBC_VERSION_MINOR` in Rust, `ZpkgWriter.VersionMinor` (zpkg
    couples minor with zbc — see version-bumping.md "zpkg 联动
    规则"), and `docs/design/runtime/zbc.md` Minor changelog.
- Test runner:
  - `DiscoveredTest` carries `timeout_ms: Option<u32>` (None →
    runner default 300 s).
  - `exec::run_one` uses the per-test value (clamped to a hard
    ceiling of `runner_default_secs * 2` so a typo can't disable
    the safety net; rationale in design.md).
- Stdlib `Std.Test`:
  - No new code — `[Timeout]` is purely attribute + TIDX + runner.
    z42 `Std.Test.Assert` etc. unchanged.
- Tests:
  - C# AttributeBinder test: parses the three syntactic forms
    (`[Timeout(1000)]`, `[Timeout(milliseconds: 1000)]`, attribute
    on non-`[Test]` method → E0915 wrong-target).
  - C# TestAttributeValidator test: rejects `0`, negative,
    overflow, non-int.
  - Rust runner integration test: spawns a z42vm with a `[Test]`
    that sleeps 200 ms with `[Timeout(50)]` → expects
    `Outcome::Failed { reason: "timed out after 0.05s ..." }`.
  - One stdlib test annotated `[Timeout(60000)]` on a known-slow
    case (e.g. an ECDSA verify) to exercise the override end-to-end.

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Core/Diagnostics/Diagnostic.cs` | MODIFY | 加 `TimeoutValueInvalid = "E0916"` 常量（E0915 已被 SetupTeardownSignatureInvalid 占用）|
| `src/compiler/z42.Core/Diagnostics/DiagnosticCatalog.cs` | MODIFY | E0916 描述条目 |
| `src/compiler/z42.Syntax/Parser/Ast.cs` | MODIFY | 新增 `AttributeArg` 抽象 record + `AttributeArgString` / `AttributeArgInt` 两个 sealed record；`TestAttribute.NamedArgs` 类型 `Dictionary<string, string>` → `Dictionary<string, AttributeArg>` |
| `src/compiler/z42.Syntax/Parser/TopLevelParser.Helpers.cs` | MODIFY | `ParseTestAttributeBody` 接受 IntegerLiteral；把 `[Timeout]` 加入 `TestAttributeNames` 白名单 |
| `src/compiler/z42.Semantics/TestAttributeValidator.cs` | MODIFY | (a) E0914 [Skip] reason 读取改用 `RequireStringArg`；(b) 新增 [Timeout] 校验分支报 E0916 |
| `src/compiler/z42.Semantics/Codegen/IrGen.Tests.cs` | MODIFY | `BuildTestEntry` 读 `AttributeArg` 类型；[Skip] reason/platform/feature 改用 string-arg helper；[Timeout] milliseconds → `TestEntry.TimeoutMs` |
| `src/compiler/z42.IR/TestEntry.cs` | MODIFY | 加 `int TimeoutMs` 字段（0 = 无 override）放在 `TestCases` 之后；ctor 参数顺序保持向后追加 |
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs` | MODIFY | `VersionMinor++` + 写 TIDX `timeout_ms i32` 槽（紧跟 expected_throw_type 之后）|
| `src/runtime/src/metadata/zbc_reader.rs` | MODIFY | `ZBC_VERSION_MINOR` + `ZPKG_VERSION_MINOR` 同步 + TIDX 反序列化读 timeout_ms |
| `src/runtime/src/metadata/test_index.rs` | MODIFY | `TestEntry { timeout_ms: u32 }` 字段 |
| `src/toolchain/test-runner/src/discover.rs` | MODIFY | `DiscoveredTest.timeout_ms: Option<u32>` |
| `src/toolchain/test-runner/src/exec.rs` | MODIFY | 重命名 `TEST_TIMEOUT_SECS` → `DEFAULT_TIMEOUT_SECS`；加 `TIMEOUT_HARD_CEILING_SECS`；`run_one` 用 per-test budget + clamp + warning |
| `src/compiler/z42.Project/ZpkgWriter.cs` | MODIFY | `VersionMinor++` + 联动注释 |
| `src/tests/zbc-format/generate-fixtures.sh` | RUN | 重生 fixtures（含新 timeout_ms = 0 字段） |
| `src/tests/zpkg-format/generate-fixtures.sh` | RUN | 同上 |
| `src/compiler/z42.Tests/TimeoutAttributeBinderTests.cs` | NEW | 解析 + validator 6 场景（spec.md ADDED Requirements） |
| `src/compiler/z42.Tests/AttributeArgIntParseTests.cs` | NEW | 通用解析层 3 场景：int literal、string literal、混合 args |
| `src/toolchain/test-runner/tests/timeout_per_test_test.rs` | NEW | 端到端 1 场景（z42 sleep 200ms + `[Timeout(milliseconds: 50)]` → 失败）|
| `src/libraries/z42.crypto/tests/ecdsa_secp256k1_vectors.z42` | MODIFY | `test_secp256k1_sign_verify_round_trip` 加 `[Timeout(milliseconds: 60000)]` 示范 |
| `docs/design/testing/testing.md` | MODIFY | "R-series 待落地" 表移除 `[Timeout]` + 新增 §"Per-test timeout" + 提及 attribute-arg type 升级 |
| `docs/design/runtime/zbc.md` | MODIFY | Minor changelog 一行 |
| `docs/design/runtime/zpkg.md` | MODIFY | 同上 |
| `src/libraries/z42.test/README.md` | MODIFY | 能力表加 `[Timeout(milliseconds: N)]` 行 |

**只读引用**：

- `src/compiler/z42.Syntax/Parser/Lexer.cs`（确认 IntegerLiteral token 名）
- `src/compiler/z42.IR/Metadata/TestFlags.cs`（看现有 flags enum 模式）
- `src/runtime/src/metadata/zbc_reader.rs` TIDX 反序列化路径
- `src/toolchain/test-runner/src/exec.rs` 现有 TEST_TIMEOUT_SECS 用法
- 现有 `BuildTestEntry` 中 `[Skip]` 读 `attr.NamedArgs.TryGetValue("reason", out var reason)` 的模式

## Out of Scope

- **降低默认 timeout** — 本 spec 只引入"每测试可覆盖"机制；改默认（300s
  → 5s）是配套的 follow-up，需要先全 stdlib 扫一遍把慢测试加上
  `[Timeout]` 再调，避免单 spec 引入大面积失败
- `[Timeout]` 应用到 `[Setup]/[Teardown]/[Benchmark]` 的 budget —
  本 spec 只覆盖 `[Test]/[Benchmark]` 方法本身的 budget（Setup/Teardown
  目前在 subprocess 模式不跑，不构成 timeout 风险）
- 软超时（"超过 X 但继续跑，标记 slow"）— 当前 hard kill 就够用
- 跨平台不同 timeout（如 Windows 给 2×）— 编译期固定值；若需平台
  乘子，由 runner CLI flag `--timeout-scale` 后续加（不在本 spec）
- 测试 retry（"超时一次再试一次"）— 与 timeout 正交，独立 spec
- 全局 default 改为 runner CLI flag — 当前 `TEST_TIMEOUT_SECS` 常量
  够用；若要 user override 默认值，单独加 `--default-timeout-ms` flag

## Open Questions

- [x] **已裁决**：syntax 形态。z42 attribute parser 不支持 positional，且
      值原本只接 string literal — 本 spec 同时升级 parser 支持 int
      literal（Option B in implementation discussion）。唯一接受形态为
      `[Timeout(milliseconds: <int>)]`。
- [x] **已裁决**：单位 = ms。与 `Std.Threading.Thread.Sleep(long millis)` /
      `TimeSpan.FromMilliseconds` 一致。
- [x] **已裁决**：sentinel = `0`。`> 0` 由 E0916 编译期保证，沿用现有
      TIDX 的 `expected_throw_type=""` 零值习惯。
- [x] **已裁决**：诊断码 = E0916（E0915 已被
      `SetupTeardownSignatureInvalid` 占用）。
- [ ] **保留**：`AttributeArg` discriminator 第三种 `AttributeArgIdent`
      (unquoted identifier 形如 `[Skip(platform: ios)]`) 是否在本 spec
      引入？建议**不**：留给 add-test-skip-platform-feature spec
      （那里有自然 use case，本 spec 不需要 identifier 值）。`AttributeArg`
      抽象 record 已开放扩展点，未来加 variant 不破坏现有解析。
