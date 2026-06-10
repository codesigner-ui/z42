# 反射（Reflection）

> 状态：MVP 落地（add-reflection-mvp, 2026-06-08）—— 只读元数据检视。
> 完整版（`Method.Invoke` / `Activator` / `MakeGenericType`）随 generic instantiation 在 0.5.x L3-R 落地。

z42 反射让程序在**运行时只读地检视类型**——字段、方法、基类、泛型实参。API 形态参照 C# `System.Type` / `System.Reflection`。

## 用法

入口是 `Object.GetType()`：

```z42
using Std;
using Std.Reflection;

class Point {
    public int X;
    public int Y;
    public int Dist2() { return this.X * this.X + this.Y * this.Y; }
}

void Demo() {
    Point p = new Point();
    Type t = p.GetType();

    // 类型身份
    Console.WriteLine(t.Name);        // "Point"
    Console.WriteLine(t.FullName);    // "<ns>.Point"
    Console.WriteLine(t.BaseType.Name); // "Object"

    // 字段（含继承的实例字段）
    foreach (FieldInfo f in t.GetFields()) {
        Console.WriteLine(f.Name + " : " + f.FieldType.Name);  // "X : int" ...
    }

    // 方法（含继承 / 虚方法）
    foreach (MethodInfo m in t.GetMethods()) {
        Console.WriteLine(m.Name + " -> " + m.ReturnType.Name);
        foreach (ParameterInfo pi in m.GetParameters()) {
            Console.WriteLine("  " + pi.Name + " : " + pi.ParameterType.Name);
        }
    }
}
```

### typeof(T)（编译期类型 → Type）

`typeof(T)` 求值为 `Std.Type`（与 `obj.GetType()` 同一类型身份），不再是字符串：

```z42
using Std;

class Point { public int X; public int Y; }

void Demo() {
    Type t1 = typeof(int);
    Console.WriteLine(t1.Name);              // "int"（i32→int 规范化）

    var tp = typeof(Point);
    Console.WriteLine(tp.Name);              // "Point"
    Console.WriteLine(tp.GetFields().Length); // 2 —— 带真句柄，成员可枚举

    Point p = new Point();
    Type viaGetType = p.GetType();
    // typeof 与 GetType 一致（同一类型身份）
    Console.WriteLine(tp.Name == viaGetType.Name); // true
}
```

**关键能力**：编译器在 `FunctionEmitter.VisitTypeof` 把目标类型解析成**限定名**（用户类经 `QualifyClassName` → `<ns>.Point`），运行时 `make_type_from_name` 据此命中主模块 `type_registry` → **真 `TypeDesc` 句柄**，故 `typeof(Point).GetFields()` 可枚举成员（不像早期 desugar-to-string 方案只有名字）。基础类型（`int`/`string`/…）→ synthetic Type（Name/FullName 有值）。

> **限制**：`var x = obj.GetType()` 的 `var` 从 `GetType()` 的导入返回类型推断，当前不带属性派发能力（`x.Name` 为 null）——用**显式** `Type x = obj.GetType()`（注解直接解析为 Type 类）。typeof 的 `var tp = typeof(Point)` 不受此限（`BoundTypeof.Type` 已是解析好的 Std.Type 类）。详见 Deferred `reflection-future-gettype-var-inference`。

### Type.GetProperties()（属性视图）

`GetProperties()` 返回该类型的属性（含继承），从 `get_<X>` / `set_<X>` 访问器约定派生——getter 使属性可读，setter 使其可写，同名 getter+setter 合并为一条：

```z42
using Std.Reflection;

class Account {
    public int Balance { get; set; }    // read-write
    public string Owner { get; }        // read-only
}

void Demo() {
    Type t = typeof(Account);
    foreach (PropertyInfo p in t.GetProperties()) {
        Type pt = p.PropertyType;        // 局部接收者（链式属性派发限制）
        Console.WriteLine(p.Name + " : " + pt.Name + " r=" + p.CanRead + " w=" + p.CanWrite);
    }
    // Balance : int r=true w=true
    // Owner   : string r=true w=false
}
```

