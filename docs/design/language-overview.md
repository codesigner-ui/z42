# z42 Language Overview

> 本文档是 **L1（Bootstrap）** 阶段的语法实现参考，面向编译器开发者。
> 语言设计决策（feature 层面）见 [`docs/features.md`](../features.md)。
> 演进计划和实现进度见 [`docs/roadmap.md`](../roadmap.md)。
>
> **SoT 关系**：本文档是语法的**叙事性说明**（user-facing prose），机器可读
> 的权威定义在 [`grammar.peg`](grammar.peg)；`Z42.Tests.GrammarSyncTests` 强制
> 校验两者一致。改动语法时按 `grammar.peg` → 本文档 → 跑 `dotnet test
> --filter GrammarSync` 的顺序，避免漂移。

---

## 1. 顶层结构

```z42
namespace Geometry;          // 命名空间声明

using System.Math;           // 导入
using System.Collections.Generic;

// 顶层函数（C# 9+ 风格，无需包在类中）
void Main() {
    var p = new Point(1.0, 2.0);
    Console.WriteLine(p.ToString());
}
```

---

## 2. 基本类型

| z42 类型   | 位宽 | 等价 C# 类型 |
|-----------|------|------------|
| `sbyte`   | 8    | `sbyte`    |
| `short`   | 16   | `short`    |
| `int`     | 32   | `int`      |
| `long`    | 64   | `long`     |
| `byte`    | 8    | `byte`     |
| `ushort`  | 16   | `ushort`   |
| `uint`    | 32   | `uint`     |
| `ulong`   | 64   | `ulong`    |
| `float`   | 32   | `float`    |
| `double`  | 64   | `double`   |
| `bool`    | —    | `bool`     |
| `char`    | 32   | `char` (Unicode) |
| `string`  | —    | `string`   |
| `void`    | —    | `void`     |

```z42
int    x = 42;
long   big = 9_000_000_000L;
double pi = 3.14159;
float  f = 1.5f;
bool   flag = true;
char   ch = 'z';
string s = "hello";

// var 推断
var count = 0;          // int
var name = "z42";       // string
var ratio = 0.5;        // double
```

### 可空类型

```z42
string? maybeNull = null;
int? optInt = 42;

// null 合并
string result = maybeNull ?? "default";

// null 条件访问
int? len = maybeNull?.Length;
```

---

## 3. 字符串

```z42
string a = "hello";
string b = "world";

// 字符串插值（C# $ 前缀）
string msg = $"Hello, {b}! Length = {b.Length}";

// 原始字符串（C# 11+）
string json = """
    {
        "key": "value"
    }
    """;

// 常用方法
int len = a.Length;
string upper = a.ToUpper();
bool starts = a.StartsWith("he");
string[] parts = a.Split(',');
```

---

## 3.5 运算符语义

### 逻辑运算符短路求值

`&&` 和 `||` 短路求值 —— 当左侧已决定整体结果时，**不**对右侧表达式求值（不触发副作用、不抛出异常）：

```z42
// `&&` 左侧为 false → 右侧跳过
bool safe = arr != null && arr[0] > 0;   // arr 为 null 时不会索引

// `||` 左侧为 true → 右侧跳过
bool ok = cached || Probe();             // 已命中缓存时 Probe() 不执行

// 优先级：&& 比 || 紧
if (a && b || c) { ... }                 // 等价 ((a && b) || c)
```

位运算 `&` / `|` **不**短路，两侧总是求值。

实现注：IR 层将 `&&` / `||` desugar 为 `BrCond` 控制流；保留 `AndInstr` / `OrInstr` 仅用于位运算。

---

## 4. 控制流

