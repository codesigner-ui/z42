# Tasks: Extend Std.Test.Assert with numeric + array helpers

> 状态：🟢 已完成 | 完成：2026-05-31 | 创建：2026-05-30 | 类型：stdlib (z42.test library extension)

## 进度概览

- [x] 阶段 1: `Assert.z42` — 5 numeric × {long, double} = 10 methods + 4 array helpers
- [x] 阶段 2: 单元测试 `assert_numeric_helpers.z42` (5 × 3 case = 15+)
- [x] 阶段 3: 单元测试 `assert_collection_helpers.z42` (4 × 3 case = 12+)
- [x] 阶段 4: 文档（用法 + 设计思路）
- [x] 阶段 5: GREEN + commit + archive

## 阶段 1: Assert.z42 实现

### 1.A Numeric ordering & range

- [x] 1.1 `Greater(long actual, long expected)` — fails when `actual <= expected`
- [x] 1.2 `Greater(double actual, double expected)` — same + NaN guard
- [x] 1.3 `Less(long, long)` + `Less(double, double)`
- [x] 1.4 `GreaterOrEqual(long, long)` + `(double, double)`
- [x] 1.5 `LessOrEqual(long, long)` + `(double, double)`
- [x] 1.6 `InRange(long actual, long min, long max)` — inclusive bounds
- [x] 1.7 `InRange(double actual, double min, double max)` — inclusive + NaN guard

### 1.B Array collection helpers

- [x] 1.8 `Contains(object needle, object[] haystack)` — overload with existing string Contains
- [x] 1.9 `DoesNotContain(object needle, object[] haystack)`
- [x] 1.10 `IsEmpty(object[] collection)`
- [x] 1.11 `IsNotEmpty(object[] collection)`

### 1.C 头部注释更新

- [x] 1.12 Assert.z42 类文件顶端注释 § "仍未支持" 段去除"集合 Contains
  泛型版"（已部分覆盖 via `object[]`）；保留"`Throws<E>` 真正泛型"等
- [x] 1.13 加新 § 注释解释 numeric helpers 何时用 Greater vs EqualApprox
  （strict ordering 用 Greater / Less，"close to" 用 EqualApprox）

## 阶段 2: assert_numeric_helpers.z42 单元测试

- [x] 2.1 NEW `src/libraries/z42.test/tests/assert_numeric_helpers.z42`
  - namespace `Z42TestAssertNumericHelpers`
  - `using Std; using Std.Test;`
- [x] 2.2 per method 3 cases — pass / fail (verified via
  `Assert.Throws("TestFailure", () => Assert.Greater(...))`) / edge
- [x] 2.3 cases by method:
  - `test_greater_long_pass / _fail / _edge_int_max`
  - `test_greater_double_pass / _fail / _nan` (NaN must fail)
  - `test_less_long_pass / _fail / _equal`
  - `test_less_double_pass / _fail / _nan`
  - `test_greaterorequal_long_pass_equal / _pass_strict / _fail`
  - `test_greaterorequal_double_pass_equal / _fail / _nan`
  - `test_lessorequal_long_pass_equal / _pass_strict / _fail`
  - `test_lessorequal_double_pass_equal / _fail / _nan`
  - `test_inrange_long_pass_middle / _pass_min_boundary / _pass_max_boundary / _fail_below / _fail_above`
  - `test_inrange_double_pass / _fail / _nan`

## 阶段 3: assert_collection_helpers.z42 单元测试

- [x] 3.1 NEW `src/libraries/z42.test/tests/assert_collection_helpers.z42`
- [x] 3.2 cases by method:
  - `test_contains_object_array_pass` (int[] needle present)
  - `test_contains_object_array_pass_string` (string[] needle present —
    verifies overload covers boxed primitives + reference types)
  - `test_contains_object_array_fail`
  - `test_contains_object_array_empty_haystack_always_fails`
  - `test_does_not_contain_pass`
  - `test_does_not_contain_fail`
  - `test_does_not_contain_empty_haystack_always_passes`
  - `test_is_empty_pass_zero_length`
  - `test_is_empty_fail_one_element`
  - `test_is_not_empty_pass`
  - `test_is_not_empty_fail`
  - `test_string_contains_still_works` (regression: pre-spec `Contains(string, string)` unchanged)

## 阶段 4: 文档

### 4.A 用法 (user-facing)

- [x] 4.A.1 `src/libraries/z42.test/README.md` 能力表更新：
  - 旧 "Assert 基础（9 方法）" → "Assert 基础（13 方法）"（已包含原 9 + Throws + ThrowsAny + DoesNotThrow + EqualApprox）
  - 新增行 "Assert 数值比较 ✅ extend-assert-numeric-and-collection-helpers | Greater / Less / GreaterOrEqual / LessOrEqual / InRange × {long, double}"
  - 新增行 "Assert 数组集合助手 ✅ 同上 | Contains / DoesNotContain / IsEmpty / IsNotEmpty (object[])"
- [x] 4.A.2 `docs/design/testing/testing.md` 新增 § "Std.Test.Assert API
  quick reference"：表格化展示全部 ~22 方法分组（Equality / Boolean /
  Null / Numeric ordering / Range / Array containment / Array emptiness
  / String containment / Exception / Float approx / Control）+ 每组
  一行示例

### 4.B 设计思路 (design rationale)

- [x] 4.B.1 `docs/design/testing/testing.md` 同节后续段 § "Why these
  helpers, not others":
  - 解释 numeric ordering 用 short name (`Greater` 而非 `GreaterThan`)
  - 解释 `(actual, expected)` 参数顺序与 `Equal(expected, actual)` 的
    *特意* 不对称（Equal 对称无所谓，ordering 有方向所以参数顺序反映读法）
  - 解释 InRange inclusive bounds (xUnit 同；half-open 不直觉)
  - 解释 double overload **不用** EqualApprox 的容差（strict vs
    tolerant 是不同 assertion）
  - 解释为何只支持 `object[]` 不支持 `List` / `Set` (z42 L1 无泛型；
    `object[]` 覆盖 6/6 stdlib 观察用例；List 等待 L2 引入再扩)
  - 引用 `docs/spec/archive/2026-05-30-extend-assert-numeric-and-collection-helpers/design.md`

## 阶段 5: GREEN + commit + archive

- [x] 5.1 `./scripts/test-stdlib.sh z42.test` GREEN — 新两个 test 文件
  全部通过
- [x] 5.2 `./scripts/test-all.sh --parallel --jobs=4` 全绿
- [x] 5.3 commit + push（spec + Assert.z42 + 2 tests + docs）
- [x] 5.4 归档 `docs/spec/changes/extend-assert-numeric-and-collection-helpers/` →
  `docs/spec/archive/2026-05-30-extend-assert-numeric-and-collection-helpers/`
- [x] 5.5 push 归档 commit

## 备注

- 纯 z42 库扩展；0 Rust / C# 改动；0 zbc/zpkg version bump
- z42 没有 `Double.IsNaN` 但有 `double != double`（IEEE-754；NaN 不
  equal 自身）— 用 `if (actual != actual || expected != expected)` 检测
  NaN
- 实施时若发现 z42 编译器对 `object[]` 重载分辨有问题（与 `string`
  base类 conflict），停下报告并讨论 fallback 命名（如 `ContainsItem`
  for array variant）— spec Decision 6 / Risks 段已识别