- 访问器方法（`get_X` / `set_X`）**仍出现在 `GetMethods()`**（与 C# 一致）；`GetProperties()` 是叠加的属性视图。
- 无 `GetValue` / `SetValue`（依赖 0.5.x `Invoke`）。基础类型 / 数组的 Type 返空。

### API（MVP）

| 类型 | 命名空间 | 成员 |
|------|---------|------|
| `Type` | `Std`（z42.core）| `Name` / `FullName` / `BaseType` / `IsAbstract` / `IsSealed` / `GetFields()` / `GetMethods()` / `GetMembers()` / `GetProperties()` / `GetGenericArguments()` |
| `MemberInfo` | `Std.Reflection` | `Name`（`FieldInfo` / `MethodInfo` / `PropertyInfo` 的基类）|
| `FieldInfo : MemberInfo` | `Std.Reflection` | `FieldType : Type` / `IsStatic` / `GetCustomAttributes()` / `GetAttribute(Type)` |
| `MethodInfo : MemberInfo` | `Std.Reflection` | `ReturnType : Type` / `IsStatic` / `IsVirtual` / `GetParameters()` |
| `ParameterInfo` | `Std.Reflection` | `Name` / `ParameterType : Type` / `Position` |
| `PropertyInfo : MemberInfo` | `Std.Reflection` | `PropertyType : Type` / `CanRead` / `CanWrite` |

`FieldType` / `ReturnType` / `ParameterType` / `BaseType` 返回的都是 `Type` 对象（不是字符串），与 C# 一致。

## 实现原理

### 为什么所有反射类型都在 z42.core

`Object.GetType()` 返回 `Type` → `Type` 必须在 z42.core（否则 z42.core 依赖外部包）。`Type.GetFields()` 返回 `FieldInfo`、`FieldInfo.FieldType` 又返回 `Type` → 若 `FieldInfo` 在独立 `z42.reflection` 包，则 `z42.core ↔ z42.reflection` 循环依赖，包构建顺序无解。

解法照搬 .NET mscorlib：`System.Type` 与 `System.Reflection.*` 同在核心程序集、仅命名空间不同。z42 对应 `Std.Type` + `Std.Reflection.*` 同在 **z42.core**。独立 `z42.reflection` 包留给 0.5.x 的 `Method.Invoke` / `Activator`（它们可**单向**依赖 core）。

### 为什么不拆 Type / TypeInfo（统一模型，2026-06-09 定）

z42 用**单一 `Std.Type`** 同时承载类型身份（`Name`/`FullName`/`BaseType`/泛型实参）与成员检视（`GetFields`/`GetMethods`/`GetMembers`/attributes），**刻意不引入 C# 的 `Type` / `TypeInfo` 拆分**。

**C# 拆分的来龙去脉**：原始 .NET Framework（2002–）是统一 `Type`，无拆分。`TypeInfo` 2012（.NET 4.5）为 WinRT 受限 profile 引入，2016（.NET Core / Standard 1.x）为「模块化 corelib + AOT trimming（反射 pay-for-play）」推广强制——`type.GetTypeInfo()` 过桥。仪式负担招致反感，2017（.NET Standard 2.0 / Core 2.0）把反射 API 搬回 `Type`；`TypeInfo` 今天仅作 back-compat 残骸存活。**.NET 自己兜一圈又收敛回统一 `Type`。**

为什么 z42 直接用统一模型：