```z42
// if / else
if (x > 0) {
    Console.WriteLine("positive");
} else if (x < 0) {
    Console.WriteLine("negative");
} else {
    Console.WriteLine("zero");
}

// while / do-while
while (count < 10) {
    count++;
}

do {
    count--;
} while (count > 0);

// for
for (int i = 0; i < 10; i++) {
    Console.WriteLine(i);
}

// foreach —— 支持数组、字符串、以及任意实现 `int Count()` + `T get_Item(int)` 的类
var numbers = new[] { 1, 2, 3, 4, 5 };
foreach (var n in numbers) {
    Console.WriteLine(n);
}

// 用户类容器（duck-typed 协议，无需显式实现 IEnumerable）
var xs = new ArrayList<int>();
xs.Add(1); xs.Add(2);
foreach (var v in xs) { Console.WriteLine(v); }

// switch 表达式（C# 8+）
string label = x switch {
    > 0  => "positive",
    < 0  => "negative",
    _    => "zero"
};

// switch 语句
switch (direction) {
    case Direction.North:
        Console.WriteLine("going north");
        break;
    default:
        Console.WriteLine("other direction");
        break;
}
```

---

## 5. 函数与方法

```z42
// 顶层函数
int Add(int a, int b) {
    return a + b;
}

// 表达式体（C# expression-bodied）
int Multiply(int a, int b) => a * b;

// 默认参数
void Greet(string name, string prefix = "Hello") {
    Console.WriteLine($"{prefix}, {name}!");
}

// 可变参数
int Sum(params int[] values) {
    int total = 0;
    foreach (var v in values) total += v;
    return total;
}

// 多返回值：tuple（推荐）
(bool ok, int v) TryParse(string s) {
    return (true, 42);
}
var (ok, v) = TryParse("42");

// 参数修饰符：ref / out / in（编译期已落地，运行时实施在 follow-up spec
// `impl-ref-out-in-runtime`；详见 docs/design/parameter-modifiers.md）
void Increment(ref int x) { x = x + 1; }    // 双向引用
bool TryParseOut(string s, out int v) {     // 单向输出 + DefiniteAssignment
    v = 0; return false;
}
double Norm(in BigVec v) { ... }            // 只读引用，零拷贝

// callsite 三者均强制写修饰符（修正 C# `in` 可省的不一致）：
var c = 0;
Increment(ref c);
TryParseOut("x", out var n);                // out var 内联声明
var d = Norm(in someVec);

```

> **过渡期注意**：编译期对 `ref` / `out` / `in` 的所有验证已生效（修饰符一致 / lvalue / DA / 4 交互规则 / overload）。运行时 callee 修改尚未传回 caller —— 见 `parameter-modifiers.md` "Runtime Implementation"。需要"修改调用方变量"语义时优先用 tuple 多返回值。

### 5.1 局部函数（嵌套函数声明，C# 7+）

```z42
int Outer() {
    // 局部函数：仅在 Outer 内可见，可直接递归
    int Helper(int x) => x * 2;
    int Fact(int n) => n <= 1 ? 1 : n * Fact(n - 1);
    return Helper(3) + Fact(5);
}
```

L2 阶段嵌套函数不允许引用外层 local（捕获是 L3 的闭包特性）；L3 阶段引用外层 local 时升级为闭包，详见 [`closure.md`](closure.md)。

### 5.2 Lambda 与函数类型

z42 提供 C# 风格 lambda 字面量与 **`(T) -> R` 函数类型**（替代 `Func<T,R>` / `Action<T>`）。

```z42
// Lambda 字面量
var inc = (int x) => x + 1;                  // 表达式 body
var step = (int x) => {                      // 语句 body
    var y = x * 2;
    return y + 1;
};

// 函数类型作为参数 / 字段类型
List<U> Map<T, U>(List<T> self, (T) -> U f) { ... }
class EventBus {
    public List<(int) -> void> Handlers = new();
}

// 高阶 API 用法
list.Map(x => x * 2);
list.Filter(x => x > 0);
```

**捕获语义**（L3 完整闭包阶段）：值类型按快照、引用类型按身份、`spawn` 边界 move + Send。完整规范见 [`closure.md`](closure.md)。

L2 阶段编译器只接受**无捕获 lambda**——回调字面量（如 `x => x.Name`）可用，但引用外层 local 直接编译错误。

---

## 6. 类

### 6.1 Object 基类与 Type 描述符

