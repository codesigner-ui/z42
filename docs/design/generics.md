# z42 泛型设计

> L3 核心特性。本文档定义泛型的语法、约束体系、编译策略和 VM 运行时支持。

---

## 设计目标

1. **Rust 级约束表达力** — trait bounds（`+` 组合）、关联类型（`Output=T`）、自引用约束
2. **C# 级代码共享** — 一份字节码服务所有实例化，零代码膨胀
3. **完整反射支持** — `typeof(T)`、`is`/`as` 运行时可用，TypeDesc 携带 type_args
4. **大型工程友好** — 编译时间和代码体积不随泛型实例化数量爆炸

---

## 方案对比与选型

### 三种主流策略

| 维度 | 单态化（Rust/C++） | 类型擦除（Java） | 代码共享 + 具化（C#） |
|------|-------------------|-----------------|---------------------|
| **代码膨胀** | 严重（N 类型 × M 泛型） | 无 | 引用类型无，值类型有 |
| **运行时性能** | 最优（零开销） | 装箱/拆箱 | 接近单态化 |
| **反射** | 不支持 | 部分丢失 | 完整支持 |
| **编译速度** | 慢 | 快 | 中等 |
| **约束表达力** | trait bounds（极强） | `<? extends T>`（弱） | `where T : I`（中等） |

### z42 选型：代码共享 + Rust 约束

| 决策 | 选择 | 理由 |
|------|------|------|
| **字节码策略** | C# 代码共享 | Value 枚举天然统一所有类型，一份代码 |
| **运行时类型** | 具化（Reified） | TypeDesc 携带 type_args，支持反射 |
| **约束语法** | Rust trait bounds | `where T: A + B`，比 C# 更灵活 |
| **关联类型** | Rust 风格 | `where T: Add<Output=T>`，C# 不支持 |
| **值类型特化** | 不做 | z42 Value 统一为 I64/F64，特化收益极小 |

### 为什么不选纯 Rust 单态化

z42 的 `Value` 枚举（`I64 | F64 | Bool | Str | Object | ...`）在 VM 层面已经是统一表示。即使单态化生成了 `List_int` 和 `List_string` 两份代码，VM 执行时仍然通过 Value tag 做 dispatch——**单态化不消除 Value dispatch 开销，但引入代码膨胀**。

### 为什么不选 Java 类型擦除

Java 的擦除导致运行时类型信息完全丢失（`List<int>` 和 `List<string>` 不可区分），无法支持 `typeof(T)`、`is T`、`as T` 等操作。z42 选择 C# 的具化模型保留完整类型信息。

---

## 语法设计

### 泛型函数

```z42
T Identity<T>(T x) {
    return x;
}

T Max<T>(T a, T b) where T: IComparable<T> {
    return a.CompareTo(b) > 0 ? a : b;
}
```

### 泛型类

```z42
class Stack<T> {
    T[] items;
    int count;

    Stack(int capacity) {
        this.items = new T[capacity];
        this.count = 0;
    }

    void Push(T item) {
        items[count] = item;
        count = count + 1;
    }

    T Pop() {
        count = count - 1;
        return items[count];
    }
}
```

### 泛型接口

```z42
interface IComparable<T> {
    int CompareTo(T other);
}

interface IEnumerable<T> {
    IEnumerator<T> GetEnumerator();
}
```

---

## 约束体系

### 基础约束（与 C# 对齐）

| 约束 | 语法 | 含义 |
|------|------|------|
| 接口约束 | `where T: ISomething` | T 必须实现该接口 |
| 基类约束 | `where T: BaseClass` | T 必须继承自该类 |
| 引用类型 | `where T: class` | T 必须是引用类型 |
| 值类型 | `where T: struct` | T 必须是值类型 |
| 构造器 | `where T: new()` | T 必须有无参构造器 |
| enum | `where T: enum` | T 必须是 enum 类型 |

### Rust 风格增强

| 约束 | 语法 | C# 能否做到 |
|------|------|------------|
| **多约束组合** | `where T: ISerializable + ICloneable` | C# 用 `,` 分隔，z42 用 `+`（Rust 风格） |
| **自引用约束** | `where T: IComparable<T>` | ✅ C# 支持 |
| **交叉类型参数** | `where K: IHash, V: ICloneable` | ✅ C# 支持 |
| **关联类型** | `where T: IAdd<Output=T>` | ❌ C# 不支持，Rust 支持 |
| **嵌套约束** | `where T: IIterator<Item=U>, U: IDisplay` | ❌ C# 不支持，Rust 支持 |

### 关联类型

```z42
interface IAdd<Rhs> {
    type Output;
    Output Add(Rhs other);
}

// 使用
T Sum<T>(List<T> items) where T: IAdd<T, Output=T> {
    var acc = items[0];
    for (int i = 1; i < items.Count; i++) {
        acc = acc.Add(items[i]);
    }
    return acc;
}
```

### 约束检查时机

| 阶段 | 职责 |
|------|------|
| **TypeChecker（编译时）** | 验证所有约束满足；类型参数传播；关联类型解析 |
| **IrGen（编译时）** | 生成共享字节码；type_params 元数据写入 IrFunction |
| **VM（运行时）** | 不检查约束（编译时已保证）；仅通过 TypeDesc.type_args 支持反射 |

---

## 编译策略

### TypeChecker 扩展

```
// 泛型函数调用 Max<int>(a, b)
1. 解析 T=int
2. 检查 int 是否满足 where T: IComparable<T>
3. 将函数体内所有 T 替换为 int 进行类型检查
4. 报告类型检查结果（不生成多份代码）
```

