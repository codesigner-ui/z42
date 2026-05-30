# Design: Surface test-failure source location in runner output

## Architecture

```
┌───────────────────────────────────────────────────────────┐
│ Interp throws ─► populate_stack_trace(value, ctx, module)│
│                  (already wired since 2026-05-10)         │
│                  Exception.StackTrace = "  at A (f.z42:7) │
│                                          \n  at B …"     │
└───────────────────────────┬───────────────────────────────┘
                            │ ExecOutcome::Thrown(value)
                            ▼
┌───────────────────────────────────────────────────────────┐
│ runner::classify_thrown(value)                            │
│   → format_failure_with_stack(value, module)              │
│         ├─ Message     ── pull via field_index["Message"] │
│         ├─ StackTrace  ── exception::read_stack_trace()   │
│         └─ first_user_frame(stack) ── filter framework    │
│     returns FailureDetails {                              │
│       message: "TestFailure: values not equal",           │
│       primary_location: Some("my_test.z42:42"),           │
│       stack_trace: Some("  at MyTests.test_arithmetic …") │
│     }                                                     │
└───────────────────────────┬───────────────────────────────┘
                            ▼
┌───────────────────────────────────────────────────────────┐
│ Outcome::Failed { reason, location, stack_trace }         │
│        ─► TestResult { ..., failure_location,             │
│                         stack_trace }                     │
└───────────────────────────┬───────────────────────────────┘
        ┌───────────────────┼─────────────────────┐
        ▼                   ▼                     ▼
    pretty.rs            tap.rs                json.rs
   ✗ name (loc)       not ok N - name        { "reason",
     reason            ---                     "failure_location",
     stack             message: ...            "stack_trace" }
                       location: ...
                       stack: |
                         ...
                       ...
```

The runner already has a `Module` reference via `LoadedRunner.ctx.module()` —
needed because `read_stack_trace` walks the type_registry to confirm the
thrown value is an Exception subclass. Pass it through to the format
helper.

## Decisions

### Decision 1: Reason field vs separate location field

**问题**：把 location 拼进 `reason` 字符串，还是单独成字段？

**决定**：单独 `failure_location: Option<String>` + `stack_trace: Option<String>` 字段。
Why：
- 既有 CI 解析脚本（grep 出 `reason` 字段的）继续工作 — backward compat
- JSON consumers 想做 IDE jump-to-source 时，独立字段省去自己 regex
- TAP YAML 也能干净放 `location:` key 而不污染 `message:`
- 用户屏幕上看到的是 formatter 合成的复合显示，但底层 data 仍分层

### Decision 2: First-user-frame extraction algorithm

**问题**：栈里同时有 framework 帧（`Std.Test.Assert.Equal` → `Std.Test.AssertCore.checkEqual`
→ throw）和用户帧（`MyTests.test_arithmetic`）。哪一帧标为 "primary location"？

**决定**：扫帧串、第一条**非** framework 帧的 `(file:line)` 部分 = primary。
Framework 帧的判定：

| 帧 `func_name` 模式 | 视为 framework |
|---------------------|----------------|
| 以 `Std.Test.` 开头 | 是 |
| 含有 `.Assert.` 子串 | 是 (兼容用户自定义 namespace `MyApp.Asserts` 的 `.Assert.` 不会误命中：要求**点号前后**都有内容) |
| 其他 | 否 |

Why：
- 用户写 `Assert.Equal(2, 3)` 时，关心的位置是**我**调它的那一行，不是
  Assert 内部的 `throw new TestFailure(...)` 在 Assert.z42 里第几行
- 退化情况：stdlib 自测里整个 stack 都是 framework 帧 → 不显示
  primary location（formatter 退回到 reason + full stack 显示）

**边界 corner case**：z42 没 `[Caller*]` attr，所以 user 的 test 方法本身
也会出现在栈中。Assert 框架帧总在栈顶（最新），user 帧在它们下方。"第一条
非 framework 帧"语义自然命中 user test 方法的 caller-of-Assert 行。

### Decision 3: Stack trace truncation / framework filter for full trace

**问题**：full stack trace 也要过滤 framework 帧吗？

**决定**：**不**过滤 — primary_location 提供 "1 行扫眼" 视图，full stack
保留全部帧（含 framework）给 deep-debug 需求。Why：
- 偶尔 Assert 内部 bug 导致 throw（不是 user assertion failure），完整 stack
  让 maintainer 一眼看到 framework 出错位置