所有 z42 **引用类型**（`class`）均隐式继承 `Std.Object`（对应 `z42.core/Object.z42`）。
编译器在 TypeCheck 和 IrGen 阶段自动注入 `base_class: "Std.Object"`，VM 在 `build_type_registry`
时将 Object 的虚方法（`ToString`/`Equals`/`GetHashCode`）加入 vtable，派生类可通过 `override` 重写。
**值类型**（`struct`、`record`）不继承 Object，编译器为其自动合成值语义的 `Equals`/`GetHashCode`/`ToString`。

`Object` 提供以下成员：

| 成员 | 签名 | 行为 |
|------|------|------|
| `GetType()` | `extern Type GetType()` | 返回运行时 `Type` 描述符（VM 提供 `__obj_get_type`） |
| `ReferenceEquals` | `static extern bool ReferenceEquals(Object? a, Object? b)` | 堆地址相等（两个 null 也为 true） |
| `Equals` | `virtual extern bool Equals(Object? other)` | 默认引用相等（`__obj_equals`）；子类可重写为值相等 |
| `GetHashCode` | `virtual extern int GetHashCode()` | 基于 Rc 指针地址的 identity hash（`__obj_hash_code`）；重写 `Equals` 时必须同步重写 |
| `ToString` | `virtual extern string ToString()` | 默认返回不含命名空间的类名（`__obj_to_str`）；子类通常应重写 |

`Type` 是轻量的运行时类型描述符，仅可通过 `GetType()` 获取，不可直接构造：

```z42
var t = obj.GetType();
Console.WriteLine(t.Name);      // "Circle"
Console.WriteLine(t.FullName);  // "geometry.Circle"
```

**规则：**
- 重写 `Equals` 时必须同时重写 `GetHashCode`，两者必须保持一致。
- `ReferenceEquals` 不可被重写（静态方法）。
- `ToString()` 默认返回不含命名空间的类名；需要完全限定名时用 `GetType().FullName`。

### 6.2 类定义示例

```z42
public class Point {
    // 属性（C# auto-property）
    public double X { get; set; }
    public double Y { get; set; }

    // 构造函数
    public Point(double x, double y) {
        X = x;
        Y = y;
    }

    // 主构造函数（C# 12+）
    // public class Point(double X, double Y) { ... }

    // 方法
    public double DistanceTo(Point other) {
        double dx = X - other.X;
        double dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // 重写 ToString
    public override string ToString() => $"Point({X}, {Y})";

    // 静态工厂方法
    public static Point Origin() => new Point(0, 0);
}

// 继承
public class Point3D(double X, double Y, double Z) : Point(X, Y) {
    public double Z { get; set; } = Z;

    public override string ToString() => $"Point3D({X}, {Y}, {Z})";
}
```

### 6.3 字段默认值与初始化器

实例字段（不是 auto-property）支持声明时附加 `=` 初始化器：

```z42
class Box {
    int n = 5;            // 显式 initializer
    string s = "hello";
    bool flag;            // 无 initializer → 取类型默认值
}
```

规则（参见 `spec/archive/2026-05-02-fix-class-field-default-init/`）：

| 字段写法 | 初始值（无显式 ctor）| 初始值（显式 ctor body 覆写后）|
|---------|--------------------|-----------------------------|
| `int n;` | `0` | ctor body 内最后一次赋值 |
| `int n = 5;` | `5`（合成 ctor 入口注入 init）| `5` → 用户 body 的赋值覆写 |
| `bool flag;` | `false` | … |
| `string s;` | `null` | … |
| `string s = "x";` | `"x"` | … |
| `Point p;` | `null`（引用类型默认）| … |

**实现细节**：

- 编译器把 instance field initializer 注入到每个显式 ctor 入口（base ctor call 之后、用户 body 之前）。
- 类没有显式 ctor 但本类或本地祖先链上任一类有字段 init → 编译器合成无参隐式 ctor，按祖先 → 自身顺序内联整条链的 field init 表达式。
- 字段无 init 时，VM `ObjNew` 按字段声明类型选默认值（`int*`/`f64*` → 0、`bool` → false、`char` → `'\0'`、`str`/引用类型 → null），不再一律 null。
- z42 当前模型不自动调用 base ctor — 显式 ctor 仍需用户主动写 `: base(...)` 触发父类 ctor side effect；合成 ctor 仅内联本地祖先字段 init，不调用任何 base ctor。