### IrGen 输出（代码共享）

```
// 源码
T Max<T>(T a, T b) where T: IComparable<T> { ... }
var r = Max<int>(3, 5);

// 生成的 IR（一份代码，不分 int/string）
.func @Max  params:2  ret:object  mode:Interp
  .type_params  T
  .constraints  T: IComparable<T>
  .block entry
    ; a = %0, b = %1
    %2 = v_call  %0.CompareTo  %1    ; T.CompareTo(T) — 通过 vtable 分发
    %3 = const.i64  0
    %4 = gt  %2, %3
    br.cond  %4  then  else
  .block then
    ret  %0
  .block else
    ret  %1

// call site
%5 = const.i64  3
%6 = const.i64  5
%7 = call  @Max  %5, %6              ; 无需 @Max_int
```

### zbc 二进制扩展

SIGS section 扩展：

```
每个函数签名追加：
  type_param_count: u8
  Per type_param:
    name_idx: u32  (STRS pool)
```

TypeDesc 扩展（类实例化时）：

```
TypeDesc {
    name: "Stack",
    type_params: ["T"],           // 泛型定义的参数名
    type_args: [],                // 非实例化时为空
    // ... 现有字段 ...
}

// 实例化后（运行时创建）
TypeDesc {
    name: "Stack<int>",
    type_params: ["T"],
    type_args: ["int"],           // 具体类型参数
    // fields/vtable 与 Stack 共享
}
```

---

## 运行时支持

### 反射

```z42
var stack = new Stack<int>(10);
var type = stack.GetType();
// type.Name == "Stack<int>"
// type.TypeArgs == ["int"]

if (stack is Stack<int>) {
    // true — 运行时类型信息完整
}
```

### TypeDesc 扩展

```rust
pub struct TypeDesc {
    pub name: String,
    pub base_name: Option<String>,
    pub fields: Vec<FieldSlot>,
    pub field_index: HashMap<String, usize>,
    pub vtable: Vec<(String, String)>,
    pub vtable_index: HashMap<String, usize>,

    // === L3 泛型新增 ===
    /// 泛型参数名：["T"]、["K", "V"]
    pub type_params: Vec<String>,
    /// 实例化时的具体类型：["int"]、["string", "int"]
    /// 定义时为空，实例化时填充。
    pub type_args: Vec<String>,
    /// 关联类型映射：{"Output" => "int"}
    pub assoc_types: HashMap<String, String>,
}
```

### 实例化机制

当 VM 遇到 `new Stack<int>(10)` 时：

1. 查找 `Stack` 的 TypeDesc（泛型定义）
2. 创建 `Stack<int>` 的实例化 TypeDesc（clone + 填充 type_args）
3. 缓存实例化 TypeDesc（相同 type_args 共享）
4. ScriptObject 引用实例化后的 TypeDesc

```rust
/// 泛型实例化缓存：(generic_name, type_args) → TypeDesc
type_instantiation_cache: HashMap<(String, Vec<String>), Arc<TypeDesc>>
```

---

## 实施路线

| 子阶段 | 内容 | 涉及模块 | 状态 |
|--------|------|---------|:----:|
| **L3-G1** | 泛型函数 + 泛型类（无约束） | Parser → TypeChecker → IrGen → VM | ✅ |
| **L3-G2** | 接口约束（`where T: I + J`） | Parser + TypeChecker + stdlib | ✅ |
| **L3-G3** | 关联类型 + zbc 格式扩展 + VM 运行时约束校验 + 反射 | 接口声明 + TypeChecker + VM loader | 📋 |
| **L3-G4** | 泛型标准库（`List<T>`、`Dict<K,V>` 原生化 + primitive 接口实现） | stdlib 重构 | 📋 |

## L3-G2 落地细节（2026-04-22）

### 语法：`where` 子句

放在签名尾部、`{` / `=>` 前：

```z42
T Max<T>(T a, T b) where T: IComparable<T> { ... }

class Sorted<T> where T: IComparable<T> + IEquatable<T> { ... }

void Copy<K, V>(K k, V v) where K: IHashable, V: ICloneable { ... }
```

- `+` 组合单参数上的多约束（Rust 风格）
- `,` 分隔不同类型参数的约束项（C# 熟悉感）

### 语义

- `Z42GenericParamType(Name, Constraints)` 携带约束接口列表
- 泛型体内 `t.Method()` 在 constraint 接口的方法表中查找，dispatch 为 VCall
- 调用点（泛型函数 / `new Class<T>(...)`）编译期校验类型参数实现所有约束
- 未约束的 T 上任何方法调用直接报错（E0402）
- 自由函数调用时 T 从实参推断后做约束校验；返回类型也按推断做 T → 具体类型替换（`Max<T>(T, T) → T` 调用时返回类型替换为推断出的 T）

### 限制（本阶段）

- primitive 类型（int/string/...）**未**实现 interface，`Max<int>(1, 2)` 暂不可用（L3-G4 配合 stdlib 泛型化同步放开）
- 约束不写入 zbc 二进制（仅编译期使用），VM 不做运行时校验（**L3-G3 必须补齐**）
- 其他约束范式排期见 L3-G2.5 子迭代（见下）

## L3-G2.5 基类约束（2026-04-22 增量）

在 L3-G2 interface 约束基础上补齐基类约束：

