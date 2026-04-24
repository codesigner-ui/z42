# z42 静态抽象接口成员 — 设计文档

> **状态**：设计（2026-04-24），实现尚未开始
> **参考**：C# 11 static abstract interface members + Rust traits
> **目标**：一套机制统一 operator 重载 / INumber 数值约束 / Rust trait 等价物

---

## 1. 动机

当前 z42 有**三套独立但功能重叠**的机制：

1. **C# 风 operator 重载**（commit `12a3854`）— `public static T operator +(T, T)` 静态方法
2. **INumber 实例方法接口**（commit `d5a1913`）— `interface INumber<T> { T op_Add(T other); }` 以实例方法形式
3. **普通 interface 虚方法**（L3-G2）— 泛型约束 + 虚派发

三套机制让 stdlib 和用户代码重复声明、语义混乱：
- `struct Vec2` 想参与泛型 `Sum<T: INumber<T>>` + 自己写 `a + b` —— 必须**同时**声明静态 operator + 实例 op_Add
- 编译器 desugar 必须"静态优先 / 实例兜底"双路径
- `T.Zero` / `T.Parse(s)` 这类类型级 API **完全无法**表达

**目标**：**单一机制**覆盖所有场景。

## 2. 参考设计

### 2.1 C# 11 Static Abstract Interface Members

C# 11（2022 年发布）引入 **static abstract interface members**，让接口可以声明静态算子和成员：

```csharp
public interface INumber<T> where T : INumber<T> {
    static abstract T operator +(T a, T b);
    static abstract T operator -(T a, T b);
    static abstract T Zero { get; }
    static abstract T Parse(string s);
}

public struct MyInt : INumber<MyInt> {
    public int value;
    public static MyInt operator +(MyInt a, MyInt b) => new(a.value + b.value);
    public static MyInt operator -(MyInt a, MyInt b) => new(a.value - b.value);
    public static MyInt Zero => new();
    public static MyInt Parse(string s) => new(int.Parse(s));
}

// 泛型代码通过约束派发到 T 的静态成员
public static T Sum<T>(IEnumerable<T> xs) where T : INumber<T> {
    T acc = T.Zero;                  // 派发到实现者的静态 Zero
    foreach (var x in xs) acc = acc + x;  // 派发到实现者的静态 operator +
    return acc;
}
```

**核心语义**：
- `static abstract` 成员在接口里是**契约声明**，不带实现
- 实现者类型必须提供**静态成员**，签名完全匹配（replacing T with self type）
- 泛型代码 `where T: I` 可以对 T 做类型级操作（`T.Member` / `a op b`）
- 运行时派发：JIT 通过泛型实例化（对 struct 是 monomorphized，对 class 是 shared code + TypeDesc）

### 2.2 Rust Traits（对照）

```rust
pub trait Num: Sized + Copy {
    fn zero() -> Self;
    fn parse(s: &str) -> Self;
}
pub trait Add<Rhs = Self> {
    type Output;
    fn add(self, rhs: Rhs) -> Self::Output;
}

pub fn sum<T>(xs: &[T]) -> T where T: Num + Add<Output = T> + Copy {
    let mut acc = T::zero();
    for &x in xs { acc = acc + x; }
    acc
}
```

**对比要点**：

| 特性 | C# 11 | Rust | z42 决策 |
|------|:-----:|:----:|:-------:|
| 接口声明静态方法 | ✅ | ✅ | ✅ |
| 接口声明静态算子 | ✅ `operator +` | ✅ `trait Add` | ✅ `operator +` |
| 类型级调用 `T::foo` | ✅ `T.foo()` | ✅ `T::foo()` | ✅ `T.foo()`（C# 语法）|
| 关联类型 | ❌ | ✅ `type Output` | ❌（L3-G3c 延后） |
| 高阶类型 HKT | ❌ | ❌ | ❌ |
| 默认方法（DIM） | ✅ C# 8+ | ✅ trait 默认实现 | ⏸ 延后 |
| 约束继承 | ✅ `I: J` | ✅ `trait A: B` | ⏸ 延后 |
| 特化 / 单态化 | JIT 混合 | 编译期单态 | 代码共享 + type_args |

**结论**：C# 11 的静态抽象机制 ≈ Rust traits 的 ~95%。z42 按 C# 11 设计即可。

### 2.3 C# 11 设计的不足 / z42 微调

