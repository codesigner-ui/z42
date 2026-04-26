# Spec: IComparer / IEqualityComparer / IFormattable contracts

## ADDED Requirements

### Requirement: IComparer<T>

```z42
public interface IComparer<T> {
    int Compare(T x, T y);
}
```

返回 negative / 0 / positive，与 `IComparable.CompareTo` 同约定。

#### Scenario: 用户实现 IComparer 并按接口引用调用
- **GIVEN** `class DescIntComparer : IComparer<int> { int Compare(int x, int y) { return y - x; } }`
- **WHEN** `IComparer<int> cmp = new DescIntComparer(); cmp.Compare(3, 5)`
- **THEN** 返回 `2`（降序：5 > 3 → x.compare(y) 返回 y-x = 2）

#### Scenario: 泛型约束可用
- **WHEN** `T DoCompare<T>(IComparer<T> c, T a, T b) { return c.Compare(a, b); }`
- **THEN** 编译通过

### Requirement: IEqualityComparer<T>

```z42
public interface IEqualityComparer<T> {
    bool Equals(T x, T y);
    int  GetHashCode(T obj);
}
```

双参数相等性 + 单参数 hash（与 IEquatable 的"自比"模式区分）。

#### Scenario: 用户实现 IEqualityComparer 并使用
- **GIVEN** `class CaseInsensitiveStringComparer : IEqualityComparer<string> {
    bool Equals(string x, string y) { return x.ToLower() == y.ToLower(); }
    int GetHashCode(string s) { return s.ToLower().GetHashCode(); }
  }`
- **WHEN** 经接口引用调用
- **THEN** `cmp.Equals("Foo", "FOO")` 返回 true

### Requirement: IFormattable

```z42
public interface IFormattable {
    string ToString(string format);
}
```

非泛型；返回带格式的字符串。format 语义由 implementer 决定。

#### Scenario: 用户类实现 IFormattable
- **GIVEN** `class Money : IFormattable { ... string ToString(string format) { ... } }`
- **WHEN** `IFormattable f = new Money(...); f.ToString("USD")`
- **THEN** 编译 + 运行通过

#### Scenario: 与 Object.ToString 共存
- **GIVEN** 类同时 override `Object.ToString()` (无参) 和实现
  `IFormattable.ToString(string format)` (带参)
- **WHEN** 编译
- **THEN** 两者按 arity 分别工作（无参 vs 单参），不冲突
- **AND** Codegen 生成不同函数（`ToString` / `ToString$1`，按现有 method overload 规则）

### Requirement: Script-First 实现

3 个接口纯 `.z42` 定义，零 VM extern。

#### Scenario: stdlib 构建
- **WHEN** `./scripts/build-stdlib.sh`
- **THEN** `z42.core.zpkg` 含三个新接口符号；其他 zpkg 无影响；VM 无需重新编译

## IR Mapping

不引入新 IR 指令；接口走普通 `InterfaceDef` + VCall 路径。

## Pipeline Steps

- [ ] Lexer / Parser — 不涉及（接口语法已支持）
- [x] **TypeChecker** — 通过 stdlib TSIG 自动可见
- [ ] IR Codegen — 不涉及
- [ ] VM — 不涉及
- [x] **stdlib build** — 编译 3 个新 .z42 接口文件