- 用户写的 helper method (`fn assertResult(r)`) 不是 Std.Test.* 但实际功能
  像 Assert wrapper — 过滤误删，"primary 提取" 给 60-80% 用例 + 完整 stack
  给剩余 20-40% 是合理 Pareto

### Decision 4: Pretty formatter line layout

**问题**：location / stack / reason 三段如何排版？

**决定**：

```
  ✗ MyTests.test_arithmetic  (my_test.z42:42)
      TestFailure: values not equal (expected 3, actual 2)
      stack:
        at MyTests.test_arithmetic (my_test.z42:42)
        at Std.Test.Assert.Equal (Assert.z42:38)
        at Std.Test.AssertCore.checkEqual (AssertCore.z42:17)
```

- 第一行：test 名 + primary location 内联，用括号视觉分组
- 第二行起：reason 单条/多条，缩进 4 spaces（沿用现有 pretty 缩进）
- `stack:` 引导一段 indented stack 行，颜色 dim（视觉降权，主要排查靠
  reason + primary location）
- location 缺席时退回到现行 `✗ name` 单行 + reason

Why 不留 `--no-stack` flag：v1 红测试场景用户主动想看 detail；噪声主要
来自全绿 run 而那里根本没 fail output。

### Decision 5: TAP YAML diagnostic block schema

**问题**：YAML keys 加哪几个？

**决定**：

```yaml
not ok 3 - MyTests.test_arithmetic
  ---
  message: 'TestFailure: values not equal (expected 3, actual 2)'
  location: 'my_test.z42:42'
  stack: |
    at MyTests.test_arithmetic (my_test.z42:42)
    at Std.Test.Assert.Equal (Assert.z42:38)
  ...
```

- `message:` 保留旧名，内容不变（backward compat）
- `location:` 新增可选 key，缺席时不写
- `stack:` 用 YAML `|` literal block，缩进 4 spaces

Why YAML literal block：multi-line stack 一行一行 `yaml_escape` 拼成
"line1 line2 line3" 会粘成一团没法读；literal block 是 TAP 13 + YAML 1.2
都原生支持的多行表达。

### Decision 6: JSON output schema

**问题**：加哪几个 fields？

**决定**：

```json
{
  "name": "MyTests.test_arithmetic",
  "status": "failed",
  "duration_ms": 7,
  "reason": "TestFailure: values not equal (expected 3, actual 2)",
  "failure_location": "my_test.z42:42",
  "stack_trace": "  at MyTests.test_arithmetic (my_test.z42:42)\n  at Std.Test.Assert.Equal (Assert.z42:38)"
}
```

- `reason` 不变（backward compat）
- 新字段都用 `#[serde(skip_serializing_if = "Option::is_none")]`，passed/skipped
  tests 不污染输出
- `failure_location` 而非 `location`：避免与未来可能的 "test method
  declaration location" 字段冲突（前者是 throw site，后者是 method
  decl site）

## Implementation Notes

### `format_failure_with_stack` 签名

```rust
pub(crate) struct FailureDetails {
    pub message: String,                  // backward-compat reason
    pub primary_location: Option<String>, // first non-framework frame
    pub stack_trace: Option<String>,      // full multi-line trace
}

pub(crate) fn format_failure_with_stack(
    val: &Value,
    module: &Module,
) -> FailureDetails;
```

Returns even when fields are missing — caller may have a Value::Str throw
or non-Exception object that has no StackTrace; degrade gracefully.

### `first_user_frame` parser

Input: multi-line stack trace string from `format_stack_trace`. Lines
match `^  at (?P<func>[^\s]+)(?:\s+\((?P<file>[^:)]+):(?P<line>\d+)(?::\d+)?\))?$`.

For each line:
1. Extract func name (between `at ` and `(` or EOL)
2. Test `is_framework_frame(func)` (Decision 2)
3. First non-framework → return `Some("<file>:<line>")` if file present, else None
4. All framework → return None

No regex crate dependency — `str::splitn` and `str::strip_prefix` suffice.
Stack format is z42-internal so we own both producer and consumer.

### Outcome::Failed migration

```rust
pub enum Outcome {
    Passed { duration_ms: u64 },
    Failed {
        reason: String,
        location: Option<String>,
        stack_trace: Option<String>,
    },
    Skipped { reason: String },
}
```

