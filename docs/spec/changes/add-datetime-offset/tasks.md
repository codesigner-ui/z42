# Tasks: add Std.Time.DateTimeOffset

> 状态：🔴 **BLOCKED** — TypeChecker E0402 bug on new files referencing cross-file sibling types (`TimeZone`, `TimeSpan`). | 创建：2026-06-02 | 类型：stdlib feat

## 阻塞原因

写第一行实现代码后立即触发 z42 编译器 bug，与 `project_scripts_z42_port.md`
备忘中记录的"数组索引方法调用的返回类型 vs 显式类型注解 unification 失败"
属同一 E0402 类（"expected X, got X" 同名异身），但触发面不同：

- **触发条件**：在已有包（如 `z42.time`）中新增 `.z42` 源文件，新文件引用
  同包内的 sibling 类型（field 类型、方法参数类型、方法返回类型）。
- **现象**：编译报错 `expected <T>, got <T>`（同名）；4 个不同行号、不同
  上下文（field-arg / instance-method-return / `var` 推断）均报同一 bug。
- **复现最小用例**（已实测）：

  ```z42
  namespace Std.Time;
  public sealed class TestThing {
      private DateTime _utc;
      public TestThing(DateTime utc) { this._utc = utc; }
      public string Foo() {
          TimeZone tz = TimeZone.Utc();
          return this._utc.ToIso8601With(tz);   // ← E0402 expected TimeZone, got TimeZone
      }
  }
  ```

- **不复现于**：同包既有文件（`DateTime.z42` / `Stopwatch.z42` 均含同
  pattern 调用且全绿）。怀疑是 metadata 加载 / TypeDesc 重建路径在
  incremental compile 边界给新文件分配了不同 type identity。
- **尝试过的 workaround（全部失败）**：
  - `using Std;` 删除
  - 显式 local 变量中转（`TimeZone tz = this._tz;` → `f(tz)`）
  - `var` 类型推断
  - 重命名类（`DateTimeOffset` → `OffsetDateTime`）
  - 重命名文件（强制末位字母序加载）
  - 全清 `artifacts/build/` 重新编译
- **触发可能根因**：最近 metadata refactor (`272b0115` `Box<[Value]>` /
  `06b57853` NameIndex) 引入了新文件的 type identity 重建路径与既有路径
  分歧；需 z42c TypeChecker 维护方调查。
- **建议先行动作**：开独立 `fix-typechecker-newfile-sibling-type-identity`
  spec 修 TypeChecker；此 spec 在 fix 落地后立即可恢复。

## 进度概览
- [ ] 阶段 1: Class skeleton + ctor + factories（**阻塞**）
- [ ] 阶段 2: Accessors + arithmetic + comparisons
- [ ] 阶段 3: Tests
- [ ] 阶段 4: Doc sync + verify + archive

## 阶段 1: Class + ctor + factories
- [ ] 1.1 Create `src/libraries/z42.time/src/DateTimeOffset.z42`
- [ ] 1.2 Fields `_utc: DateTime` + `_tz: TimeZone`
- [ ] 1.3 Constructor `(DateTime utc, TimeZone tz)` — rejects null
      for either argument (`ArgumentNullException`)
- [ ] 1.4 Factory `Now(TimeZone tz)`
- [ ] 1.5 Factory `FromLocal(y, mo, d, h, mi, s, ms, tz)` —
      `DateTime.Utc(...)` then subtract offset minutes (so the
      passed fields are interpreted as wall clock in `tz`)
- [ ] 1.6 Factory `Parse(string)` — first call
      `DateTime.ParseIso8601(s)` for the UTC instant; then scan `s`
      backwards for the offset suffix (`Z` / `±HH:MM` / `±HHMM`)
      and decode to minutes; throw `FormatException` on missing
      suffix

## 阶段 2: Accessors + arithmetic + comparisons
- [ ] 2.1 `UtcDateTime()` / `LocalDateTime()` / `Offset()`
- [ ] 2.2 `Year/Month/Day/Hour/Minute/Second/Millisecond/DayOfWeek/
      DayOfYear` — delegate to `LocalDateTime()`
- [ ] 2.3 `ToIso8601()` — `UtcDateTime().ToIso8601With(Offset())`
- [ ] 2.4 `override ToString()` — same as `ToIso8601()`
- [ ] 2.5 `Equals(DateTimeOffset)` — UTC-only; `null` → false
- [ ] 2.6 `EqualsExact(DateTimeOffset)` — UTC AND offset minutes
- [ ] 2.7 `IsAfter / IsBefore(DateTimeOffset)`
- [ ] 2.8 `Subtract(DateTimeOffset) → TimeSpan`
- [ ] 2.9 `Add(TimeSpan) → DateTimeOffset` (preserves offset)
- [ ] 2.10 `SubtractSpan(TimeSpan) → DateTimeOffset`

## 阶段 3: Tests
- [ ] 3.1 Create `src/libraries/z42.time/tests/datetime_offset.z42`
- [ ] 3.2 `test_construct_stores_utc_and_offset`
- [ ] 3.3 `test_construct_null_dt_throws`
- [ ] 3.4 `test_construct_null_tz_throws`
- [ ] 3.5 `test_local_datetime_applies_offset`
- [ ] 3.6 `test_local_field_accessors_match_offset`
- [ ] 3.7 `test_from_local_computes_utc`
- [ ] 3.8 `test_parse_utc_suffix_z`
- [ ] 3.9 `test_parse_positive_offset`
- [ ] 3.10 `test_parse_negative_offset`
- [ ] 3.11 `test_parse_no_offset_throws`
- [ ] 3.12 `test_iso8601_round_trip_positive_offset`
- [ ] 3.13 `test_iso8601_round_trip_negative_offset`
- [ ] 3.14 `test_equals_same_utc_different_offset_true`
- [ ] 3.15 `test_equals_exact_same_utc_different_offset_false`
- [ ] 3.16 `test_equals_null_returns_false`
- [ ] 3.17 `test_is_after_by_utc`
- [ ] 3.18 `test_subtract_returns_timespan`
- [ ] 3.19 `test_add_timespan_preserves_offset`
- [ ] 3.20 `test_now_returns_recent_utc`

## 阶段 4: Doc sync + verify + archive
- [ ] 4.1 `docs/design/stdlib/time.md`: mark
      `time-future-datetime-offset` ✅, add brief description
- [ ] 4.2 `docs/design/stdlib/roadmap.md`: update row status
- [ ] 4.3 `src/libraries/z42.time/README.md`: add `DateTimeOffset.z42`
- [ ] 4.4 `./scripts/test-stdlib.sh z42.time` — green
- [ ] 4.5 `./scripts/test-all.sh` — full GREEN
- [ ] 4.6 Move dir to `docs/spec/archive/2026-06-02-add-datetime-offset/`
- [ ] 4.7 Commit + push
