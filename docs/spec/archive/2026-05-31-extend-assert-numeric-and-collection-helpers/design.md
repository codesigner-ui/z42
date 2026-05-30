# Design: Numeric + array-collection Assert helpers

## Architecture

Pure additive change to one file:

```
src/libraries/z42.test/src/Assert.z42
├── existing (preserved verbatim):
│   ├── Equal / NotEqual
│   ├── True / False
│   ├── Null / NotNull
│   ├── Contains(string, string)        ← unchanged
│   ├── Fail / Skip
│   ├── Throws / ThrowsAny / DoesNotThrow
│   └── EqualApprox
└── added (this spec):
    ├── § Numeric ordering & range:
    │   ├── Greater(long, long)         + Greater(double, double)
    │   ├── Less(long, long)            + Less(double, double)
    │   ├── GreaterOrEqual(long, long)  + (double, double)
    │   ├── LessOrEqual(long, long)     + (double, double)
    │   └── InRange(long, long, long)   + (double, double, double)
    └── § Array collection helpers:
        ├── Contains(object, object[])   ← overload of existing string version
        ├── DoesNotContain(object, object[])
        ├── IsEmpty(object[])
        └── IsNotEmpty(object[])
```

No runtime / formatter / compiler dependency — all assertion logic
expressible in pure z42 using `throw new TestFailure(...)`. Builds and
ships through the existing `z42.test` zpkg build pipeline.

## Decisions

### Decision 1: Method-name style — `Greater` not `GreaterThan`

**问题**：xUnit-style `GreaterThan` vs short `Greater`?

**决定**：`Greater`. Why：
- z42 stdlib precedent: `NotEqual` (not `IsNotEqual`), `Contains` (not
  `IsContaining`), `Null` (not `IsNull`) — short forms throughout
- Reading `Assert.Greater(port, 0)` parses as "assert (port) greater than
  (0)"; `Assert.GreaterThan(port, 0)` adds an English connective without
  adding clarity
- 4-character savings × 10+ call sites per test ≈ noticeable signal-to-
  noise improvement in test code

### Decision 2: Argument order — `(actual, expected)` not `(expected, actual)`

