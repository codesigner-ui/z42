# Tasks: `[Timeout(milliseconds: <int>)]` test attribute

> 状态：🟢 已完成 | 创建：2026-05-30 | 完成：2026-05-30 | 类型：lang + ir + stdlib (full flow)

## 进度概览

- [x] 阶段 1: AttributeArg discriminator + parser int-literal 支持
- [x] 阶段 2: 现有 [Skip] / [Native] 消费侧切到 RequireStringArg
- [x] 阶段 3: [Timeout] AttributeBinder + TestAttributeValidator (E0917)
- [x] 阶段 4: TestEntry + ZbcWriter minor bump + ZpkgWriter 联动
- [x] 阶段 5: Rust zbc_reader 同步 minor + 读 timeout_ms
- [x] 阶段 6: test-runner exec.rs per-test budget + 2× ceiling clamp
- [x] 阶段 7: 测试（C# parser/binder/validator + Rust integration + stdlib 示范）
- [x] 阶段 8: 文档同步（zbc.md / zpkg.md changelog / testing.md / z42.test README）
- [x] 阶段 9: Fixture regen + GREEN + commit + archive

## 阶段 1: AttributeArg discriminator + parser int-literal

- [x] 1.1 `src/compiler/z42.Syntax/Parser/Ast.cs` —
  - 新增 `public abstract record AttributeArg(Span Span);`
  - 新增 `public sealed record AttributeArgString(string Value, Span Span) : AttributeArg(Span);`
  - 新增 `public sealed record AttributeArgInt(long Value, Span Span) : AttributeArg(Span);`
  - 修改 `TestAttribute.NamedArgs` 类型 `IReadOnlyDictionary<string, string>?` → `IReadOnlyDictionary<string, AttributeArg>?`
- [x] 1.2 `src/compiler/z42.Syntax/Parser/TopLevelParser.Helpers.cs` —
  `ParseTestAttributeBody` 在 named-arg value 位置：
  - StringLiteral → `AttributeArgString`（保持现有 strip-quotes 行为）
  - IntegerLiteral → `AttributeArgInt`（调用 ExprParser 的整数解析 helper；
    若不存在 standalone helper，则在本文件就地实现 base-10 / `_` 分隔的小型 parse）
  - 其他 → ParseException 错误消息扩展为 "must be a string or integer literal"
- [x] 1.3 同上：把 `"Timeout"` 加入 `TestAttributeNames` 白名单
- [x] 1.4 任何现有 unit test 直接构造 `TestAttribute(...)` 字面量传 dict
  的，更新到新 AttributeArg 形态（多半在 z42.Tests 一两处）

## 阶段 2: 现有消费侧迁移

- [x] 2.1 `src/compiler/z42.Semantics/TestAttributeValidator.cs` 加
  `private static string? RequireStringArg(TestAttribute attr, string key, string code, DiagnosticBag diags)`
  helper：返回 string 或 null，wrong-type 报对应 diag code
- [x] 2.2 同上：`hasSkip && skipAttr` 分支的 reason 读取改用 helper
- [x] 2.3 `src/compiler/z42.Semantics/Codegen/IrGen.Tests.cs`
  `BuildTestEntry` 的 [Skip] 三个 named arg 读取（reason / platform /
  feature）改成 pattern match `AttributeArgString`；遇到 wrong-type
  fall through to 0（validator 已报错）
- [x] 2.4 任何使用 `[Native(lib: "...", entry: "...")]` 的解析处也走
  同样 string-arg helper（位置：`ParseNativeAttributeBody` 上游消费侧）
- [x] 2.5 `dotnet build src/compiler/z42.slnx` 通过；`dotnet test`
  `--filter "FullyQualifiedName~TestAttribute"` 全绿（旧测试无回归）

## 阶段 3: [Timeout] AttributeBinder + Validator

- [x] 3.1 `src/compiler/z42.Core/Diagnostics/Diagnostic.cs` —
  加 `public const string TimeoutValueInvalid = "E0917";`（紧跟 E0915
  `SetupTeardownSignatureInvalid` 之后）
- [x] 3.2 `src/compiler/z42.Core/Diagnostics/DiagnosticCatalog.cs` —
  E0917 entry：name "TimeoutValueInvalid"，category Test，description
  覆盖六种 trigger