```z42
class Animal { public int legs; }
class Dog : Animal { ... }

void Introduce<T>(T pet) where T: Animal {
    Console.WriteLine(pet.legs);   // 基类字段
    pet.Describe();                // 基类方法（VCall）
}

// 组合基类 + 接口
void F<T>(T x) where T: Animal + IDisplay { ... }
```

### 语义约定

- **基类唯一**：单继承模型，`where T: A + B` 其中 A B 都是类 → 报错
- **基类必首位**：`where T: IFoo + Animal` → 报错（遵循 C# 惯例，简化解析）
- **成员查找顺序**：先基类字段/方法，后接口方法（声明顺序）
- **调用点校验**：typeArg 须等于或继承自基类；复用 `SymbolTable.IsSubclassOf`（O(1) 祖先集）
- **VM 行为**：不区分约束类型 — 方法调用一律 VCall，按 T 实际 TypeDesc.vtable 分发

### 引用 / 值类型约束（L3-G2.5 refvalue，2026-04-22）

```z42
T Default<T>() where T: class { return null; }                // T 限引用类型
void LogValue<T>(T v) where T: struct { Console.WriteLine(v); } // T 限值类型
```

**实现要点**：
- `GenericConstraint.Kinds: [Flags] GenericConstraintKind`（Class / Struct），与类型约束 `Constraints: List<TypeExpr>` 并列
- `GenericConstraintBundle.RequiresClass / RequiresStruct` 传到 ValidateGenericConstraints
- `class` 满足：`Z42ClassType && !IsStruct` 或 `IsReferenceType(t) == true`（含 interface / array / option / string）
- `struct` 满足：`Z42ClassType && IsStruct` 或 `Z42PrimType && !IsReferenceType`（int/bool/float/double/char）
- 互斥：`where T: class + struct` → 编译期报错
- 组合：`where T: class + IFoo` / `where T: struct + IBar` / `where T: BaseClass + class` 都允许

### 源码级泛型容器清理（L3-G4g，2026-04-22）

基于 L3-G4f 基础进行一次容器实现清理：

- **`Std.Collections.ArrayList<T>`** 补齐 `Contains` / `IndexOf`（通过 `where T: IEquatable<T>`）
- **`Std.Collections.HashMap<K,V>`** 新上线 — 开放寻址 + 线性探测，支持 indexer / Set / Get / ContainsKey / Remove / Clear / Count
- **Stub 文件** (`List.z42` / `Dictionary.z42` / `HashSet.z42`) 清理，指向实际实现

**修复的基础设施 gap**：

1. **libsDirs 走查向上搜索**（`PackageCompiler.BuildLibsDirs`）：stdlib 包互编时
   能看到兄弟包的 zpkg（之前 `projectDir/artifacts/z42/libs` 不存在即完全丢依赖）
2. **TSIG 不再导出 imported 类**（`ExportedTypeExtractor.ExtractClasses`）：以前
   `sem.Classes` 包含本地+导入，TSIG 会重新导出导入的类，导致消费方把
   `new HashMap<...>()` 路由到错误命名空间（如 `Std.Text.HashMap`）
3. **Z42ArrayType `T[]` 赋值跨约束态**：字段存的 `T[]`（无约束）与方法体内
   active 约束下的 `T[]` 之前记录相等性失败；现在按元素名忽略约束差异
4. **IEquatable<T> 增加 GetHashCode**：配对 Equals 对应的哈希能力，让
   `HashMap<K,V>` 可以 `key.GetHashCode()` + `key.Equals(other)` 同时依赖 constraint

**发现的 z42 语义限制**（记入 L3-G4h）：

- **pseudo-class List/Dictionary 仍并存**：真替换要先有 foreach iterator 协议（L3-G4h）

### 短路求值（L3-G4h step 1，2026-04-22）

`&&` / `||` 在 IR 层 desugar 为 `BrCond` 控制流块，右侧仅在左侧未定结果时求值：

- `a && b` → `if a then b else false`
- `a || b` → `if a then true else b`

保留 `AndInstr` / `OrInstr` 仅服务于位运算 `&` / `|`（bool 操作数不再使用）。
`HashMap.FindSlot` 随即回归自然写法 `occupied[s] && !keys[s].Equals(k)`。
golden test `82_short_circuit` 覆盖：左真/左假 RHS 副作用观察、null-guard 惯用法、链式 &&/||、优先级混用。

### 构造器约束（L3-G2.5 ctor，2026-04-23）

`where T: new()` — 要求类型实参有无参构造器。语法复用 `+` AND 分隔器：

```z42
class Factory<T> where T: class + new() {
    T Create() { return null; }  // body 内 `new T()` 待 L3-R 实现
}

void Main() {
    var f1 = new Factory<Widget>();   // ✅ Widget() 无参 ctor 可见
    var f2 = new Factory<int>();      // ✅ primitive 默认构造
    var f3 = new Factory<NeedsArg>(); // ❌ NeedsArg 只有 NeedsArg(int)
    var f4 = new Factory<IShape>();   // ❌ interface 不可实例化
}
```

**实现范围**：
- 编译期**校验**完整（`TypeChecker.HasNoArgConstructor`）
- 实际 **`new T()` 泛型 body 实例化未实现** —— 依赖 L3-R 的运行时 type_args 传递机制
  （code-sharing IR 下 T 被擦除，无法在 body 知道具体 class name）
- zbc / TSIG flags bit `0x10` 承载 `RequiresConstructor`；与现有 class/struct/base/tp-ref
  共享 flags 字节，所有 flag 可组合