1. **无 back-compat 包袱**：绿地语言，没有理由背 `TypeInfo` 这个残骸；直接落在 .NET 收敛到的统一形态。
2. **拆分的技术驱动（trim 反射栈）在 z42 不成立**：z42 反射是 **native-delegated**——`Type` 只揣 `NativeData::TypeHandle`，"重"的枚举在 native builtin（`__type_fields` 等），**不在托管 `Type` 类**。要 trim 反射 = 注册不注册那些 builtin，与 `Type` 拆成几个类无关。拆分在 z42 省不下任何东西。
3. **MVP 是只读元数据**：`Invoke` / `Activator` / `MakeGenericType` 在 0.5.x 独立 `z42.reflection` 包——拆分本想隔离的"重"部分这里根本不在。
4. **拆分对 z42 是纯仪式负成本**：`typeof` / `GetType` 高频返回 `Type`；拆了要么处处 `GetTypeInfo()`，要么两边都挂成员 API（等于没拆 + 双份 native plumbing），还破坏 typeof→Type / `Type.GetCustomAttributes` / `MethodInfo` 等已落地 API。
5. **与 z42 一贯取舍一致**：[attributes.md](attributes.md) 已修了 C# attribute 5 处缺陷；不再把 C# 最被诟病的反射缺陷搬进来。

> 层级上仍有一处可对齐的空间（`Type` 当前不是 `MemberInfo`、且在 `Std` 而非 `Std.Reflection`）——见 Deferred `reflection-future-type-memberinfo-hierarchy`。但那是"统一得更彻底"而非"拆分"，统一模型的方向不变。

### Type 如何连回真实元数据：NativeData::TypeHandle

每个堆对象（`ScriptObject`）持有 `Arc<TypeDesc>`（类身份）。`GetType()` 时 VM 手上就有这个 Arc，直接把它存进返回的 `Std.Type` 对象的 `NativeData::TypeHandle(Arc<TypeDesc>)`。反射 builtins（`__type_fields` / `__type_methods` / `__type_base` / `__type_generic_args` / `__type_members`）从该句柄读 `TypeDesc` 枚举成员。

- O(1)、跨 zpkg 安全（Arc 就是真身，无需按名/TypeId 二次查表）。
- 与既有 `NativeData::WeakRef` 同款模式。
- **基础类型 / 数组**（`int` / `T[]` / `string`）的 `GetType()` 返回**无句柄**的 `Std.Type`（`NativeData::None`）：`Name`/`FullName` 有值，但成员查询退化为空数组 / null —— builtins 见无句柄即 lenient 返回空，绝不 `bail!`。

### 成员元数据来源

| 反射数据 | 运行时来源 |
|---------|-----------|
| 字段（名 + 类型）| `TypeDesc.fields: Vec<FieldSlot{name, type_tag}>`（已含继承，base 在前）|
| 方法名 + 虚标志 | `TypeDesc.vtable: Vec<(simple, qualified)>`（虚/继承）+ `cold.own_methods`（声明的非虚）|
| 方法签名（参数类型 / 返回 / 静态）| 按 qualified 名经 `ctx.try_lookup_function` 拿 `Function { param_count, ret_type, is_static, cold.param_types }` —— **已加载在运行时**，无需新 wire 格式 |
| 基类 | `TypeDesc.base_name`（无则 `Std.Object`；`Std.Object` 自身 → null）|
| 泛型实参 | `TypeDesc.cold.type_args`（实例化泛型）|

Member / Type 对象**eager 填充**：每个 builtin 用 `ctx.try_lookup_type("Std.Reflection.FieldInfo")` 等取真实 z42 类 TypeDesc，按 `field_index` 名字写槽。

### 属性派生（无新元数据，add-reflection-properties）

`__type_properties` **不持久化任何 PropertyDesc 元数据**——属性纯运行期从 `get_<X>` / `set_<X>` 方法名约定派生（auto-property 降解为 field + `get_`/`set_` 方法，约定已是编译器既定事实，见 [attributes.md] 同源的 auto-property getter dispatch）。builtin 扫 `TypeDesc.vtable`（含继承，base-first）+ `cold.own_methods`，按前缀配对去重：`get_<X>`（0 逻辑参）记 getter，`set_<X>`（1 逻辑参）记 setter，同名合并；`PropertyType` 取 getter 返回类型（无 getter 时取 setter 参数类型），与 `MethodInfo` 签名同源（`Function.ret_type` / `cold.param_types`，已加载，无新 wire）。**故无 zbc 格式变更**——这是它能在 0.3.x 干净落地（不撞自举 zbc-writer 移植）的关键。逻辑参数 arity 不符（`get_X(args)` / `set_X` 多参）的方法按普通方法跳过，不误判为属性。

