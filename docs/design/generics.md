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

| 子阶段 | 内容 | 涉及模块 | 前置 |
|--------|------|---------|------|
| **L3-G1** | 泛型函数 + 泛型类（无约束） | Parser → TypeChecker → IrGen → VM | — |
| **L3-G2** | 接口约束（`where T: I + J`） | TypeChecker 约束验证 | L3-G1 |
| **L3-G3** | 关联类型（`type Output; Output=T`） | 接口声明 + TypeChecker | L3-G2 |
| **L3-G4** | 泛型标准库（`List<T>`、`Dict<K,V>` 原生化） | stdlib 重构 | L3-G2 |

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