**问题**：Mirror existing `Equal(expected, actual)`, or follow the natural
order of `Assert.Greater(actual, expected)` ("`actual` is greater than
`expected`")?

**决定**：For ordering helpers, `(actual, expected)` — reads left-to-right
as the asserted inequality. Why：
- `Assert.Greater(port, 0)` reads as "port is greater than 0" — natural
  direction
- `Assert.Greater(0, port)` would mean "0 is greater than port" which is
  not the typical assertion
- xUnit takes the same direction for `Assert.True`-style ordering
  assertions; FluentAssertions `port.Should().BeGreaterThan(0)` confirms

**Trade-off**: this is *inconsistent* with `Assert.Equal(expected,
actual)` (where expected comes first). We accept the asymmetry because
"equality is symmetric, ordering isn't" — the order matters for ordering
helpers, doesn't for Equal. Documented inline.

### Decision 3: InRange bounds are inclusive

**问题**：`InRange(x, min, max)` — bounds inclusive or exclusive?

**决定**：Inclusive. `min <= x <= max`. Why：
- Most observed workarounds are `Assert.True(x > 3.1415 && x < 3.1416)`
  (half-open in spirit but written without thinking) — both intents are
  served by inclusive bounds when the caller provides tight values
- xUnit `Assert.InRange` is inclusive
- Half-open is mathematically cleaner but counter-intuitive for the
  common "is this number close to X" use case
- If users need exclusive, they can call `Greater` + `Less` separately

### Decision 4: Float ordering uses plain `<`/`>`, not tolerance

**问题**：For `Greater(double, double)`, should the comparison apply an
epsilon tolerance (like `EqualApprox` does)?

**决定**：No tolerance. Plain `<` / `>`. Why：
- `Greater` semantically asks "is A strictly larger than B?". Adding
  tolerance creates a half-equal-half-ordering hybrid that's hard to
  reason about
- Users who want "actual ≥ expected within tolerance" can call
  `EqualApprox(actual, expected, eps)` and trust `Greater` for strict
  cases
- Matches all other test frameworks' behavior (xUnit, JUnit, pytest)
- For range checks where tolerance is wanted, the user controls it by
  passing tightened `min` / `max`: `InRange(x, 3.0 - eps, 3.0 + eps)`
  is the equivalent expressive form

### Decision 5: Array-only collection helpers — defer List / Set / Dict

**问题**：Why not `IsEmpty(List)` / `Contains(K, Dictionary<K,V>)`?

**决定**：Phase 1 z42 has no generics, so `IsEmpty<T>(IList<T>)` can't
be expressed. Three non-generic alternatives:

1. **Per-type overloads** — `IsEmpty(List)`, `IsEmpty(Set)`, … one per
   container. Forces every new collection type to extend Assert; doesn't
   scale
2. **`object` typed + reflection** — `IsEmpty(object)` and runtime-
   dispatch on type. Requires dynamic invoke of `.IsEmpty()` / `.Count()`
   which z42 doesn't ergonomically support
3. **`object[]` only** — covers the most common case (arrays are the
   default literal in tests); other collection types stay as
   `Assert.True(coll.IsEmpty())` until generics land

**Decision: option 3**. Why：
- 6/6 observed `IsEmpty()` workarounds wrap collections that have an
  array-equivalent (`pq.ToArray()`, `list.ToArray()`); users either
  convert or fall back to `Assert.True`
- L2 generics is on the roadmap; future spec
  `extend-assert-generic-collection-helpers` can add the trait-bound
  versions without renaming existing methods (overload resolution adds
  generic on top of `object[]`)
- Trade-off: not fully closing the gap. Acceptable for v1 because
  partial closure with simple types > full closure delayed to L2

### Decision 6: `Contains(object, object[])` uses default equality

**问题**：How to determine "needle is in haystack"?

**决定**：Use the standard z42 `==` operator (reference-equal for
objects, value-equal for boxed primitives, deep-equal for strings).
Same semantics as `List.Contains`, `string.Contains`, etc. — matches
caller's intuition from elsewhere in stdlib. Loop is just:

```z42
for (var item in haystack) {
    if (item == needle) { return; }      // hit — assert passes
}
throw new TestFailure(...);              // miss — fail
```

No `Equals(object)` virtual dispatch needed; `==` already does the right
thing for the common types tests exercise (int, string, bool, refs).

## Implementation Notes

### Failure message format

Adopt the existing style — sentence + 'actual' + 'expected' triplet fed
to the `TestFailure(message, actual, expected)` ctor so JSON / pretty
output can render the typed values separately:

```z42
public static void Greater(long actual, long expected) {
    if (actual <= expected) {
        throw new TestFailure(
            $"expected {actual} > {expected}",
            actual.ToString(),
            $"> {expected}");
    }
}
```

For `InRange`:

```z42
public static void InRange(long actual, long min, long max) {
    if (actual < min || actual > max) {
        throw new TestFailure(
            $"expected {actual} in [{min}, {max}]",
            actual.ToString(),
            $"[{min}, {max}]");
    }
}
```

For `IsEmpty(object[])`:

```z42
public static void IsEmpty(object[] coll) {
    if (coll.Length != 0) {
        throw new TestFailure(
            $"expected empty array but length = {coll.Length}",
            coll.Length.ToString(),
            "0");
    }
}
```

For `Contains(object, object[])`:

```z42
public static void Contains(object needle, object[] haystack) {
    for (var item in haystack) {
        if (item == needle) { return; }
    }
    throw new TestFailure(
        $"array does not contain expected element",
        $"<array of length {haystack.Length}>",
        needle.ToString());
}
```

### Overload disambiguation

The string `Contains(string, string)` and array `Contains(object, object[])`
overloads are distinguished by parameter types. z42's overload resolver
handles standard arity + type matching (verified via existing `Equal(int,
int)` / `Equal(object, object)` overloads elsewhere in stdlib). No naming
trick required.

### Double overload tie-breaking

When both `Greater(long, long)` and `Greater(double, double)` exist and
the user calls `Assert.Greater(3, 5)` (int literals), z42 picks the long
overload (integer literal default type). For `Assert.Greater(3.0, 5)`
(mixed), the double overload wins via standard implicit-widening rules.
Same pattern as `Equal` already handles.

## Testing Strategy

### Unit tests — `assert_numeric_helpers.z42`

Per assertion, three test cases:

| Assertion | Pass-path | Fail-path (Assert.Throws TestFailure) | Edge case |
|-----------|-----------|----------------------------------------|-----------|
| `Greater(long, long)` | `Greater(5, 3)` | `Greater(3, 3)` (equal) → fail; `Greater(3, 5)` → fail | INT_MAX boundary |
| `Greater(double, double)` | `Greater(0.1, 0.0)` | `Greater(0.0, 0.0)` → fail | NaN: `Greater(NaN, 0.0)` should fail (NaN-comparisons are false) |
| `Less` (long + double) | symmetric | symmetric | symmetric |
| `GreaterOrEqual` / `LessOrEqual` | equal case passes | strict-violation fails | NaN |
| `InRange(long, long, long)` | `InRange(5, 0, 10)` | `InRange(-1, 0, 10)` → fail; `InRange(11, 0, 10)` → fail | boundary inclusive: `InRange(0, 0, 10)` & `InRange(10, 0, 10)` both pass |
| `InRange(double, double, double)` | analog | analog | NaN: `InRange(NaN, 0.0, 10.0)` → fail |

### Unit tests — `assert_collection_helpers.z42`

Per assertion:

| Assertion | Pass-path | Fail-path | Edge case |
|-----------|-----------|-----------|-----------|
| `Contains(object, object[])` | `Contains(2, [1,2,3])` | `Contains(4, [1,2,3])` → fail | empty array always fails |
| `DoesNotContain` | `DoesNotContain(4, [1,2,3])` | `DoesNotContain(2, [1,2,3])` → fail | empty array always passes |
| `IsEmpty(object[])` | `IsEmpty([])` | `IsEmpty([1])` → fail | 0-element vs 1-element distinguished |
| `IsNotEmpty(object[])` | `IsNotEmpty([1])` | `IsNotEmpty([])` → fail | symmetric |

Coverage: each test exercises `int[]`, `string[]`, `object[]` to confirm
boxing flows through correctly.

Run via `./scripts/test-stdlib.sh z42.test` — both new files land
alongside `dogfood.z42` in the same wave.

## Risks

- **Overload selection ambiguity** — `Assert.Greater(3, 5.0)` mixes int +
  double. Compiler should widen to double overload. If the existing
  overload resolver fails (no implicit widening on call site), tests
  catch this; fallback is rename to `GreaterI` / `GreaterF` (ugly, last
  resort).
- **`Contains(object, object[])` shadows `Contains(string, string)`?** —
  Overloads distinguished by 2nd param type (`string` vs `object[]`).
  Need to verify z42 compiler doesn't get confused by `object` being
  parent of `string`. Mitigation: unit tests verify both overloads
  reachable by passing canonical-typed literals.
- **NaN double comparisons** — IEEE-754 says NaN comparisons are always
  false. `Greater(NaN, 0.0)` evaluates `NaN <= 0.0` as false → no
  exception → assertion *passes*, which is wrong. Need explicit NaN
  guard: `if (Double.IsNaN(actual) || Double.IsNaN(expected)) { throw }`.
  Listed as edge case in test matrix.
