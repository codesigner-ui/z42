# z42 Language Overview

## 设计策略

z42 分两个演进阶段：

| 阶段 | 策略 | 目标 |
|------|------|------|
| **Phase 1 — Bootstrap** | 语法完全对齐 C#，降低编译器开发难度 | 跑通完整 pipeline：源码 → IR → 解释执行 |
| **Phase 2 — Evolution** | 引入 Rust 设计改进内存模型、类型系统、错误处理 | 零开销抽象、无 GC、更强的安全保证 |

本文档描述 **Phase 1** 语法。Phase 2 改动标注为 `[Phase 2]`。

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

// foreach
var numbers = new[] { 1, 2, 3, 4, 5 };
foreach (var n in numbers) {
    Console.WriteLine(n);
}

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

// 命名参数
Greet(name: "world", prefix: "Hi");

// 可变参数
int Sum(params int[] values) {
    int total = 0;
    foreach (var v in values) total += v;
    return total;
}

// out / ref 参数
bool TryParse(string s, out int result) {
    // ...
    result = 0;
    return false;
}

// 泛型函数
T Max<T>(T a, T b) where T : IComparable<T> {
    return a.CompareTo(b) >= 0 ? a : b;
}
```

---

## 6. 类

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

## 12. 泛型

```z42
// 泛型类
public class Stack<T> {
    private readonly List<T> _items = new();

    public void Push(T item) => _items.Add(item);

    public T Pop() {
        if (_items.Count == 0) throw new InvalidOperationException("Stack is empty");
        var last = _items[^1];
        _items.RemoveAt(_items.Count - 1);
        return last;
    }

    public int Count => _items.Count;
}

// 泛型约束
public T Min<T>(T a, T b) where T : IComparable<T> =>
    a.CompareTo(b) <= 0 ? a : b;

// 多约束
public class Repository<T> where T : class, IEntity, new() {
    // ...
}
```

---

## 13. 委托与 Lambda

```z42
// 内置委托类型
Func<int, int, int> add = (a, b) => a + b;
Action<string> print = s => Console.WriteLine(s);
Predicate<int> isEven = n => n % 2 == 0;

// Lambda 捕获
int multiplier = 3;
Func<int, int> triple = x => x * multiplier;

// LINQ 风格
var numbers = new[] { 1, 2, 3, 4, 5, 6 };
var result = numbers
    .Where(n => n % 2 == 0)
    .Select(n => n * n)
    .ToList();   // [4, 16, 36]
```

---

## 14. 异常处理

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

## 15. 异步编程

```z42
using System.Net.Http;

// async / await（完全对齐 C#）
async Task<string> FetchAsync(string url) {
    using var client = new HttpClient();
    return await client.GetStringAsync(url);
}

// async void（事件处理器）
async void OnButtonClick() {
    var content = await FetchAsync("https://example.com");
    Console.WriteLine(content[..100]);
}

// Task.WhenAll 并行
async Task<string[]> FetchAllAsync(string[] urls) {
    var tasks = urls.Select(FetchAsync);
    return await Task.WhenAll(tasks);
}

// ValueTask（低分配路径）
async ValueTask<int> GetCachedAsync(string key) {
    if (_cache.TryGetValue(key, out var v)) return v;
    return await LoadFromDbAsync(key);
}
```

---

## 16. 执行模式注解（z42 扩展）

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

## 17. 热更新注解（z42 扩展）

`[HotReload]` 注解标记的命名空间支持运行时函数替换，无需重启 VM。面向游戏脚本等需要快速迭代的场景。

```z42
[HotReload]
namespace Game.Scripts;

void OnUpdate(float dt) { ... }   // 热更新后下一次调用即生效
```

与 `[ExecMode(Mode.Interp)]` 配套使用；JIT/AOT 模式不支持热更新。

详见 `specs/hot-reload.md`。

---

## 实现状态（Phase 1）

下表追踪各特性的编译器/VM 实现进度：

| 特性 | Parser | TypeCheck | IrGen | VM | 备注 |
|------|--------|-----------|-------|-----|------|
| 基本类型、运算符 | ✅ | ✅ | ✅ | ✅ | |
| if / while / for / foreach | ✅ | ✅ | ✅ | ✅ | |
| `do-while` | ✅ | ✅ | ✅ | ✅ | |
| switch 表达式 / 语句 | ✅ | ✅ | ✅ | ✅ | |
| 三目运算符 `?:` | ✅ | ✅ | ✅ | ✅ | |
| `??` 空合并运算符 | ✅ | ✅ | ✅ | ✅ | |
| 字符串插值 | ✅ | ✅ | ✅ | ✅ | |
| 数组 `T[]` | ✅ | ✅ | ✅ | ✅ | |
| `List<T>` | ✅ | ✅ | ✅ | ✅ | pseudo-class 策略 |
| `Dictionary<K,V>` | ✅ | ✅ | ✅ | ✅ | pseudo-class 策略，key 序列化为 string |
| `?.` 空条件访问 | ✅ | ✅ | ✅ | ✅ | 返回 null 若 target 为 null |
| 类（字段、构造器、方法） | ✅ | ✅ | ✅ | ✅ | |
| 枚举 `enum` | ✅ | ✅ | ✅ | ✅ | 值映射为 i64 |
| 异常 try/catch/throw | ✅ | ✅ | ✅ | ✅ | |
| 默认参数值 | ✅ | ✅ | ✅ | ✅ | call site 展开；不支持命名参数 |
| C# 数值类型别名（sbyte/short/byte/ushort/uint/ulong） | ✅ | ✅ | ✅ | ✅ | 映射为对应 IR 类型 |
| 可空类型 `T?` 赋值（`T → T?` 隐式包装） | ✅ | ✅ | ✅ | ✅ | |
| Lambda 表达式 | ✅ | — | — | — | 待实现 |
| 泛型 `<T>` | ✅ | — | — | — | 待实现 |
| 接口 / 继承 | ✅ | — | — | — | 待实现 |
| async / await | ✅ | — | — | — | 待实现 |
| Math / Assert / Console | — | ✅ | ✅ | ✅ | pseudo-class |

### `do-while` IR 映射

```
do { body } while (cond);

