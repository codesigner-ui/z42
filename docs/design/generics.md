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