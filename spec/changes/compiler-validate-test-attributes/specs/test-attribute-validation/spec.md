# Spec: Test Attribute Validation

## ADDED Requirements

### Requirement: Z0911 — [Test] 签名校验

#### Scenario: 合法 [Test] 不报错

- **WHEN** `[Test] fn ok_test() {}`
- **THEN** 编译通过

#### Scenario: 带参数的 [Test] 报错

- **WHEN** `[Test] fn bad(x: i32) {}`
- **THEN** 报 Z0911；message 含 "must take no parameters"

#### Scenario: 有返回值的 [Test] 报错

- **WHEN** `[Test] fn bad() -> i32 { return 0; }`
- **THEN** 报 Z0911；message 含 "must return void"

#### Scenario: 泛型 [Test] 报错

- **WHEN** `[Test] fn bad<T>() {}`
- **THEN** 报 Z0911；message 含 "must not be generic"

---

### Requirement: Z0912 — [Benchmark] 签名校验

#### Scenario: 合法 [Benchmark] 不报错

- **WHEN** `[Benchmark] fn b(bencher: Bencher) {}`
- **THEN** 编译通过

#### Scenario: 缺 Bencher 参数报错

- **WHEN** `[Benchmark] fn b() {}`
- **THEN** 报 Z0912

#### Scenario: 第一个参数不是 Bencher 报错

- **WHEN** `[Benchmark] fn b(x: i32) {}`
- **THEN** 报 Z0912

#### Scenario: 有返回值报错

- **WHEN** `[Benchmark] fn b(bencher: Bencher) -> i32 { return 0; }`
- **THEN** 报 Z0912

---

### Requirement: Z0913 — [ShouldThrow<E>] 校验

#### Scenario: 合法 [ShouldThrow<E>] 不报错

- **WHEN** `[Test] [ShouldThrow<DivByZero>] fn t() { 1/0 }` (DivByZero 是 Exception 子类)
- **THEN** 编译通过

#### Scenario: 类型不存在报错

- **WHEN** `[ShouldThrow<NotAType>]`
- **THEN** 报 Z0913；message 含 "not found"

#### Scenario: 类型不是 Exception 子类报错

- **WHEN** `[ShouldThrow<int>]`
- **THEN** 报 Z0913；message 含 "must be a subtype of Exception"

#### Scenario: 缺类型参数报错

- **WHEN** `[ShouldThrow]`（无尖括号）
- **THEN** 报 Z0913

#### Scenario: 类型 idx 写入 TestEntry

- **WHEN** 校验通过的 `[ShouldThrow<E>]`
- **THEN** TestEntry.expected_throw_type_idx 指向 E 的 type pool entry

---

### Requirement: Z0914 — [Skip] reason 校验

#### Scenario: 合法 [Skip(reason)] 不报错

- **WHEN** `[Test] [Skip(reason: "blocked by issue 123")] fn t() {}`
- **THEN** 编译通过

#### Scenario: 缺 reason 参数报错

- **WHEN** `[Test] [Skip] fn t() {}`
- **THEN** 报 Z0914

#### Scenario: 空字符串 reason 报错

- **WHEN** `[Test] [Skip(reason: "")] fn t() {}`
- **THEN** 报 Z0914

---

### Requirement: Z0915 — [Setup] / [Teardown] 签名

#### Scenario: 合法 [Setup] 不报错

- **WHEN** `[Setup] fn s() {}`
- **THEN** 编译通过

#### Scenario: [Setup] 带参数报错

- **WHEN** `[Setup] fn s(x: i32) {}`
- **THEN** 报 Z0915

#### Scenario: [Setup] 有返回值报错

- **WHEN** `[Setup] fn s() -> i32 { return 0; }`
- **THEN** 报 Z0915

#### Scenario: [Teardown] 同样规则

- **WHEN** `[Teardown] fn t(x: i32) {}`
- **THEN** 报 Z0915

---

### Requirement: Attribute 组合校验

#### Scenario: [Test] + [Benchmark] 互斥

- **WHEN** `[Test] [Benchmark] fn x(b: Bencher) {}`
- **THEN** 报 Z0911

#### Scenario: [Setup] + [Test] 互斥

- **WHEN** `[Setup] [Test] fn x() {}`
- **THEN** 报 Z0915

#### Scenario: [Skip] 单独使用报错

- **WHEN** `[Skip(reason: "x")] fn x() {}`
- **THEN** 报 Z0914 (要求搭配 [Test] 或 [Benchmark])

#### Scenario: [ShouldThrow] 必须搭 [Test]

- **WHEN** `[ShouldThrow<DivByZero>] fn x() { 1/0 }`（无 [Test]）
- **THEN** 报 Z0913

---

### Requirement: TestCase 参数数量校验

#### Scenario: 参数数量匹配不报错

- **WHEN** `[Test] [TestCase(1, 2)] fn t(a: i32, b: i32) {}`
- **THEN** 编译通过

#### Scenario: 参数数量 mismatch 报错

- **WHEN** `[Test] [TestCase(1)] fn t(a: i32, b: i32) {}` (1 arg vs 2 params)
- **THEN** 报 Z0911

#### Scenario: 多个 [TestCase] 各自校验

- **WHEN** `[Test] [TestCase(1, 2)] [TestCase(3)] fn t(a: i32, b: i32) {}`（第二个 case 数量不对）
- **THEN** 报 Z0911（针对第二个 case）

---

### Requirement: 错误信息质量

#### Scenario: 错误信息含源码位置

- **WHEN** 任一校验失败
- **THEN** 错误信息含 file:line:col + Span underline
- **AND** 含错误码 Z091X

#### Scenario: docs/design/error-codes.md 完整

- **WHEN** 阅读 [docs/design/error-codes.md](docs/design/error-codes.md)
- **THEN** Z0911-Z0915 每条含：触发条件 / 修复建议 / 示例
- **AND** 替换 R1 期占位描述