| C# 11 问题 | z42 调整 |
|----------|---------|
| 默认接口方法 (DIM) 历史包袱复杂 | **z42 不支持 DIM**（至少初版不支持），所有接口方法都是抽象的 |
| `static abstract` 双关键字冗长 | **保留双关键字**以保持 C# 语法兼容，便于 C# 用户迁移 |
| `T.StaticMember` 访问依赖 JIT 运行时类型参数 | **z42 采用值驱动派发**作为主模式（对 operator 场景足够）；类型级访问 `T.Zero` 延后到 L3-R 反射子集 |
| CRTP 约束 `where T : INumber<T>` 语法冗余但必要 | **同样保留**，这是类型安全的代价 |
| 没有关联类型 | **z42 同不支持**（与 C# 一致；等 L3-G3c） |

---

## 3. z42 语法设计

### 3.1 接口声明

```z42
public interface INumber<T> where T : INumber<T> {
    // 静态抽象运算符
    static abstract T operator +(T a, T b);
    static abstract T operator -(T a, T b);
    static abstract T operator *(T a, T b);
    static abstract T operator /(T a, T b);
    static abstract T operator %(T a, T b);

    // 静态抽象方法（非运算符）
    static abstract T Parse(string s);

    // 静态抽象属性
    static abstract T Zero { get; }
    static abstract T One  { get; }

    // （继续可以混入实例方法）
    string ToDisplayString();
}
```

**语法规则**：
- `static abstract` 双关键字必须相邻（顺序不限，建议 `static abstract`）
- 静态抽象成员只能是：方法、运算符、属性（`{ get; }` 形式）
- 静态抽象方法/算子**没有 body**（`;` 结尾）
- 静态抽象属性**没有 getter body**（`{ get; }`）
- 一个接口可以混合 instance 方法和 static abstract 成员

### 3.2 实现者

```z42
public struct int : INumber<int> {
    public static int operator +(int a, int b) { return a + b; }
    public static int operator -(int a, int b) { return a - b; }
    // ... 5 个运算符
    public static int Parse(string s) { /* native / script */ }
    public static int Zero { get { return 0; } }
    public static int One  { get { return 1; } }
}

public struct Vec2 : INumber<Vec2> {
    int x; int y;
    Vec2(int a, int b) { this.x = a; this.y = b; }

    public static Vec2 operator +(Vec2 a, Vec2 b) { return new Vec2(a.x + b.x, a.y + b.y); }
    // ...
    public static Vec2 Zero { get { return new Vec2(0, 0); } }
    public static Vec2 One  { get { return new Vec2(1, 1); } }
}
```

**实现验证规则**：
- 对每个接口静态抽象成员，class/struct 必须提供**同名 static 成员**
- 签名比较：把接口签名里的 T 替换为**实现者自己**（self-type）后，应该与 class 的 static 成员签名相同
- 成员类别必须匹配（operator vs 普通方法 vs 属性）

### 3.3 泛型用法

```z42
T Sum<T>(T[] xs) where T : INumber<T> {
    T acc = T.Zero;                          // ← 类型级访问 (iter 2 ⏸)
    foreach (var x in xs) {
        acc = acc + x;                       // ← 运算符派发 (iter 1 ✅)
    }
    return acc;
}

void Main() {
    var total = Sum(new int[] { 1, 2, 3 });          // T=int
    var vecs  = Sum(new Vec2[] { new Vec2(1,1), new Vec2(2,3) }); // T=Vec2
}
```

### 3.4 与 existing operator 重载的关系

完全兼容 —— C# 11 的运算符声明语法和 C# 1-10 一致。用户写的 `public static Vec2 operator +(Vec2, Vec2)` **既是 C# 标准运算符重载，也满足 `INumber<Vec2>` 契约**。一处声明两用。

---

## 4. 语义与类型检查

### 4.1 存储结构

`Z42InterfaceType` 增加 `StaticMethods` 和 `StaticProperties`（可暂合为一个 dict）：

```csharp
public sealed record Z42InterfaceType(
    string Name,
    IReadOnlyDictionary<string, Z42FuncType> Methods,      // instance
    IReadOnlyList<Z42Type>? TypeArgs = null,
    IReadOnlyDictionary<string, Z42FuncType>? StaticMethods = null,   // static abstract methods + operators
    IReadOnlyDictionary<string, Z42Type>?     StaticProperties = null // iter 2+
);
```

### 4.2 实现者验证

扩展 `SymbolCollector.Classes.cs` 第四遍：

```
对 class C : INumber<C>：
  查 INumber.StaticMethods 每一条 (name, sig)：
    把 sig 里的 T 替换为 C（self-substitution）→ 期望 class sig
    检查 C.StaticMethods[name] 匹配期望 sig
    不匹配 → E0412 InterfaceMismatch
  查 INumber.Methods（instance）每一条：同 L3-G2 现有逻辑
```