**设计决策记录（2026-04-23 写入）**：

- **约束合取使用 `+`**（Rust 风格）而不是 `,`（C# 风格）：`where T: A + B, U: C + D` 一条
  `where` 同时覆盖多参数 + 多约束；`,` 只用于切换参数。C# 的 `where T: A, B where U: C`
  repeated-where 更冗长，视觉易与 `Dictionary<K, V>` 冲突
- **不支持 OR 约束 `T: A | B`**：主流语言都没有（C# / Java / Rust / Swift / Scala），
  核心困难是函数 body 只能调用 A ∩ B 的方法交集，实用价值低；替代方案（共同基接口 /
  方法重载 / 和类型 ADT）更清晰。z42 遵循主流约定，不做 OR 约束

### enum 约束（L3-G2.5 enum，2026-04-23）

`where T: enum` — 要求类型实参是 z42 原生 enum 类型。用于泛化 flags、解析器、
序列化工具（`Parse<T: enum>(string) -> T`、`AllValues<T: enum>() -> T[]` 等场景）。

```z42
enum Color { Red = 10, Green = 20, Blue = 30 }

T Identity<T>(T x) where T: enum {
    return x;   // body 内 T 被擦除为 i64，enum 反射式操作待 L3-R
}

void Main() {
    var c = Identity(Color.Green);   // ✅ Color 是 enum
    // Identity(42);                  // ❌ int 不是 enum
    // Identity<IShape>(...);         // ❌ interface 不是 enum
}
```

**实现范围**：
- 语义层新增 `Z42EnumType : Z42Type`（`SymbolCollector` / `SymbolTable` 的
  `ResolveType` 在 enum 名查到时发射 `Z42EnumType` 而非 fallback 到 `Z42PrimType`）
- 编译期校验完整（`TypeChecker.IsEnumArg`）；`Z42EnumType` 满足 `struct` 约束
  （enum 是值类型），不满足 `class` 约束
- body 内 `T.Values` / `T.Parse` / flags 位运算等反射式操作**依赖 L3-R**
  运行时 type_args 传递；本迭代只做约束校验
- zbc / TSIG flags bit `0x20` 承载 `RequiresEnum`

**互斥规则**：
- `class + enum` 被拒（enum 是值类型）
- `struct + enum` 允许但冗余（enum 已隐含 struct 语义）
- `enum + new()` 允许（enum 天然 default-constructible）
- `enum + IXxx<...>` 允许；enum 暂不能 implements interface（待 L3-R）

### INumber 数值约束（L3-G2.5 INumber 迭代 1，2026-04-23）

**接口形态**：`Std.Numerics.INumber<T>`（独立在 `z42.numerics` 包，与 `z42.core`
解耦，按需引入，不给核心库加体积）。

```z42
// z42.numerics/src/INumber.z42
public interface INumber<T> {
    T op_Add(T other);
    T op_Subtract(T other);
    T op_Multiply(T other);
    T op_Divide(T other);
    T op_Modulo(T other);
}
```

**关键结论**：INumber **不是新约束 kind**，就是普通接口约束 —— 走 L3-G2 既有
的 `where T: I<T>` 机制。与 `enum` 约束（flag 关键字、新 GenericConstraintKind
bit、新语义类型）本质不同：INumber 零新语法 / 零新 AST / 零新 bundle 字段。

**Primitive 桥接**（L3-G4b 既有表的 1 行扩展）：

```csharp
// TypeChecker.PrimitiveImplementsInterface
("int" or "long" or ..., "IComparable" or "IEquatable" or "INumber") => true,
("float" or "double" or ..., "IComparable" or "IEquatable" or "INumber") => true,
```

primitive `int` / `long` / `float` / `double` 自动满足 `INumber<T>`（self-referential，
与 IComparable 一致；`int` 满足 `INumber<int>` 而非 `INumber<long>`）。

**VM 运行时路由**（与 `.CompareTo()` 同机制）：

```rust
// primitive_method_builtin
(Value::I64(_), "op_Add")  => Some("__int_op_add"),
(Value::F64(_), "op_Add")  => Some("__double_op_add"),
// ... Subtract / Multiply / Divide / Modulo 对称
```

整数走 `wrapping_*` 与现有 `AddInstr` 一致；浮点走 IEEE 754（Inf/NaN on div-by-zero）。

**调用点**（迭代 1 — 显式方法调用）：

```z42
T Double<T>(T x) where T: INumber<T> {
    return x.op_Add(x);    // 显式；迭代 2 加 `x + x` 自动 desugar
}

void Main() {
    Console.WriteLine(Double(21));    // 42
    Console.WriteLine(Double(1.5));   // 3.0
}
```

**Scope 限制与后续迭代**：

- 迭代 1（已完成）：5 个算术方法 + 4 主流数值类型 + stdlib 接口定义
- 迭代 2（规划）：`CheckBinary` 检测 `Z42GenericParamType + INumber<T>` 操作数，
  将 `a + b` 自动 desugar 到 `a.op_Add(b)`；零新语法，零用户类重写
- 未来 operator 重载：新 `operator` 关键字，编译为同名 `op_Add` 方法，
  INumber 接口不变。**前向兼容，无 rework**
- 明确不做：混合类型算术（`int + double`）、checked 算术（溢出抛异常）、
  BigInteger / Decimal

### 跨参数约束链校验（L3-G2.5 chain，2026-04-23）