Every construction site needs updating (~10 places). The two main ones —
classify_thrown / classify_should_throw — get the new helper. Setup/Teardown
error paths can pass `location: None, stack_trace: None` since those don't
go through a thrown z42-side exception.

### TestResult adds two fields

```rust
pub struct TestResult {
    pub name: String,
    pub status: TestStatus,
    pub duration_ms: u64,
    #[serde(skip_serializing_if = "Option::is_none")] pub reason: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")] pub failure_location: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")] pub stack_trace: Option<String>,
}
```

## Testing Strategy

### Unit tests (`runner_tests.rs`)

Per `format_failure_with_stack` + `first_user_frame`:

| Case | Value shape | Expected FailureDetails |
|------|-------------|-------------------------|
| 1 — Value::Str throw | `Value::Str("oops")` | message="oops", location=None, stack=None |
| 2 — non-Exception object | object w/o Message field | message="TypeName{...}", location=None, stack=None |
| 3 — Exception, no StackTrace field | object w/ Message only | message="Type: msg", location=None, stack=None |
| 4 — Exception w/ null StackTrace | object w/ Message + StackTrace=null | same as 3 |
| 5 — Exception w/ stack, no user frames | stack = "  at Std.Test.Assert.Equal (Assert.z42:38)" | message="Type: msg", location=None, stack=Some(full) |
| 6 — Exception w/ stack, user frame present | stack = framework lines + "  at MyTests.test_x (mt.z42:42)" | message="Type: msg", location=Some("mt.z42:42"), stack=Some(full) |
| 7 — Stack without (file:line) suffix | frame "  at Foo" alone | location=None even if non-framework |
| 8 — Mixed `.Assert.` substring frames | "  at MyApp.AssertUtils.foo (mu.z42:10)" 后跟 user frame | depends on rule precision; design.md says `.Assert.` middle qualifies as framework → MyApp.AssertUtils matches → skipped → next user frame becomes primary |

Plus `first_user_frame` standalone:

| Input | Expected |
|-------|----------|
| Empty string | None |
| Single framework line | None |
| Single user line w/ location | Some("file:line") |
| Two lines, framework then user | Some(user's location) |
| Frame without `at ` prefix (malformed) | None for that line; scan continues |

### E2E demo

`src/libraries/z42.test/tests/failure_location_demo.z42`:

A `[ShouldThrow<TestFailure>]` test that calls `Assert.Equal(2, 3)` —
runner expects throw, passes. The throw's stack trace populates
TestFailure.StackTrace. To **prove** the stack is populated (not just that
the test passes), separately inspect inside the test body: catch and
re-throw or use direct read…

Actually simpler approach: **2 tests**:
1. `test_assertion_includes_stack` — calls `Assert.Equal(2, 3)` directly
   wrapped in `[ShouldThrow<TestFailure>]`. Body inverts: catches the
   TestFailure via direct throw, checks `e.StackTrace.Contains("Assert.z42")`
   AND `e.StackTrace.Contains("failure_location_demo.z42")`. If stack is
   populated correctly, this passes.

   Wait — can z42 catch an exception thrown by Assert.Equal? Need try/catch.
   If yes, this works inside `[Test]` (no ShouldThrow needed).

2. `test_runner_surfaces_stack` — pure dogfood. Just `[Test]` + `Assert.Equal`
   that PASSES (so test passes). The actual verification that runner
   surfaces stack is by reading test-stdlib.sh wave output post-facto,
   manually. Auto-verification of "runner output contains stack" needs
   subprocess/output capture which is too brittle for in-tree dogfood —
   leave to one-off manual or future spec.

So e2e covers (1) only — verifies the *runtime* populates trace; the
*formatter* upgrades are verified by unit tests + manual inspection.

## Risks

- **Stack trace parser brittleness** — depends on
  `format_stack_trace`'s exact output. If runtime team changes "  at X
  (Y:Z)" format, parser breaks silently (returns no primary). Mitigation:
  unit test 5 + 6 + 7 trip immediately; add invariant test that
  `format_stack_trace` of a known frame parses back via `first_user_frame`.
- **Outcome::Failed signature break** — every Outcome::Failed construction
  needs updating (compiler will catch all; Rust enum variants are checked
  at use sites).
- **TAP YAML literal block formatting** — incorrect indentation breaks
  YAML parsing. Unit test in tap.rs validates exact byte output.