- [x] 3.3 `src/compiler/z42.Semantics/TestAttributeValidator.cs` —
  - `ValidateFunction` 局部加 `bool hasTimeout = false; TestAttribute?
    timeoutAttr = null;` 累积
  - switch case 加 `"Timeout"` → 累积；若已有 → 立即 E0917 "applied
    more than once"
  - 校验链末尾加 `if (hasTimeout && timeoutAttr is not null)`
    分支调 `ValidateTimeoutAttribute`：
    - 无 `[Test]` 也无 `[Benchmark]` → E0917 "requires [Test] or
      [Benchmark]"
    - `NamedArgs` 缺 `milliseconds` key → E0917 "requires a single
      named arg \"milliseconds: <int>\""
    - value 不是 `AttributeArgInt` → E0917 "must be an integer literal
      (got string)"
    - `<= 0` → E0917 "must be > 0 (got <v>)"
    - `> int.MaxValue` → E0917 "must fit in i32 (got <v>)"

## 阶段 4: TestEntry + ZbcWriter

- [x] 4.1 `src/compiler/z42.IR/TestEntry.cs` — `TestEntry` record
  追加 `int TimeoutMs` 字段（默认 0），放在 `TestCases` 之后；XML
  doc 注明 "0 = no override, runner uses default"
- [x] 4.2 `src/compiler/z42.Semantics/Codegen/IrGen.Tests.cs` —
  `BuildTestEntry`：
  - 加 `int timeoutMs = 0;`
  - switch case 加 `"Timeout"` → 读 `NamedArgs["milliseconds"]` as
    `AttributeArgInt`，赋值 `timeoutMs = (int)i.Value`
  - new TestEntry(...) 加 `TimeoutMs: timeoutMs`
- [x] 4.3 `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs` —
  - `VersionMinor++`（注释列出本 bump 引入的 spec name + 字段）
  - TIDX section per-entry 序列化加 `w.WriteI32(entry.TimeoutMs);`
    紧跟 `ExpectedThrowTypeIdx` 之后
- [x] 4.4 `src/compiler/z42.Project/ZpkgWriter.cs` —
  `VersionMinor++` 联动注释（zbc 已 bump → zpkg 必跟）

## 阶段 5: Rust zbc_reader

- [x] 5.1 `src/runtime/src/metadata/zbc_reader.rs` —
  - `ZBC_VERSION_MINOR` 同步到新值
  - `ZPKG_VERSION_MINOR` 同步到新值
  - TIDX entry 反序列化在 `expected_throw_type_idx` 之后读
    `let timeout_ms = r.read_i32()?;`
- [x] 5.2 `src/runtime/src/metadata/test_index.rs` —
  `TestEntry` struct 加 `pub timeout_ms: u32`（i32 → u32 转换 OK，
  validator 已保证 > 0）

## 阶段 6: test-runner exec.rs

- [x] 6.1 `src/toolchain/test-runner/src/discover.rs` —
  `DiscoveredTest` 加 `pub timeout_ms: Option<u32>`（on-wire `0` →
  `None`，其它 → `Some(v)`）
- [x] 6.2 `src/toolchain/test-runner/src/exec.rs` —
  - 把 `const TEST_TIMEOUT_SECS: u64 = 300;` 改名为
    `const DEFAULT_TIMEOUT_SECS: u64 = 300;`
  - 加 `const TIMEOUT_HARD_CEILING_SECS: u64 = DEFAULT_TIMEOUT_SECS * 2;`
  - `run_one` 顶部按 design.md "Implementation Notes" 计算 budget +
    clamp + warning
  - 失败 reason 字符串里 `timed out after Xs` 用实际 budget 而非常量
- [x] 6.3 引用 `TEST_TIMEOUT_SECS` 的其他地方（如 doc comment）一并
  改名 / 删除

## 阶段 7: 测试

- [x] 7.1 `src/compiler/z42.Tests/AttributeArgIntParseTests.cs` — NEW
  - parse `[Skip(reason: "x", count: 3)]` → `NamedArgs["reason"]
    is AttributeArgString`, `NamedArgs["count"] is AttributeArgInt`
  - parse `[Skip(reason: "x")]` → 与旧版字节等价（回归）
  - parse `[Skip(reason: 42)]` → E0914 (reason must be string)