接口约束的 TypeArgs 现在参与校验而不仅仅是名字匹配：

- `Z42InterfaceType` 新增可空 `TypeArgs` 字段；`SymbolTable.ResolveGenericType`
  遇到带参接口引用时构造带 TypeArgs 的新实例
- `ValidateGenericConstraints` 在检查前用当前 call-site 的 type-arg map
  替换接口 TypeArgs 里的 type-param 引用（`IEquatable<U>` + U=string → `IEquatable<string>`）
- Primitive 类型只满足自指 `I<Self>` —— `int` 实现 `IEquatable<int>` 但不实现
  `IEquatable<string>`；Z42GenericParamType 按 name + args 双重匹配

```z42
class Foo<T, U> where T: IEquatable<U>, U: IEquatable<U> { ... }

void Main() {
    var ok  = new Foo<int, int>(0, 0);        // ✅ int 满足 IEquatable<int>
    var bad = new Foo<int, string>(0, "x");   // ❌ int 不满足 IEquatable<string>
}
```

### 类侧 TypeArg 跟踪（L3-G2.5 chain class-ifaces，2026-04-23）

Class 声明的接口列表升级后：

- AST `ClassDecl.Interfaces` 从 `List<string>` 改为 `List<TypeExpr>`（parser 用
  `TypeParser.Parse` 代替 skip-generic-params）
- `SymbolTable.ClassInterfaces` 从 `Dictionary<string, HashSet<string>>` 改为
  `Dictionary<string, List<Z42InterfaceType>>`，每个条目携带完整 TypeArgs
- `ClassSatisfiesInterface` 走类继承链，对齐 name + args（Instantiated 类通过
  `BuildSubstitutionMap` 做 typeparam 替换）
- `TypeArgEquals` 辅助：class / prim / generic-param 按**名字**比对（避免 record
  结构相等在 collection phase stub vs full 间错配）

```z42
interface IEquatable<T> { bool Equals(T other); }
class IntEq : IEquatable<int> { ... }          // 存 [int]
class Pair<U> : IEquatable<U> { ... }          // 存 [U]

class Foo<T> where T: IEquatable<int> { ... }

new Foo<IntEq>()         // ✅ IntEq 的 IEquatable<int> 精确匹配
new Foo<Pair<int>>()     // ✅ U→int 替换后 IEquatable<int> 匹配
new Foo<Pair<string>>()  // ❌ 实际是 IEquatable<string>
```

**留待独立迭代**：TSIG 当前只按名字序列化 imported 类的 interface
（`ExportedClassDef.Interfaces: List<string>`）；跨 zpkg 消费 `class Foo: IEquatable<int>`
时 args 丢失。本地类 arg-aware 检查 100% 生效，imported 类走 lenient 路径。

### 跨 zpkg 约束消费（L3-G3d，2026-04-23）

stdlib 泛型类的 `where` 子句现在随 zpkg TSIG 传播给消费方，`TypeChecker` 编译期
（而非 VM 加载期）即可拒绝非法类型实参：

- **新 TSIG 字段**：`ExportedClassDef.TypeParamConstraints` / `ExportedFuncDef.TypeParamConstraints`
  （`List<ExportedTypeParamConstraint>?`），接口 / 基类 / tp-ref 以名字存储；
  forward-compat guard 保证旧 zpkg 不读失败
- **序列化布局**（writer / reader 对称）：`u8 count`，每条
  `{u32 tpName, u8 flags, u8 ifaceCount, u32* ifaces, [u32 base]?, [u32 tpRef]?}`
- **Consumer rehydration**：`ImportedSymbolLoader` 携带原始列表；`TypeChecker` 在
  Pass 0.5 后 `MergeImportedConstraints` 将名字解析回 `Z42InterfaceType` /
  `Z42ClassType` 产出 `GenericConstraintBundle`；本地 decl 同名 win
- **覆盖面**：`new ImportedGeneric<T>()` 与 `ImportedGenericFunc<T>(...)` 现在都
  会触发 `ValidateGenericConstraints`

```z42
// 消费端零改动；stdlib List.z42 的 where 会被读到消费方 TypeChecker
class NonEq { int x; NonEq() { this.x = 0; } }

void Main() {
    var bad  = new List<NonEq>();  // ❌ compile error: NonEq 不实现 IEquatable / IComparable
    var good = new List<int>();    // ✅ int 通过 VM primitive 协议满足 IEquatable + IComparable
}
```

**测试**：`TsigConstraintsTests` —— zpkg 二进制 round-trip、负向拒绝、正向通过三个
单元测试验证端到端。

**留到 G2.5 chain**：约束接口的 TypeArgs 当前仍按名字匹配（比如 `T: IEquatable<U>`
只校验"T 必须实现 IEquatable"不检查 U 是否对齐）—— 跨参数链接的完整校验随后独立迭代。

### Pseudo-class List/Dictionary 正式退场（L3-G4h step 3，2026-04-22）

`List<T>` / `Dictionary<K,V>` 从编译器 pseudo-class 快路径迁移到纯源码实现：

- **新源码类**：`Std.Collections.List<T> where T: IEquatable<T> + IComparable<T>`、
  `Std.Collections.Dictionary<K,V> where K: IEquatable<K>`。旧的中间产物
  `ArrayList<T>` / `HashMap<K,V>` 源文件**已删除**，其能力合并到 List/Dictionary。
- **Count 统一为 `public int Count` 字段**（直接字段读），替代原来的 `Count()` 方法。
  foreach 协议同时支持 `Count` 字段和 `Count()` 方法，自动适配。