### 4.3 运算符派发（方案 A：值驱动）

**情况 1**：非泛型、具体类型的 `a + b`
- `a.Type = Vec2`, `b.Type = Vec2`
- 查 `Vec2.StaticMethods["op_Add"]` 签名 `(Vec2, Vec2) -> Vec2`
- 生成 `BoundCall(Static, class=Vec2, method=op_Add, args=[a, b])`
- **已实现**（commit `12a3854`）

**情况 2**：泛型、T 约束 `where T : INumber<T>` 的 `a + b`
- `a.Type = Z42GenericParamType("T")`
- 查约束接口 `INumber<T>.StaticMethods["op_Add"]`
- **编译期无法确定 T**
- 生成**新的 BoundCall 种类**：`InterfaceStaticCall(interface=INumber, method=op_Add, args=[a, b])`
- IR 层：新指令 `OpInterfaceStaticCall(iface_name, method_name, args)` 或复用 VCall 但 kind=InterfaceStatic
- VM 运行时：
  - 取 `args[0]` 的 concrete type（primitive → class_name 如 `Std.int`；Object → type_desc.name）
  - 查该 class 的 `StaticMethods[method_name]`
  - 调用，返回结果
- **值驱动 limitation**：`args[0]` 必须是非 null 值；对 `T.Zero` 这类无参静态成员**不适用**

### 4.4 类型级访问（方案 B：类型参数驱动）— iter 2+

**情况 3**：`T.Zero` / `T.Parse(s)`
- 需要在泛型 callsite 传递 T 的 TypeDesc
- z42 已有 L3-G3a 的 constraint metadata；扩展为 runtime type_args 传递
- 本设计 iter 1 不覆盖

### 4.5 与现有 L3-G2 约束体系的关系

- `where T: I` 的 interface 约束已经存在
- 对 instance 方法派发仍然走 vtable（不变）
- 对 static 方法派发走新路径（值驱动 / 类型驱动）
- 同一约束可同时提供 instance 和 static 成员，互不冲突

---

## 5. 运行时模型

### 5.1 值驱动静态派发

新 IR 指令（或 VCall 扩展）：

```
%ret = static_call_via_iface  "INumber"  "op_Add"  %a  %b
```

VM 执行：

```rust
Instruction::StaticCallViaIface { dst, iface_name, method, args } => {
    let receiver = frame.get(args[0])?;
    // 查 receiver 的 class name（primitive 走 primitive_class_name，Object 走 type_desc.name）
    let class_name = resolve_concrete_class(receiver)?;
    // 静态方法名约定：{class}.{method}
    let fn_name = format!("{}.{}", class_name, method);
    let callee  = module.lookup_function(&fn_name)?;
    exec_function(module, callee, &collected_args)?
}
```

### 5.2 性能考虑

- primitive 的 `a + b` 编译为 StaticCallViaIface → VM 派发到 `Std.int.op_Add`
- `Std.int.op_Add` body 是 `return a + b` → IR AddInstr
- 开销：**1 次函数调用 + 原生加法**（vs 直接 AddInstr）
- 对泛型代码可接受；未来可加 IR-level specialization（iter 3+）

### 5.3 与现有 primitive_class_name 机制复用

`src/runtime/src/interp/exec_instr.rs` 已有：

```rust
pub(crate) fn primitive_class_name(obj: &Value) -> Option<&'static str> {
    match obj {
        Value::I64(_)  => Some("Std.int"),
        Value::F64(_)  => Some("Std.double"),
        ...
    }
}
```

新的 StaticCallViaIface 指令 dispatch 复用此函数 + Object.type_desc 组合。

---

## 6. 实现路线图

### 迭代 1 — operator 派发（本轮）
- **阶段 1**：Parser + AST（`static abstract` 在接口）✅ 已完成
- **阶段 2**：`Z42InterfaceType.StaticMethods` 存储 ✅ 已完成
- **阶段 3**：实现者验证（接口静态抽象成员漏缺检查）
- **阶段 4**：stdlib INumber 迁移到 static abstract
- **阶段 5**：primitive struct (int/long/double/float) 改为提供 static operator
- **阶段 6**：generic `a + b` 派发（TypeChecker 生成 InterfaceStaticCall）
- **阶段 7**：新 IR 指令 + VM 执行
- **阶段 8**：TSIG 跨 zpkg 导出/导入 StaticMethods
- **阶段 9**：测试 + 文档 + 归档