### 类修饰符标志（add-reflection-type-flags，zbc 1.12）

`Type.IsAbstract` / `Type.IsSealed` 由 zbc TYPE section 每类的 `flags: u8` 字节背书（bit0 abstract / bit1 sealed / bit2 struct / bit3 record）。编译期 `IrGen.EmitClassDesc` 从 `ClassDecl` 取修饰符，`ZbcWriter` 写在每类记录末尾（attr block 之后）；运行期 `read_type` 载入 `ClassDesc.class_flags` → `build_type_registry` → `TypeDesc.class_flags`（hot，1 字节）。builtin `__type_is_abstract` / `__type_is_sealed` 读对应 bit；handle-less Type（基础类型/数组）→ false。修饰符是**声明级**，不随继承传播。**一次 bump 捕获 4 位**：struct/record 已写进 wire（无 stdlib API），将来 `IsValueType`/`IsRecord` 纯 stdlib 加 getter、不再 bump 格式。`IsStatic` 未暴露——z42 无 `static class` 概念。

### 类静态字段反射（add-reflection-static-fields，zbc 1.13）

`Type.GetFields()` 在实例字段（base-first，含继承）之后追加该类的**静态字段**，每个 `FieldInfo` 带 `IsStatic`（实例 false / 静态 true）。根因处理：运行期原本只有 `VmContext.static_fields`（全局按名 key 的 Value 槽，**无 per-type 类型元数据**），故编译期把静态字段 (名, 类型) 持久化进 zbc TYPE section 的**独立静态字段块**（在 flags 字节之后，与实例字段块同形）。静态字段**独立于** `TypeDesc.fields`（实例热路径布局）——存于 `TypeDescCold.static_fields`（cold，反射专用，普通无静态字段的类不分配 cold）。`builtin_type_fields` 合并两者输出。**MVP 仅声明类自身静态字段**——静态字段非继承存储，不走实例字段的 cross-zpkg base-merge fixup；继承静态反射见 Deferred。

### 类型名规范化

VM 内部有两套 tag 词汇：字段槽用 `"int"`/`"long"`，函数签名用 `"i32"`/`"i64"`/`"str"`。反射在 `make_type_from_name` 处统一映射到 C# 风格别名（`i32→int` / `i64→long` / `str→string` / …），让 `FieldType.Name` 与 `ReturnType.Name` 对同一类型给出一致结果。用户类名（非基础 tag）原样透传。

## 已知限制：链式属性访问需先赋值给局部变量

实测（add-reflection-mvp 验证）发现一个**编译器成员解析限制**（非反射本身）：extern 属性（`Type.Name`/`FullName`/`BaseType`，以及 `FieldInfo.FieldType`/`MethodInfo.ReturnType` 这类 `Type` 字段上的 `.Name`）**只在接收者是局部变量时才正确派发 getter**；接收者是方法调用结果或字段访问结果（链式）时，getter 不被调用，返回 null。

```z42
// ✗ 链式：属性 getter 不派发 → null
var n = obj.GetType().BaseType.Name;
foreach (var f in t.GetFields()) { var x = f.FieldType.Name; }  // f.FieldType.Name → null

// ✓ 先赋值给局部变量
Type t = obj.GetType();
Type bt = t.BaseType;            // 局部接收者 → 派发
string n = bt.Name;
foreach (var f in t.GetFields()) {
    Type ft = f.FieldType;       // 局部
    string x = ft.Name;          // ✓
}
```

extern **方法**（`GetFields()`/`GetMethods()`/`GetGenericArguments()`）不受影响——它们在任意接收者（含链式）上都正确派发（late-bound VCall）。根因是编译器对子表达式上的属性成员解析不完整；修复属编译器改动（与 0.3.x 进行中的 `port-z42c-*` 自举移植冲突），记入下方 Deferred。z42.core 的反射 `[Test]` 用例据此用局部变量写法。