- **新增 `List.Sort` / `Reverse` / `Remove`**：Sort 使用插入排序（约束 `T: IComparable<T>`）；
  Remove 通过 IndexOf + RemoveAt 组合；Reverse 原地反转。
- **编译器清理**：
  - `SymbolTable.ResolveGenericType` 删除 `List`/`Dictionary` pseudo-class 映射
  - `FunctionEmitterExprs.EmitBoundNew` 删除 `__list_new` / `__dict_new` 分支
  - `FunctionEmitterCalls`/`TypeChecker.Calls` 的 `IsBuiltinCollectionType` 收窄到
    `Array` / `StringBuilder`（StringBuilder 仍走 builtin，Array 走 `__list_*`）
  - `ResolveBuiltinMethod` 仅保留 StringBuilder 方法映射；`__list_*` / `__dict_*`
    builtin 不再被编译器发射（VM 侧保留实现，等未来彻底删除）
- **VM 无感知**：`new List<int>()` 现在实例化 `Std.Collections.List` 对象，`Add` /
  `Contains` / `Sort` 等走 Instance/VCall 正常分发；原 `__list_*` / `__dict_*` 仍然
  存在，但没有编译器发射路径。
- **测试迁移**：
  - `80_stdlib_arraylist` 改名语义：源代码改用 `List<T>`（文件名保留作为迭代标识）
  - `81_stdlib_hashmap`：改用 `Dictionary<K,V>`
  - `83_foreach_user_class`：ArrayList 替换为 List
  - `18_list` / `20_dict` / `40_list_operations`：零改动直接跑通 —— `new List<int>()`
    与 `.Count` 字段读与旧 pseudo-class API 等价

### foreach 鸭子协议（L3-G4h step 2，2026-04-22）

`foreach` 不再仅限于数组 / Option —— 任何满足以下协议的**类**（含泛型实例化类型）
都可直接被 `foreach` 迭代：

- `int Count()` —— 返回元素个数
- `T get_Item(int i)` —— 按索引取元素（通常由 indexer 语法声明）

codegen 展开为：

```
i = 0
len = coll.Count()
while (i < len) {
    e = coll.get_Item(i)
    // body
    i = i + 1
}
```

Pseudo-class `List<T>` / `Dictionary<K,V>` 保留在数组 / Map fast path（L3-G4h
step 3 会统一迁移）。`TypeChecker.ElemTypeOf` 做类型参数替换，确保 `var e` 推断为
正确的元素类型（`Z42InstantiatedType` 下 `T` → 具体类型）。

golden test `83_foreach_user_class` 覆盖自定义 `Ring<T>` + 标准库 `ArrayList<T>` +
break/continue 控制流。

### 源码级泛型容器（L3-G4f，2026-04-22）

`Std.Collections.ArrayList<T>` 上线，纯 z42 源码实现：
- `T[] items` 动态数组 + `int count / capacity`
- `Add` / `Insert` / `RemoveAt` / `Clear` / `Count` / `IsEmpty` / `Grow`
- `public T this[int index] { get; set; }` 索引器

```z42
var xs = new ArrayList<int>();
xs.Add(1); xs.Add(2); xs.Add(3);
xs[0] = 99;
int n = xs[2];
xs.RemoveAt(1);
```

**策略**：ArrayList 与 pseudo-class `List<T>` 并存。用户可选用源码版本（可扩展 / subclass /
添加方法）或继续用 pseudo-class（foreach 友好、零编译器路径开销）。真正移除
pseudo-class 需要先做 foreach iterator 协议（后续迭代）。

**当前限制**（已记录为 L3-G4f 后续项）：
- **Contains / IndexOf 暂缺**：需要 `where T: IEquatable<T>` 约束。跨 `Std.Collections`
  → `Std` 命名空间的约束解析当前有 gap（qualified name `Std.IEquatable<T>` parser 不接受；
  短名 `IEquatable` 在跨模块作用域里失败）
- **HashMap<K,V> 未交付**：依赖同样的 IEquatable/IHash 约束；先等命名空间解析修好

### 索引器语法（L3-G4e，2026-04-22）

类可声明索引器，使 `obj[i]` / `obj[i] = v` 语法可用：

```z42
class MyList<T> {
    T[] items;
    public T this[int i] {
        get { return this.items[i]; }
        set { this.items[i] = value; }
    }
}

var xs = new MyList<int>();
int a = xs[0];      // → get_Item(0)
xs[1] = 99;          // → set_Item(1, 99)
```

**实现要点**：
- Parser 识别 `<vis>? <type> this [params] { get {..} set {..} }`，desugar 为两个
  FunctionDecl：`get_Item(params) → T` 和 `set_Item(params, T value) → void`（`value` 隐式参数名）
- 至少要有 `get` 或 `set` 之一；支持单独 getter / 单独 setter
- TypeChecker `IndexExpr` 分支：target 为 class / InstantiatedType 且有 `get_Item` → BoundCall VCall；
  array / string 仍走既有 ArrayGet
- 赋值 LHS = `IndexExpr` + class 接收者 + `set_Item` → BoundCall VCall（value 作最后一个参数）
- 泛型类透过 L3-G4a SubstituteTypeParams 替换返回/参数类型
- **零 IR 改动**（复用 VCallInstr）；**零 VM 改动**

**限制**：
- 不支持 auto-indexer（`{ get; set; }` 无 body）— 必须显式写 body
- 不支持 `ref this[...]`
- `set` 的隐式参数名固定为 `value`