- [x] 7.2 `src/compiler/z42.Tests/TimeoutAttributeBinderTests.cs` — NEW
  - `[Test][Timeout(milliseconds: 5000)]` → TestEntry.TimeoutMs == 5000
  - `[Test][Timeout]` (无参) → E0917 "requires \"milliseconds: <int>\""
  - `[Test][Timeout(milliseconds: 0)]` → E0917 "must be > 0"
  - `[Test][Timeout(milliseconds: "5000")]` → E0917 "must be integer"
  - `[Timeout(milliseconds: 1000)]` 无 `[Test]` → E0917 "requires [Test]"
  - `[Test][Timeout(milliseconds: 1000)][Timeout(milliseconds: 2000)]`
    → E0917 "applied more than once"
- [x] 7.3 `src/toolchain/test-runner/tests/timeout_per_test_test.rs` — NEW
  - 用 inline z42 源或 fixture 写 `[Test][Timeout(milliseconds: 50)]`
    void sleep_200ms() { Thread.Sleep(200); }
  - 编译到 .zbc 并 `run_one`：断言 `Outcome::Failed`，reason 含
    `timed out after 0.05s` 字样
- [x] 7.4 `src/libraries/z42.crypto/tests/ecdsa_secp256k1_vectors.z42`
  — MODIFY，`test_secp256k1_sign_verify_round_trip` 之上加
  `[Timeout(milliseconds: 60000)]`，作 in-tree 示范

## 阶段 8: 文档同步

- [x] 8.1 `docs/design/runtime/zbc.md` — Minor changelog 加一行
  （minor 新值 / 日期 / 触发 spec / 引入字段 `TestEntry.timeout_ms i32`）
- [x] 8.2 `docs/design/runtime/zpkg.md` — 同上（联动 zbc minor 的注解）
- [x] 8.3 `docs/design/testing/testing.md` —
  - "R-series 待落地" 表去掉 `[Timeout]` 行
  - 新增 §"Per-test timeout"（精简版 spec.md 内容）
  - 提一行 "attribute named-arg values support integer literals as
    of this spec"
- [x] 8.4 `src/libraries/z42.test/README.md` — 能力表加
  `[Timeout(milliseconds: N)]` 行

## 阶段 9: Fixture regen + GREEN + commit + archive

- [x] 9.1 `./src/tests/zbc-format/generate-fixtures.sh` 跑一遍；git
  diff 应只含 minor 字节 + 新 timeout_ms 字段（默认 0）
- [x] 9.2 `./src/tests/zpkg-format/generate-fixtures.sh` 跑一遍同上
- [x] 9.3 `./scripts/regen-golden-tests.sh --release` 把 stdlib + 测试
  zbc 一并 regen
- [x] 9.4 `dotnet test --filter "FullyQualifiedName~Z42.Tests.Zbc|FullyQualifiedName~Z42.Tests.Zpkg"`
  全绿（format invariant + golden）
- [x] 9.5 `./scripts/test-all.sh --parallel --jobs=4` 全绿
- [x] 9.6 commit + push（spec 文件 + 实现 + 文档 + fixture diff
  一并；单 commit）
- [x] 9.7 归档：`docs/spec/changes/add-test-timeout-attribute/` →
  `docs/spec/archive/YYYY-MM-DD-add-test-timeout-attribute/`
- [x] 9.8 push 归档 commit

## 备注

- 若实施期发现 `IntegerLiteral` token kind 名不同（z42 lexer 可能用
  `IntLit` / `Number` 等），停下确认；不在 spec 内 inline 改 token
  名（潜在 cross-cutting）
- 若发现 ExprParser 的整数解析 helper 是 `internal` / `private` 不便
  共享，就地在 `TopLevelParser.Helpers.cs` 实现一个小版本（只接十进制
  + `_` 分隔；attribute 值不需要 hex / binary 等扩展形态）。spec 内
  允许此小复制
- C# parser 数字字面量 i32 vs i64 表示：`AttributeArgInt.Value` 用
  `long` 容纳 lexer 产出，在 [Timeout] 消费侧再 narrow + E0917
  报溢出
- AttributeArgString 的 strip-quotes 行为必须**字节等价**旧实现，否则
  现有 `[Skip(reason: "x")]` 的 reason string 在 zbc 内会变（破坏
  format invariant tests）
