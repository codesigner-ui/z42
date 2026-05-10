# Proposal: Auto-property 语法完整实现 (class + interface + extern)

## Why

z42 当前对 property 语法 `T Name { get; [set;] }` 的处理是**显式 SkipAutoPropBody
+ continue**（[TopLevelParser.cs:258 / 402 / 521](src/compiler/z42.Syntax/Parser/TopLevelParser.cs)），
**完全丢弃 property 声明**。注释明示 "iter 1 skip（properties 属于 iter 2）"
—— 一直占位未实现。

直接影响：

- **Wave 2 限制 #4**：`IEnumerator<T>` 标准 C# 形式 `T Current { get; }` 无法
  parse，只能退化为 `T Current()` 方法形式
- **stdlib 设计被阻滞**：`Length`、`Count`、`Message`（如 Exception）等本应
  自然写为 property 的字段不能用 property 语法；强写为字段（无封装）或方法
  （冗余括号）
- **泛型约束**未来若需要 `where T: { Item Get; }` 等结构性约束（虽非当前
  scope），property 是基础

按 z42 总体目标对齐 C# BCL 心智，auto-property 是核心语言特性。本变更补齐
parser + TypeCheck + Codegen 完整支持。

## What Changes

参考 L3-G4e indexer desugar pattern，把 auto-property 在 parser 层 desugar
到方法（与 indexer `get_Item` / `set_Item` 同路径）：

### 类 body（含 backing field）

```z42
public class Foo {
    public int X { get; set; }      // ← user
    public int Y { get; }            // readonly
}
```

desugar 为：

```z42
public class Foo {
    private int __prop_X;            // 合成 backing field（隐藏，用户不可访问）
    public  int get_X()             { return this.__prop_X; }
    public  void set_X(int value)   { this.__prop_X = value; }

    private int __prop_Y;
    public  int get_Y()             { return this.__prop_Y; }
    // 无 setter（readonly）
}
```

`__prop_<Name>` 双下划线前缀（与 `__foreach_i` 等编译器内部命名一致），
保证不与用户字段冲突。

### Interface body（仅方法签名）

```z42
public interface IEnumerator<T> {
    T Current { get; }
}
```

desugar 为：

```z42
public interface IEnumerator<T> {
    T get_Current();
}
```

接口无字段，仅声明 method signature；implementer 必须提供 `get_Current()`
（或 auto-property `T Current { get; }` 自动合成）。

### Extern property

```z42
public class String {
    public extern int Length { get; }   // VM intrinsic
}
```

desugar 为：

```z42
public class String {
    public extern int get_Length();
}
```

VM intrinsic 名约定不变（与 stdlib 现有 `[Native]` 配合）。

### 用户代码访问

```z42
var f = new Foo();
f.X = 42;             // → f.set_X(42)
Console.WriteLine(f.X); // → f.get_X()
```

TypeChecker 在 BoundMember binding 时识别 method `get_X` / `set_X` 存在，
desugar 为方法调用（Codegen 走 VCall 路径，已支持）。

## Scope（允许改动的文件/模块）

| 文件 | 变更类型 | 说明 |
|------|---------|------|
| `src/compiler/z42.Syntax/Parser/TopLevelParser.cs` | edit | 3 处 SkipAutoPropBody 替换为完整 desugar 逻辑 |
| `src/compiler/z42.Syntax/Parser/TopLevelParser.Helpers.cs` | edit | 新增 `ParseAutoPropDecl` helper（class + interface + extern 共用） |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.cs` | edit | BoundMember binding：识别 property → 转方法调用 |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Stmts.cs` 或对应文件 | edit | Assign target 是 property → 转 set_X 调用 |
| `src/compiler/z42.Tests/PropertyTests.cs` | edit (existing file) | 加入 auto-property 端到端测试 |
| `src/runtime/tests/golden/run/` | add | 至少 2 个 golden（class auto-property + interface property + readonly） |
| `src/libraries/z42.core/src/IEnumerator.z42` | edit | 升级 `T Current()` 回 property 形式 `T Current { get; }` |
| `docs/design/properties.md` | add | 使用者视角 property 语法 + desugar 规则 + backing field 命名约定 |
| `docs/design/language-overview.md` | edit | 新增 property 章节链接 |

## Out of Scope

- 自定义 property body：`int X { get { return _x * 2; } set { _x = value / 2; } }`
  — 仅支持 auto-property 形式（`{ get; set; }` 或 `{ get; }`）；body form
  留给后续变更
- accessor 各自的可见性：`public int X { get; private set; }` — accessor
  visibility 解析但**忽略**（与 indexer 现状一致）
- init-only 访问器：`public int X { get; init; }` — C# 9 特性，不在本 scope
- expression-bodied property：`public int X => _x;` — 类似 expression-bodied
  method，独立后续考虑
- backing field 用户可见性：`__prop_X` 在 zasm dump 可见；**不**对用户代码
  暴露访问（TypeChecker 拒绝 `obj.__prop_X`，或者 visibility=private）

## Open Questions

- [x] backing field 命名 — `__prop_<Name>`（双下划线避免用户冲突）
- [x] 用户代码 `obj.__prop_X` 是否禁止 — 通过 visibility=private 阻挡（用户
  代码访问 private field 需 same-class 上下文，stdlib 自动 allow，外部 deny）
- [x] auto-property 字段是否真的合成 vs 走 method-only — **真的合成**
  （否则 stdlib `String.Length { get; extern }` 走 extern method 会和
  其他 extern property 不一致；统一合成 backing field + method 简化）
- [x] interface property 与 default body — 本 scope 仅声明（与现有接口
  default body 处理一致；`{ get; }` 即声明，无 body）

## Blocks / Unblocks

- **Blocks**：无（独立改动）
- **Unblocks**：
  - Wave 2 限制 #4 解锁，IEnumerator.Current 升级回 property
  - 未来 stdlib 重构：String.Length / Array.Length / Dictionary.Count 等可
    用自然 property 语法
  - Wave 3 IComparer / IFormattable 接口可能用到 property 形式