### stdlib 导出泛型类（L3-G4d，2026-04-22）

让 user 代码能直接 `new Stack<int>()` 指向 stdlib 的 `Std.Collections.Stack<T>`。

**实现要点**：
- **TSIG 格式扩展**：`ExportedClassDef` 新增 `TypeParams: List<string>?`；ZpkgReader/Writer 在 class 条目尾部增加 `tp_count + name_idx[]`（向前兼容：reader 在 section 剩余空间不足时按 0 处理）
- **ExportedTypeExtractor / ImportedSymbolLoader**：跨 zpkg 保留 TypeParams，imported 泛型类在消费方重建时保持泛型性质
- **SymbolCollector 冲突裁决**：local 同名覆盖 imported — 发现 `_classes` 已有同名且属于 `_importedClassNames`，移除 imported 记录，继续注册 local（不报 duplicate）
- **IrGen QualifyClassName**：local 优先 — `Classes.ContainsKey(n) && !ImportedClassNames.Contains(n)` → 用本模块命名空间；否则 imported → 用 `ImportedClassNamespaces` 映射
- **FunctionEmitterCalls DepIndex 守卫**：仅在接收者非本地类时查 DepIndex（否则 local class 方法会被同名 stdlib 方法劫持）
- **VM ObjNew lazy-load**：type_registry 未命中时调 `lazy_loader::try_lookup_type` 触发 zpkg 按命名空间加载；同样对 ctor 函数用 `try_lookup_function` 兜底

**能力**：
```z42
using-like behavior (no using keyword yet, auto-resolved):
var s = new Stack<int>();   // → Std.Collections.Stack<int>
s.Push(1); s.Pop();

class Stack { ... }          // user 定义同名会覆盖
var local = new Stack();    // → local 版本，stdlib 不生效
```

**限制（L3-G4e/f 继续）**：
- 索引器语法 `T this[int]` 未实现 → `List<T>` / `Dictionary<K,V>` pseudo-class 暂不替换
- qualified `new Std.Collections.Stack<int>()` 语法未支持（L3 后期）
- `using` 导入未支持

### User-level 容器源码实现（L3-G4c，2026-04-22）

验证纯 z42 源码能完整实现泛型容器，无需编译器特判。

`src/runtime/tests/golden/run/76_generic_list/` 展示用户编写的 `class MyList<T>`：
- `T[]` 动态数组字段 + 扩容（`Grow`）
- `Add` / `Get` / `Set` / `RemoveAt` / `Count`
- 同时支持 `MyList<int>` 和 `MyList<string>`（primitive T 由 L3-G4a/G4b 打通）

**已确认的能力**：
- 泛型类 + 泛型方法 + 泛型 `T[]` 数组（L3-G1/G4a）
- 实例化类型替换（L3-G4a）— `list.Get(0)` 返回具体类型
- Primitive T 和约束（L3-G4b）

**剩余工作（L3-G4d）—— stdlib 导出的泛型类**：
- 名称解析：当前 `new Stack<int>()` 若 stdlib 和 user 都定义了 `Stack` 会冲突
- 需要 SymbolCollector 识别 imported 泛型类并正确路由 ObjNew 到 `Std.Collections.Stack`
- VM VCall 对导入类的 type_desc.name 应为命名空间限定全名

`src/libraries/z42.collections/src/Stack.z42` 和 `Queue.z42` 里以注释形式保留了
参考实现，待 L3-G4d 放开导出路径后即可启用。

### Primitive 类型实现 interface（L3-G4b，2026-04-22）

让 `Max<int>(3, 5)` 真正可用 — primitive 类型（int/double/bool/char/string）
编译期视为实现了 `IComparable<T>` / `IEquatable<T>`；运行时 VCall 分派到
corelib builtin。

```z42
T Max<T>(T a, T b) where T: IComparable<T> {
    return a.CompareTo(b) > 0 ? a : b;
}

Max(3, 5);         // int — OK（L3-G2 只支持 user class，现在 primitive 也行）
Max("a", "b");     // string
Max(1.5, 2.5);     // double
```

**实现要点**：
- TypeChecker `PrimitiveImplementsInterface(primName, ifaceName)` 硬编码表：
  - 整型 / 浮点 / string / char 满足 `IComparable` + `IEquatable`
  - `bool` 只满足 `IEquatable`
- `TypeSatisfiesInterface` 对 `Z42PrimType` 查表；编译期透明
- VM `primitive_method_builtin(Value, method)` 映射到 corelib builtin（interp + JIT 共用）：
  - int  → `__int_compare_to` / `__int_equals` / `__int_hash_code` / `__int_to_string`
  - double/bool/char/string 同构
- 无 IR / zbc 格式改动；VCall 指令不变，dispatch 在 VM 内部分支

### 实例化类型替换（L3-G4a，2026-04-22）

泛型类实例化后，成员访问 / 方法调用的类型需按 type args 替换。

```z42
class Box<T> {
    T value;
    T Get() { return this.value; }
}

var b = new Box<int>(42);
int n = b.Get() + 1;    // Get() 返回 int（而非未替换 T）
int v = b.value;        // 字段 value 为 int
```