## Deferred / Future Work

### reflection-future-chained-property-dispatch
- **来源**：add-reflection-mvp 验证
- **现象**：`t.BaseType.Name` / `f.GetType().BaseType.Name` 链式 getter 不派发 → 运行期 `FieldGet on Null` 崩溃。局部变量接收者（`Type bt = t.BaseType; bt.Name`）正常。
- **精确根因（2026-06-10 调查，two-part）**：链式接收者的中间类型降级为 `Unknown`/`PrimType`，故外层成员访问命中 Members.cs:174 静默 fallback（当成字段读）→ getter 不派发。两处降级源：
  1. **Object stub 的 `GetType` 返回类型 = `Z42Type.Unknown`**（[SymbolCollector.Classes.cs](../../../src/compiler/z42.Semantics/TypeCheck/SymbolCollector.Classes.cs):93）—— Phase 1 建 stub 时 `Std.Type` 尚未经 `MergeImported` 加载，用 Unknown 占位且**未 fixup**。故 `f.GetType()` 类型为 Unknown，`.BaseType` 立即降级。**修法**：MergeImported 后加 fixup pass，把 Object.GetType 返回类型从 Unknown 升级为真实 `Std.Type` 类（跨阶段降级 fixup 模式，见 philosophy.md）。
  2. **导入的 getter 返回类型经 `ResolveTypeName` 降级**（[ImportedSymbolLoader.TypeResolver.cs](../../../src/compiler/z42.Semantics/TypeCheck/ImportedSymbolLoader.TypeResolver.cs):111-117）—— TSIG 里 getter 返回的 FQN 类名（`ExportedTypeExtractor` emit `Z42ClassType.Name` = FQN）未命中**按短名 key 的** `classes` 字典 → `Z42PrimType(name)` fallback。故即便 part 1 修好，`t.BaseType.Name` 仍因 `get_BaseType` 返回 Prim 而失败。**修法**：ResolveTypeName 在 prim fallback 前，对含 `.` 的名字剥命名空间、用短名重查 `classes`/`interfaces`（类比 fix-fqn-class-resolution）。
- **验证教训**：standalone `z42c <file> --dump-bound`（Z42_LIBS）与 workspace（GoldenTests）的 Std.Type 解析不同，standalone 会误显示 local 形式也 `<unknown>`；复现须用 workspace golden（`src/tests/types/` 经 GoldenTests）。
- **触发条件**：两 part 一起修 + 全测量 blast radius（Members.cs:174 同族 load-bearing 区，须测回归）；与 `port-z42c-*` 协调（改 codegen/typecheck 动 byte-identical，但仅影响用链式反射的程序，自举源码罕用）。
- **当前 workaround**：链式属性访问先赋值给局部变量；extern 方法不受影响。

### reflection-future-type-memberinfo-hierarchy
- **来源**：2026-06-09 "TypeInfo or unify" 设计讨论（User 裁决：不拆 TypeInfo，维持统一 `Std.Type`——见上文 "为什么不拆 Type / TypeInfo"）。
- **触发原因**：统一模型下 `Std.Type` 当前既不是 `Std.Reflection.MemberInfo` 子类、也不在 `Std.Reflection` 命名空间（在 prelude `Std`，为 `typeof`/`GetType` 免 import 的人体工学）；而 .NET 中 `Type : MemberInfo`（类型本身可作为成员——嵌套类型）。对齐后嵌套类型反射可经 `GetMembers()` 自然流过，`Name` 由基类继承而非现两套（`Type` 的 `[Native]` getter vs `MemberInfo` 的字段）。
- **前置依赖**：编译器支持跨命名空间基类（`Std.Type : Std.Reflection.MemberInfo`）+ 调和 native-`Name` 与 field-`Name` 两套机制；可能动 `TypeDesc` 布局 → re-pin zbc。
- **触发条件**：需要嵌套类型反射（`Type.GetNestedTypes()` / `GetMembers()` 含嵌套类型）时；或自举 `port-z42c-*` 镜像反射类时一并对齐。
- **当前 workaround**：无——统一模型功能完整，仅层级未对齐，不影响现有反射能力。