---

## 7. 结构体

值类型，分配在栈上，赋值时复制。

```z42
public struct Color {
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }

    public Color(byte r, byte g, byte b) => (R, G, B) = (r, g, b);

    public static readonly Color White = new Color(255, 255, 255);
    public static readonly Color Black = new Color(0, 0, 0);

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

// 使用
var red = new Color(255, 0, 0);
var copy = red;     // 值拷贝
```

---

## 8. Record

不可变数据类型，自动生成相等性比较、`ToString`、解构。

```z42
// record class（引用语义）
public record Person(string Name, int Age);

// record struct（值语义）
public record struct Vector2(double X, double Y);

// 使用
var alice = new Person("Alice", 30);
var older = alice with { Age = 31 };    // 非破坏性更新

Console.WriteLine(alice);              // Person { Name = Alice, Age = 30 }
Console.WriteLine(alice == older);     // false
```

---

## 9. 接口

```z42
public interface IShape {
    double Area();
    double Perimeter();
    string Name { get; }
}

public interface IDrawable {
    void Draw();
}

// 多接口实现
public class Circle : IShape, IDrawable {
    public double Radius { get; }

    public Circle(double radius) {
        Radius = radius;
    }

    public double Area()      => Math.PI * Radius * Radius;
    public double Perimeter() => 2 * Math.PI * Radius;
    public string Name        => "Circle";

    public void Draw() {
        Console.WriteLine($"Drawing {Name} r={Radius}");
    }
}
```

---

## 10. 枚举

```z42
// 简单枚举
public enum Direction {
    North, South, East, West
}

// 带底层值的枚举
public enum StatusCode : int {
    Ok      = 200,
    NotFound = 404,
    Error   = 500
}

Direction dir = Direction.North;
StatusCode code = StatusCode.Ok;

// 枚举作为 switch 目标
string label = dir switch {
    Direction.North => "↑",
    Direction.South => "↓",
    Direction.East  => "→",
    Direction.West  => "←",
    _               => "?"
};
```

---

## 11. 判别联合（代数类型）

Phase 1 使用 C# abstract record 层次结构模拟。

```z42
public abstract record Shape;
public sealed record Circle(double Radius) : Shape;
public sealed record Rectangle(double Width, double Height) : Shape;
public sealed record Triangle(double Base, double Height) : Shape;

// 模式匹配
double Area(Shape s) => s switch {
    Circle c        => Math.PI * c.Radius * c.Radius,
    Rectangle r     => r.Width * r.Height,
    Triangle t      => 0.5 * t.Base * t.Height,
    _               => throw new ArgumentException($"Unknown shape: {s}")
};

// 解构模式
if (s is Circle { Radius: > 10 } big) {
    Console.WriteLine($"Big circle: r={big.Radius}");
}
```

---

## 12. 异常处理

```z42
// throw
void Validate(int age) {
    if (age < 0) throw new ArgumentException($"Invalid age: {age}");
}

// try / catch / finally
try {
    var result = Divide(10, 0);
} catch (DivideByZeroException ex) {
    Console.WriteLine($"Error: {ex.Message}");
} catch (Exception ex) when (ex.Message.Contains("overflow")) {
    Console.WriteLine("Overflow detected");
} finally {
    Console.WriteLine("cleanup");
}

// 自定义异常
public class Z42RuntimeException(string message, int errorCode)
    : Exception(message) {
    public int ErrorCode { get; } = errorCode;
}
```

---

## 13. 执行模式注解（z42 扩展）

z42 在 C# 基础上新增执行模式注解，对 VM 行为进行提示：