**实现要点**：
- `Z42InstantiatedType(Definition, TypeArgs)` 承载实例化形式
- `ResolveGenericType` 当 TypeArgs 数量匹配 TypeParams 时返回 Z42InstantiatedType（否则回退到裸 ClassType 保持 L3-G1 行为）
- `TypeChecker.SubstituteTypeParams(Z42Type, map)` 递归替换 Z42GenericParamType — 覆盖 Array / Option / Func / 嵌套 Instantiated
- BindMemberExpr / BindCall 识别 Z42InstantiatedType 接收者，用 `BuildSubstitutionMap` + Substitute 得到替换后的字段/方法签名
- `IsAssignableTo` / `IsReferenceType` 处理新类型（同 Definition 且 TypeArgs 相等即可赋）
- zbc / IR / VM 无改动（代码共享不变，IR 层仍是单一未实例化形式）

### 裸类型参数约束（L3-G2.5 bare-tp，2026-04-22）

```z42
class Container<T, U> where U: T {
    U child;
    Container(U c) { this.child = c; }
}

// 调用点校验：Dog 必须是 Animal 的子类型
var c = new Container<Animal, Dog>(new Dog());   // ✅
var x = new Container<Animal, Vehicle>(...);      // ❌ E0402
```

**实现要点**：
- `GenericConstraintBundle.TypeParamConstraint: string?` 存另一 type-param 名
- `ResolveWhereConstraints` 优先识别 NamedType ∈ active type params（早于 class/interface 分派）
- 体内成员查找：`SymbolTable.LookupEffectiveConstraints` 做"一跳"合并 — U 查找命中不了走 T 的 bundle
- 调用点 `ValidateGenericConstraints`：拿到 typeArgs 映射后，比较 `typeArg[U]` 与 `typeArg[T]` 的子类型关系（IsSubclassOf；非 class 退回相等）
- zbc 版本 0.5 → 0.6；bundle flag bit3 + 条件 `type_param_name_idx`
- Rust VM `verify_constraints` 对裸 type-param 引用跳过（本地即解）

**限制**：
- 一跳策略：`U: V, V: T` 的两跳不支持（实际场景少）
- primitive / interface 作 typeArg 时只认相等性（不做 primitive 子类型）

### 其他约束范式排期

见 `docs/roadmap.md` L3-G2.5 表：

- `new()` — 依赖 VM 运行时类型参数（L3-R 批次）
- `notnull` — 后续小迭代（与 z42 可空性语义协同设计）

### L3-G3a 已完成（2026-04-22）

- zbc 版本 0.4 → 0.5：SIGS / TYPE section 每个 type_param 追加约束布局
  - `flags: u8`（bit0 RequiresClass / bit1 RequiresStruct / bit2 HasBaseClass）
  - `[if bit2] base_class_name_idx: u32`
  - `interface_count: u8 + interface_name_idx[] × u32`
- C# IR: `IrFunction.TypeParamConstraints` / `IrClassDesc.TypeParamConstraints` 与 `TypeParams` 按索引对齐
- Rust VM: `Function.type_param_constraints` / `TypeDesc.type_param_constraints` 读取并保留
- Rust loader: 加载后运行 `verify_constraints`，对未知 class/interface 引用返回 `InvalidConstraintReference`；对 `Std.*` 前缀和 `I<Upper>...` 接口名放行（分别由 lazy loader 和 L3-G3b 反射补齐）
- ZpkgReader: SIGS 扫描同步跳过新字段

### L3-G3 剩余子阶段

1. **L3-G3b 反射接口**：`type.Constraints` / `t is IComparable<T>` 运行时依据元数据判断
2. **L3-G3c 关联类型**：`type Output; Output=T`
3. **L3-G3d 跨 zpkg**：TSIG section 扩展携带约束，消费方 TypeChecker 做跨模块校验
4. **运行时 Call/ObjNew 约束校验**：代码共享下 type_args 不可得；等 L3-G3b 反射机制出来后再决定如何拿 type_args 并 enforce

### L3-G1 详细 pipeline

```
Parser:     解析 <T> 类型参数列表、where 子句
AST:        FunctionDecl/ClassDecl 新增 TypeParams 字段
TypeCheck:  类型参数作用域；泛型实例化时替换 T → 具体类型
IrGen:      生成共享代码 + .type_params 元数据
ZbcWriter:  SIGS section 写入 type_param_count + names
VM loader:  读取 type_params → TypeDesc
VM interp:  ObjNew 时创建实例化 TypeDesc（填充 type_args）
```

---

## 约束与 Trait 的关系

> z42 features.md 规划：L3 引入 `Trait` 替代 `interface` 作为零开销抽象。

泛型约束天然与 Trait 结合：

```z42
// Trait 定义（L3 后期）
trait Display {
    string Format();
}

// 泛型约束使用 Trait
void Print<T>(T item) where T: Display {
    Console.WriteLine(item.Format());
}
```

**当前阶段（L3-G1/G2）先用 interface 作为约束目标**，Trait 系统独立实现后可无缝替换。

---

## 与现有 pseudo-class 的迁移路径

当前 `List<T>` 和 `Dictionary<K,V>` 是 pseudo-class（编译器特殊处理 + VM builtin）。泛型实现后：

| 阶段 | 状态 |
|------|------|
| L3-G1/G2 | pseudo-class 保持不变；泛型仅用于用户定义的类型 |
| L3-G4 | `List<T>` → 真正的泛型类（替代 pseudo-class） |
| L3-G4 | `Dictionary<K,V>` → 真正的泛型类 |
| L3-G4 | Queue/Stack/HashSet → 新增泛型集合 |
| L3-G4 | 标准库接口类改成泛型的(IComparable, IEquatable等) |