### ~~reflection-future-type-flags~~ — 已落地（2026-06-10）
- **状态**：`Type.IsAbstract` / `Type.IsSealed` 已落地 2026-06-10（add-reflection-type-flags）。zbc TYPE section 每类追加 `flags: u8`（zbc 1.12 / zpkg 0.14），运行期载入 `TypeDesc.class_flags`。见上文「Type.IsAbstract / IsSealed」+「类修饰符标志」。
- **剩余**：`IsStatic` —— z42 无 `static class` 概念，不暴露（bit 位预留）；`IsValueType`（struct）/ `IsRecord` —— **wire 已捕获 struct/record 位**，将来纯 stdlib 加 getter、不再 bump 格式。

### ~~reflection-future-properties~~ — 已落地（2026-06-09）
- **状态**：`Type.GetProperties()` + `Std.Reflection.PropertyInfo`（`PropertyType` / `CanRead` / `CanWrite`）已落地 2026-06-09（add-reflection-properties）。从 `get_`/`set_` 方法约定**运行期派生**（选了 Deferred 里"从方法名推导"的方案而非持久化元数据——零 zbc 格式变更，不撞自举 port）。见上文「Type.GetProperties()」+「属性派生」。
- **剩余**：`GetValue`/`SetValue`（需 0.5.x `Invoke`）；properties 纳入 `GetMembers()`；隐藏 auto-property backing field。

### reflection-future-gettype-var-inference
- **来源**：make-typeof-return-type（C2）验证
- **触发原因**：`var x = obj.GetType()` 的 `var` 从 `GetType()` 的**导入返回类型**推断，该返回类型当前未解析为带属性派发能力的 `Std.Type` 类 → `x.Name` 为 null。根因在反射导入签名解析（与 typeof 无关——typeof 的结果类型由 `BoundTypeof.Type` 显式解析为 Std.Type 类，`var tp = typeof(T)` 正常）。
- **前置依赖**：导入的方法签名返回类型解析为带属性的类（编译器 import 解析改动）。
- **触发条件**：用户期望 `var x = obj.GetType(); x.Name` 直接可用时。
- **当前 workaround**：显式注解 `Type x = obj.GetType()`（注解直接解析为 Type 类，属性派发正常）。

### reflection-future-element-typed-array
- **来源**：add-reflection-mvp（承接更早 `expand-type-metadata` 设想）
- **触发原因**：`T[]` 的 `GetType()` 返回 `Std.Array`，不带元素类型。
- **触发条件**：需要 `int[]` 反射出元素类型 `int` 时。

### reflection-future-parameter-names
- **来源**：add-reflection-mvp
- **触发原因**：参数名仅在 debug symbols（`Function.cold.local_vars`）中，无符号时 `ParameterInfo.Name` 退化为 `arg{n}`。
- **触发条件**：需要稳定参数名反射时。

### ~~reflection-future-static-fields~~ — 已落地（2026-06-10）
- **状态**：`GetFields()` 含静态字段 + `FieldInfo.IsStatic` 已落地 2026-06-10（add-reflection-static-fields）。静态字段元数据持久化进 zbc TYPE section 静态字段块（zbc 1.13 / zpkg 0.15），运行期载入 `TypeDescCold.static_fields`，`GetFields()` 在实例字段后追加（IsStatic=true）。见上文「类静态字段反射」。
- **剩余**：继承的静态字段 —— MVP 仅声明类自身（实例字段经 cross-zpkg fixup 含继承，静态字段不走该 fixup）。见 `reflection-future-inherited-static-fields`。