```z42
[ExecMode(Mode.Interp)]        // 始终解释执行（快速启动、热重载）
namespace Scripts.Config;

[ExecMode(Mode.Jit)]           // JIT 编译（最优吞吐）
namespace Engine.Render;

[ExecMode(Mode.Aot)]           // AOT 编译（确定性性能）
namespace Core.Crypto;
```

跨模式调用透明，像普通方法调用一样。

---

## 14. 热更新注解（z42 扩展）

`[HotReload]` 注解标记的命名空间支持运行时函数替换，无需重启 VM。面向游戏脚本等需要快速迭代的场景。

```z42
[HotReload]
namespace Game.Scripts;

void OnUpdate(float dt) { ... }   // 热更新后下一次调用即生效
```

与 `[ExecMode(Mode.Interp)]` 配套使用；JIT/AOT 模式不支持热更新。

详见 `specs/hot-reload.md`。

## 15. InternalCall 互操作（`extern` + `[Native]`）

z42 通过 `extern` 关键字 + `[Native("__intrinsic")]` 属性声明 VM 内建函数的绑定，实现标准库与 VM 的零开销互操作（InternalCall 机制）。

```z42
namespace Std.IO;

public static class Console {
    // 声明：VM 实现，无 z42 函数体
    [Native("__println")]
    public static extern void WriteLine(string value);

    [Native("__readline")]
    public static extern string ReadLine();
}
```

**规则：**

- `extern` 方法必须同时带 `[Native("__name")]` 属性；缺少属性报 `Z0903`
- `[Native("__name")]` 属性必须在 `extern` 方法上使用；缺少 `extern` 报 `Z0904`
- `__name` 必须是 VM 已注册的内建名（见 `NativeTable.All`）；未知名报 `Z0901`
- 参数数量必须与 `NativeTable` 中的定义一致；不符报 `Z0902`
- `extern` 方法不允许有函数体（使用 `;` 代替 `{}`）

**IR 映射：** 编译器将 `extern` 方法编译为单块函数，函数体为一条 `Builtin` 指令 + `Ret`：

```
function z42.io.Console.WriteLine(param_count=1) -> void
  entry:
    r1 = builtin "__println" [r0]
    ret
```

**方法体语法糖（表达式体）：** 非 `extern` 方法支持 `=> expr;` 形式作为函数体简写：

```z42
public static void Log(string msg) => Console.WriteLine(msg);
// 等价于 { Console.WriteLine(msg); }
```

---

---

## 16. 编译器错误恢复

z42 编译器支持**多错误报告**（error recovery）：解析器遇到语法错误后不立即停止，而是恢复到下一个恢复点并继续解析，从而在单次编译中报告多个错误。

**恢复点层级（从粗到细）：**

| 层级 | 恢复位置 | 说明 |
|------|----------|------|
| 顶层声明 | 下一个 `class`/`struct`/`enum`/`void`/类型关键字 | 一个声明解析失败后继续解析下一个 |
| 类体成员 | 下一个 `;` 或 `}` | 一个字段/方法失败后继续解析下一个成员 |
| 枚举成员 | 下一个 `,` 或 `}` | 枚举成员修饰符等错误后跳过该成员 |
| 语句 | 下一个 `;` / `}` / 语句关键字 | 一条语句失败后继续解析同一块的后续语句 |

**AST 占位节点：**
- `ErrorExpr` — 表达式解析失败时插入，TypeChecker 将其类型推断为 `Error`，Codegen 生成空常量
- `ErrorStmt` — 语句解析失败时插入，Codegen 跳过

**调用方式：**
```csharp
// 推荐：不捕获异常，通过 Diagnostics 检查
var cu = parser.ParseCompilationUnit();
if (parser.Diagnostics.HasErrors) { /* 处理错误 */ }
```

> 错误恢复是尽力而为的机制，用于改善开发体验。级联错误（cascade errors）可能出现，但编译器保证不会因错误恢复陷入死循环。

---

> IR 映射细节（`do-while`、`??`、`?.`、`enum` 编译策略、`List<T>`/`Dictionary<K,V>` 内置方法）见 [`docs/design/ir.md`](ir.md)。
