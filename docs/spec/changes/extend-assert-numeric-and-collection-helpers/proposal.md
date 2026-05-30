# Proposal: Extend `Std.Test.Assert` with numeric-comparison + array-collection helpers

## Why

Surveying stdlib test files turned up **20+ instances** across `z42.math`,
`z42.toml`, `z42.net`, `z42.collections`, `z42.json` where the test author
worked around a missing assertion by writing one of:

| Workaround pattern | Observed count | Example |
|--------------------|----------------|---------|
| `Assert.True(x > N)` for numeric ordering | 10+ | `Assert.True(port > 0);` (z42.net/tests/tcp_listener_factory.z42) |
| `Assert.True(x > low && x < high)` for range | 4+ | `Assert.True(v.AsDouble() > 3.1415 && v.AsDouble() < 3.1416);` (z42.toml/tests/parse_numeric_bases.z42:153) |
| `Assert.True(coll.IsEmpty())` / `Assert.False(coll.IsEmpty())` | 8 | priority_queue, linkedlist tests |
| `Assert.True(coll.Contains(x))` / `Assert.Equal(true, …)` | 6+ | z42.collections/tests/sorted_set.z42, z42.json/tests/json_path.z42:21 |

Three problems with these workarounds:

1. **Lossy failure messages** — `Assert.True(false)` says `"expected true
   but got false"`; the user gets no value to debug from. A real
   `Assert.Greater(actual, expected)` would say `"expected 0 > 5 but
   condition failed (actual=0, threshold=5)"`.
2. **Compound expressions hide intent** — `Assert.True(x > 3.1415 &&
   x < 3.1416)` mixes "ordering" + "logical AND" + "range" semantics into
   a bool. `Assert.InRange(x, 3.1415, 3.1416)` makes intent self-evident.
3. **`Assert.True(collection.IsEmpty())` is a double-negative** — the
   method is `IsEmpty()`, the assertion wraps it in `True`. A direct
   `Assert.IsEmpty(coll)` reads naturally.

Spec adds the 9 most-impactful methods identified by usage survey,
constrained to forms achievable in z42 Phase 1 (no generics — use
`object` / concrete array types).

## What Changes

Add to `Std.Test.Assert` (z42.test) — **no changes to z42.core's `Std.Assert`** per
the documented prelude-independence rule (`Assert.z42:8-19`):

### Numeric comparisons (long + double overloads each)

1. `Greater(long actual, long expected)` / `Greater(double, double)` — fails when `actual <= expected`
2. `Less(long actual, long expected)` / `Less(double, double)` — fails when `actual >= expected`
3. `GreaterOrEqual(long, long)` / `GreaterOrEqual(double, double)`
4. `LessOrEqual(long, long)` / `LessOrEqual(double, double)`
5. `InRange(long actual, long min, long max)` / `InRange(double, double, double)` — fails when `actual < min || actual > max` (inclusive bounds; opt for clarity over the half-open question)

### Array-collection helpers (object[] only — generic List<T> needs L2)

6. `Contains(object needle, object[] haystack)` — overload of the existing string-string `Contains`
7. `DoesNotContain(object needle, object[] haystack)` — symmetric
8. `IsEmpty(object[] collection)`
9. `IsNotEmpty(object[] collection)`

**Failure messages** include the actual values and the asserted relation,
mirroring the existing `Equal` / `NotEqual` style. Each throws structured
`TestFailure` with `actual` + `expected` fields populated so JSON
output / IDE integration get the typed values.