→  body_lbl:
     <body instructions>
   cond_lbl:
     <cond instructions>  → r0
     BrCond r0, body_lbl, end_lbl
   end_lbl:
```

### `??` 空合并运算符 IR 映射

```
expr ?? fallback

→  r0 = <expr>
   r1 = (r0 == null)           // EqInstr with ConstNull
   BrCond r1, null_lbl, end_lbl
null_lbl:
   r2 = <fallback>
   Copy result, r2
   Br end_lbl
end_lbl:
   Copy result, r0              // non-null path
```

### `List<T>` 内置方法映射

| z42 方法 | VM Builtin | 说明 |
|---------|-----------|------|
| `new List<T>()` | `__list_new` | 创建空列表 |
| `list.Add(v)` | `__list_add` | 末尾追加 |
| `list.Count` | `__list_count` | 元素个数（属性） |
| `list[i]` | `ArrayGet` | 索引读（复用数组指令） |
| `list[i] = v` | `ArraySet` | 索引写（复用数组指令） |
| `list.RemoveAt(i)` | `__list_remove_at` | 按索引删除 |
| `list.Contains(v)` | `__list_contains` | 是否包含 |
| `list.Clear()` | `__list_clear` | 清空 |
| `list.Insert(i, v)` | `__list_insert` | 按位置插入 |

内部存储复用 `Value::Array`（`Rc<RefCell<Vec<Value>>>`），`List<T>` 与 `T[]` 共享底层表示，仅在 IrGen 层区分调用方式。

### `Dictionary<K,V>` 内置方法映射

| z42 方法 | VM Builtin | 说明 |
|---------|-----------|------|
| `new Dictionary<K,V>()` | `__dict_new` | 创建空字典 |
| `dict[key]` | `ArrayGet` (Map 分支) | 键读（key 转 String） |
| `dict[key] = v` | `ArraySet` (Map 分支) | 键写（key 转 String） |
| `dict.Count` | `__len` | 键值对个数 |
| `dict.ContainsKey(k)` | `__dict_contains_key` | 是否含该键 |
| `dict.Remove(k)` | `__dict_remove` | 删除键 |
| `dict.Keys` | `__dict_keys` | 返回键数组 |
| `dict.Values` | `__dict_values` | 返回值数组 |

内部存储为 `Value::Map`（`Rc<RefCell<HashMap<String, Value>>>`），所有键在存储时序列化为 `String`。

### `?.` 空条件访问 IR 映射

```
target?.member

→  r0 = <target>
   r_null = const null
   r_cmp  = (r0 == r_null)        // EqInstr
   BrCond r_cmp, null_lbl, nonnull_lbl
nonnull_lbl:
   result = FieldGet(r0, "member")  // or __len for Length/Count
   Br end_lbl
null_lbl:
   result = const null
   Br end_lbl
end_lbl:
```

### `enum` 编译策略

- 枚举成员编译为 `i64` 常量（`ConstI64Instr`）
- 带底层值的枚举（`enum Foo : int { A = 1 }`）直接使用指定值
- 不带值的枚举从 0 开始自动编号
- `EnumType.Member` 引用在 TypeChecker 的 `TypeEnv` 中注册为常量符号

---

## Phase 2 改进预告（引入 Rust 思想）

以下特性将在完成基础实现后引入：

| 特性 | C# Phase 1 | Rust-influenced Phase 2 |
|------|-----------|------------------------|
| 内存模型 | GC（.NET 运行时）| 所有权 + 借用，无 GC |
| 可变性 | 默认可变 | 默认不可变，显式 `mut` |
| 错误处理 | 异常（`try/catch`）| `Result<T, E>` + `?` 运算符 |
| 接口分发 | 虚函数表（运行时） | Trait 静态分发（零开销）|
| 模式匹配 | `switch` 表达式 | `match` 表达式（穷尽检查）|
| 空安全 | `T?` 可空类型 | `Option<T>`，编译期穷尽检查 |
| 并发 | `async/await` + Task | `async/await` + Send/Sync trait |
| 枚举 | abstract record 层次 | 代数类型（真正的 sum type）|