### reflection-future-inherited-static-fields
- **来源**：add-reflection-static-fields（MVP 划界）
- **触发原因**：静态字段非继承存储（per 声明类），不走实例字段的 cross-zpkg base-merge fixup。MVP `GetFields()` 只返回声明类自身的静态字段。
- **前置依赖**：沿 base 链聚合各祖先类的 `TypeDescCold.static_fields`（运行期 base-walk，类似实例字段 fixup）。
- **触发条件**：用户需要反射子类时拿到继承的静态字段（对齐 C# `GetFields()` 默认含继承静态）。
- **当前 workaround**：反射声明该静态字段的具体类（`typeof(BaseClass).GetFields()`）。

### reflection-future-method-invoke（0.5.x L3-R）
- **来源**：roadmap 0.3.x C 主线划界
- **触发原因**：`Method.Invoke` / `Activator.CreateInstance<T>()` / `Type.MakeGenericType` 强依赖 generic instantiation。
- **触发条件**：0.5.x 泛型 instantiation 落地后，于独立 `z42.reflection` 包提供。

### ~~reflection-future-attributes（C3）~~ — 已落地（class + method）
- **状态**：用户自定义 attribute + 反射全落地 2026-06-09。class-level（C3a，archive/2026-06-09-add-attribute-reflection）+ method-level（C3b，archive/2026-06-09-add-attribute-reflection-methods）。`class Foo : Attribute` + `[Foo(args)]` 标注 class/method → `Type` / `MethodInfo` 的 `GetCustomAttributes()` / `GetAttribute(Type)` 返活实例（缓存）。设计 + 实现原理见 [attributes.md](attributes.md)。
- **剩余**：field/parameter 目标、AttributeUsage、泛型 attribute、专用诊断 —— 见 attributes.md Deferred。

### attr-factory-return-type-resolution — 合成 attribute factory 返回类型解析为 PrimType（compiler bug）

- **来源**：fix-reflection-test-compile（2026-06-10，reflection.z42 编译失败暴露）；fix-qualified-base-upcast 调查（2026-06-10）证伪了"FQN-upcast"初判、定位到真根因。
- **现象**：`class FieldTag : Std.Attribute`（**限定基名**）时，`AttributeFactorySynthesizer` 合成的 `public Attribute __f() { return new FieldTag(...); }` 报 `return type mismatch: expected Attribute, got FieldTag`（[TypeChecker.Stmts.cs:421](../../../src/compiler/z42.Semantics/TypeCheck/TypeChecker.Stmts.cs)）。
- **真根因（调查更正）**：**不是** `IsSubclassOf` 的 FQN 比较问题（`RequireAssignable` 的 `IsSubclassOf` 分支根本没被命中）。插桩显示 `target = Z42PrimType:Attribute` —— 合成 factory 的返回类型 `NamedType("Attribute")` 在**合成顶层函数的解析上下文**里**未解析到 `Std.Attribute` 类，回落成 sentinel `Z42PrimType("Attribute")`**（改成限定 `NamedType("Std.Attribute")` 也回落成 `Z42PrimType:Std.Attribute`）。同一文件里用户手写 `Std.Attribute[] attrs = ...`（reflection.z42:245）作类型注解却能解析 → 差异在**合成函数体的类型解析上下文**（疑似拿不到 file 的 using / imported-class 解析）。属上游 name-resolution 缺陷,非 assignability。
- **诡异点（待解释）**：stdlib-test 编译路径下 unqualified `: Attribute` **能**编译过（reflection.z42 现状即此),但等价单文件 repro（`using Std` + `class Tag : Attribute`）**两种基名都**回落 PrimType。说明失败与"编译上下文(workspace/imported vs 单文件)"强相关。
- **试过无效**：① `IsSubclassOf`/`_ancestors` 短名归一(走不到该分支);② 合成 factory 返回类型改限定 `Std.Attribute`(仍回落 PrimType)。均已回退。
- **当前 workaround**：attribute 类基名写 unqualified `: Attribute`（reflection.z42 即此,绿）。
- **前置依赖 / 触发条件**：根因在 name-resolution(合成函数如何解析 imported 类型),需较深 compiler 调查;与进行中 `port-z42c-*` 自举移植可能冲突 → 独立 change，自举收口后再做。