## Scope (允许改动的文件)

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.test/src/Assert.z42` | MODIFY | 在已有 `Throws` 段之后插入 § "ordering / range" 和 § "collection helpers"，新增 9 类共 14 个方法（5 numeric × {long, double} + 4 array helpers — `Contains`/`DoesNotContain` 与现有 string `Contains` 重载，name 不冲突，参数类型区分）|
| `src/libraries/z42.test/tests/assert_numeric_helpers.z42` | NEW | per-method 单元测试：每个 Assertion 各 2 case（pass-path + fail-path 用 `Assert.Throws("TestFailure", () => ...)` 验证 throw） + 1 Edge case（infinity / NaN for floats, empty/-1 for InRange edges） |
| `src/libraries/z42.test/tests/assert_collection_helpers.z42` | NEW | 同上 for Contains / DoesNotContain / IsEmpty / IsNotEmpty。覆盖 `int[]` / `string[]` / `object[]` 三种 element type，确认 boxing 兼容 |
| `src/libraries/z42.test/README.md` | MODIFY | "Assert 基础" 行扩展为 "(13 方法)" → "(22 方法)"；新增 "Assert 数值比较 (5 family × 2 overload)" 与 "Assert 数组集合助手 (4 方法)" |
| `docs/design/testing/testing.md` | MODIFY | (用法) § "Assert API quick reference" — 列表化展示全部 22 方法分组（Equality / Boolean / Null / Numeric ordering / Range / String / Array / Exception / Float approx / Control）+ rewrite-example for each new method；(设计思路) § "Why numeric comparison helpers" + § "Why array-only collection helpers"：解释为什么不加 List/Set 泛型版（需要 L2 generics），未来扩展路径 |

**只读引用：**

- `src/libraries/z42.test/src/Failure.z42:24-53` — `TestFailure` ctor 形态
- `src/libraries/z42.core/src/Assert.z42` — 不修改，但确认 Greater/Less/InRange 等不存在于 z42.core（避免 DepIndex 解析时 first-wins 命中 z42.core 抛 plain Exception）

## Out of Scope

- **修改 z42.core 的 `Std.Assert`** — 违反 prelude-independence；本 spec
  只动 z42.test
- **泛型 collection helpers** (`Contains<T>(T, IList<T>)`, `IsEmpty<T>(ICollection<T>)`) — 需要 L2 泛型；本 spec 只支持 `object[]`。这覆盖 stdlib 测试里 6+ 个 workaround，剩余 List / Set / Dictionary 工作流留待 generics-enabled future spec
- **`StartsWith` / `EndsWith` 字符串断言** — observed 0 instances；按 YAGNI 跳过
- **`HasCount(n, coll)`** — observed 5+ instances via `Assert.Equal(N, coll.Count())`，但需要泛型 collection；同上未来 spec
- **`ContainsKey` / `ContainsValue` dictionary helpers** — 同上
- **修改 `EqualApprox` 接口** — 已有；保持不动
- **TIDX format / runner / formatter 改动** — 纯 z42.test 库扩展

## Open Questions

- [x] **已裁决**：方法名 `Greater` / `Less` 而非 xUnit 的 `GreaterThan` /
      `LessThan` — z42 命名风格 PascalCase 短形（`NotEqual` vs `IsNotEqual`，
      `Contains` 不是 `IsContaining`），短名简洁
- [x] **已裁决**：`InRange(actual, min, max)` 边界**包含**（`actual >= min &&
      actual <= max` 才通过）— C# `InRange` / xUnit 同语义；半开区间不直觉
- [x] **已裁决**：double overload 用绝对比较（`<` / `>`），**不复用 EqualOrApprox
      公差**。需要容差时使用 `EqualApprox`；range 检查时容差通过收/放 min/max
      表达。避免把"容差"语义偷塞进 ordering helper
- [x] **已裁决**：collection helpers 仅 `object[]` 不引入 `List` / `Set` —
      stdlib 测试里多数已通过 `array` literal 或 ToArray() 构造，覆盖率
      可观；List 等留给泛型化后引入
- [x] **已裁决**：`Contains(object, object[])` 用 `==` 运算符比对（z42 默认
      reference-equality for object，value-equality for boxed primitives；
      与 List<T>.Contains 等 stdlib 现有语义一致）