### 迭代 2 — 类型级访问（未来）
- `T.Zero` / `T.Parse(s)` 表达式支持
- runtime type_args 传递（L3-R 子集）
- static abstract **属性**（iter 1 仅支持方法 / operator；属性延后）

### 迭代 3 — 性能优化（未来）
- Primitive 泛型 `a + b` IR-level 特化（StaticCallViaIface → AddInstr）
- 避免运行时 dispatch 开销

---

## 7. 迁移影响

### 7.1 需要重写的 stdlib

**当前** (`z42.core/src/INumber.z42`):
```z42
public interface INumber<T> {
    T op_Add(T other);         // 实例方法
    // ...
}
```

**迁移后**:
```z42
public interface INumber<T> where T : INumber<T> {
    static abstract T operator +(T a, T b);
    // ...
}
```

**当前** (`z42.core/src/Int.z42`):
```z42
public struct int : ..., INumber<int> {
    public int op_Add(int other) { return this + other; }  // 实例
}
```

**迁移后**:
```z42
public struct int : ..., INumber<int> {
    public static int operator +(int a, int b) { return a + b; }
}
```

### 7.2 对 commit `12a3854` operator 重载的影响

`12a3854` 的 TypeChecker desugar 逻辑分两支：
1. 静态 operator → BoundCall(Static) —— **保留**
2. 实例 op_Add（泛型 / 类直接实现）→ BoundCall(Virtual) —— **本次改写**

改写后：
- 泛型 T 约束 → InterfaceStaticCall 新路径
- 用户类直接 `a + b` → 仍用静态 operator BoundCall

最终**移除 TryLookupInstanceOperator 路径**（iter 1 结束时）。

### 7.3 golden test 影响

- `87_generic_inumber`：依赖原 INumber 实例方法，需改写或标记废弃
- `88_operator_overload`：静态 operator，**不受影响**

---

## 8. 诊断与错误

| 错误码 | 触发 |
|-------|------|
| E0412 InterfaceMismatch | class 未提供接口的 static abstract 成员 |
| E0412 InterfaceMismatch | static abstract 方法签名不匹配 |
| E0412 InterfaceMismatch | class 提供了 instance 方法但接口要求 static（或反之）|
| E0201 ExpectedToken | `static abstract` 后缺少方法签名 |
| E0413 InvalidImpl | 接口体内静态抽象成员格式错误 |

---

## 9. 明确的 Scope

### iter 1 **包含**：
- 接口 `static abstract` 运算符（5 个：+ - * / %）
- 接口 `static abstract` 方法（非运算符、非属性）
- 实现者静态成员验证
- 泛型 `a + b` on `T: INumber<T>` 派发（InterfaceStaticCall IR + VM）
- stdlib INumber / primitive struct 迁移
- TSIG 跨 zpkg

### iter 1 **不包含**（iter 2+ 做）：
- 静态抽象**属性** `T Zero { get; }`
- 类型级访问 `T.Zero` / `T.Parse(s)`
- 静态抽象**字段**（C# 也不支持）
- 静态抽象**构造器**（C# 也不支持）
- 默认接口方法（DIMs）
- 接口继承 `interface I : J`
- 一元运算符、比较运算符、相等运算符（独立迭代）

---

## 10. 关键设计权衡记录

### 为什么值驱动派发而非类型参数驱动？
- z42 的代码共享哲学：一份字节码服务所有 T
- 运算符场景下 receiver (args[0]) 始终可用 → 值驱动足够
- 类型参数传递需要 L3-R 完整支持，规模大 → 延后

### 为什么保留 `static abstract` 双关键字？
- 与 C# 11 一致
- 显式表达"这是契约"意图
- 未来加 DIM 时不需要新关键字

### 为什么不做关联类型（type Output）？
- C# 11 也没有
- 实现复杂（约束求解 + 统一）
- 对 operator / INumber 不是必需（同质算子够用）
- 延后到 L3-G3c

### 为什么要求 `where T : INumber<T>` 的 CRTP 约束？
- 让 `static abstract T operator +(T, T)` 里的 T 准确指向实现者自身
- 防止 `class Foo : INumber<Bar>` 这种错配
- 与 C# 11 一致

---

## 11. 相关文档

- `docs/design/generics.md` — L3-G 泛型总体设计
- `docs/design/philosophy.md` §8 — Simplicity First + Script-First
- `openspec/changes/archive/2026-04-23-add-inumber-script-first/` — INumber iter 1（将被本设计替代）
- `openspec/changes/archive/2026-04-24-add-operator-overload-csharp/` — operator 重载语法（本设计扩展）
