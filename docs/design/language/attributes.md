# 自定义 Attribute 与反射

> 状态：class-level（C3a）+ method-level（C3b）均已落地（2026-06-09）。
> 相关：[reflection.md](reflection.md)（GetType / typeof / 反射对象）。

z42 的 attribute 是**用户自定义的元数据注解**，应用到声明上，运行期经反射读回**活实例**。设计取自 C#，但修正了 C# 的几处长期缺陷（见下「对 C# 的改进」）。

## 用法

attribute 是一个继承 `Std.Attribute` 的普通类，按**真实类名**应用（无后缀约定）：

```z42
using Std;

class Route : Attribute {
    public string Path;
    public string Method;
    // 全部状态走构造器（命名参数 + 默认值）——单一初始化路径
    public Route(string path, string method = "GET") {
        this.Path = path;
        this.Method = method;
    }
}

[Route("/users", method: "POST")]
class UsersController { }

void Demo() {
    Type t = typeof(UsersController);

    // 全部 attribute：活实例，按应用顺序
    Attribute[] all = t.GetCustomAttributes();   // [ Route 实例 ]

    // 按类型单查（不存在返回 null）
    Route r = (Route) t.GetAttribute(typeof(Route));
    Console.WriteLine(r.Path);    // "/users"
    Console.WriteLine(r.Method);  // "POST"

    // 缓存：对同一 Type 重复调用返回同一批实例
    Attribute[] again = t.GetCustomAttributes();   // 同一数组 / 同一实例
}
```

- attribute 参数限**编译期常量**（字面量 / enum 成员 / `typeof`）。
- 应用按真实类名 `[Route]`（**不**接受 `[RouteAttribute]`）。
- `GetAttribute(Type)` 按实例的运行期类型（FullName）比较，故子类 attribute 也匹配。

### API（class-level，z42.core）

| 类型 / 成员 | 说明 |
|------------|------|
| `Std.Attribute` | 所有用户 attribute 的基类 |
| `Type.GetCustomAttributes() : Attribute[]` | 该类型的全部 attribute 活实例（缓存）|
| `Type.GetAttribute(Type) : Attribute?` | 指定类型的单个 attribute，无则 null |

### method-level（C3b）

方法（含顶层函数）上的 attribute 同样可反射，经 `MethodInfo`：

```z42
class Service {
    [Doc("lists users")] [Route("/users")]
    public void List() { }
}

Type t = typeof(Service);
foreach (MethodInfo m in t.GetMethods()) {
    if (m.Name == "List") {
        Attribute[] attrs = m.GetCustomAttributes();        // [ Doc, Route ]
        Doc d = (Doc) m.GetAttribute(typeof(Doc));          // d.Text == "lists users"
    }
}
```

机制完全镜像 class-level：编译期合成工厂（key `mth$<Class>$<Method>`）；元数据载体改为 **SIGS section**（per-function，vs class 的 TYPE section）；运行期 `__method_custom_attributes(qualified)` 按方法限定名解析 `Function.custom_attributes` → 调工厂 + 缓存。`MethodInfo` 携带隐藏 `__qualified` 供 builtin 解析。

## 对 C# 的改进（z42 取舍）

| # | C# 缺陷 | z42 |
|---|---------|-----|
| 1 | `Attribute` 后缀魔法（`class FooAttribute` 写作 `[Foo]`/`[FooAttribute]`，两种拼法）| **无后缀**：`class Route : Attribute` 即写作 `[Route]`，单一拼法 |
| 2 | 双初始化路径（positional→ctor，named→public 字段直写）| **单一 ctor 路径**：全部参数走构造器（复用 named-arg + 默认值）|
| 3 | 每次 `GetCustomAttributes()` 重新分配新实例 + 返 `object[]` | **缓存单例**：首次实例化一次，后续返同一批 |
| 4 | 实例可变（正是 #3 必须复制的根因）| **不可变实例**（ctor 内一次写定）→ 缓存安全 |
| 5 | `AttributeUsage` 是自循环元属性 + 反直觉默认 | **推后**；将来做一等声明子句（非元属性）|

**刻意保留的 C# 约束**：参数限编译期常量（安全 + "attribute 是数据非行为"心智模型）；attribute 被动（数据，不主动变换被注解项——主动注解属 z42 customization/macro 层，与此分开）。

## 实现原理

### Factory thunk：活实例不碰 0.5.x-deferred 的 Activator/Invoke

attribute 构造**全编译期已知**（已知类、已知构造器、常量参数），无需运行时泛型实例化。编译器为每个 `[Foo(args)]` 应用合成一个**无参工厂函数**（[AttributeFactorySynthesizer](../../../src/compiler/z42.Semantics/Codegen/AttributeFactorySynthesizer.cs)，pre-typecheck，复用 BenchmarkDesugar 合成模式）：

```z42
public Attribute __attr$cls$UsersController$0() { return new Route("/users", method: "POST"); }
```

工厂的**返回类型是 `Attribute` 基类**——于是普通 typecheck 顺带强制了 attribute 契约，错误锚定在应用点：
- 非 `Attribute` 派生类 → return 上转型失败（"不是 attribute"）；
- 非常量参数 → 在无参工厂作用域里成为未知标识符；
- 构造器不匹配 → 正常重载解析报错。

无需独立 validator pass。

### 元数据持久化（zbc 1.10）

每个 class 在 `.zbc` TYPE section 记 `attr_count: u16` + 每条 (`type_name`, `factory_func`) 字符串引用（见 [runtime/zbc.md](../runtime/zbc.md) 1.10）。运行期 loader 把它们载入 `TypeDescCold.custom_attributes`。

### 反射时构造 + 缓存

native `__type_custom_attributes(type)` 对每条 ref 用 `run_returning` 调工厂函数拿活实例（`exec_function` 的每调用状态在栈局部 `Frame`，native→VM re-entrancy 安全）。z42 层 `Type.GetCustomAttributes()` 把结果缓存到 Type 对象（`__attrCache`），故重复调用返回同一批实例。跨 zpkg：工厂函数经 lazy loader 跨模块解析。

## Deferred / Future Work

### ~~attribute-future-method-level（C3b）~~ — 已落地
- **状态**：method-level（`MethodInfo.GetCustomAttributes()` / `GetAttribute`）已落地 2026-06-09（add-attribute-reflection-methods，SIGS section per-function attr refs，zbc 1.11）。见上文「method-level（C3b）」。

### attribute-future-crossfile-property-resolution（编译器 bug，C3b 发现）
- **来源**：add-attribute-reflection-methods 实施。
- **现象**：z42.core **同包内跨文件**访问 extern `{ get; }` 属性，访问点被误解析为**字段**（field_index miss → null），而非属性 getter 调用。即 `MethodInfo.z42`（Std.Reflection）里 `someType.FullName`（`Type.FullName` 定义在 Type.z42）→ 读不存在的字段 "FullName" → null。跨**包**（主模块 import z42.core）经 TSIG 正常；同包跨文件失配。
- **当前 workaround**：把属性比较逻辑放到属性定义所在文件——`Type.FindByType(...)` 静态方法定义在 Type.z42（`FullName` 可见处），`Type.GetAttribute` + `MethodInfo.GetAttribute` 都委托它（MethodInfo 加 `using Std` 后以 `Type.FindByType` 非限定调用）。
- **触发条件**：修编译器同包跨文件符号收集，使属性（非仅字段）对其他文件可见。属于反射/stdlib 之外的编译器根因，记此备查。

### attribute-future-dedicated-diagnostics
- **来源**：add-attribute-reflection（C3a）。
- **触发原因**：契约违例当前借工厂 typecheck 报通用错误（"cannot return X"/"unknown identifier"），非专用 E09xx（"X 不是 attribute"/"参数须为常量"）。
- **触发条件**：需要更清晰诊断 + stdlib-aware negative 测试 harness 时。

### attribute-future-attributeusage
- **触发原因**：MVP 不校验 target（attribute 可标任意支持位）。
- **触发条件**：需要 target 限制时；做成声明上的一等子句（非 C# 元属性自循环），默认 AllowMultiple=true、无隐式继承。

### attribute-future-targets-fields-params
- **触发原因**：MVP 仅 class（C3a）+ method（C3b）；field / parameter 目标未做。

### attribute-future-generic-and-typed-lookup
- **触发原因**：泛型 attribute 类 + `GetAttribute<T>()` 泛型糖依赖 0.5.x 泛型方法实例化。MVP 用 `GetAttribute(typeof(T))`。

### attribute-future-raw-args-view
- **触发原因**：factory-thunk 只给活实例，无 C# `CustomAttributeData` 式不实例化读原始 ctor 参数的视图。
