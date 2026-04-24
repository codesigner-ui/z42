# Tasks: 静态抽象接口成员（C# 11 对齐）

> 状态：🟡 进行中 | 创建：2026-04-24

**变更说明：** 按 C# 11 的 static abstract interface members 扩展 z42 接口系统。
接口可声明 `static abstract` 方法、运算符和属性；实现者类型必须提供匹配的静态成员；
泛型代码通过约束派发到实现者的静态成员。**一套机制解决 operator 重载 + INumber + Rust trait 等价物**。

**目标形态**：

```z42
public interface INumber<T> where T : INumber<T> {
    static abstract T operator +(T a, T b);
    static abstract T operator -(T a, T b);
    static abstract T operator *(T a, T b);
    static abstract T operator /(T a, T b);
    static abstract T operator %(T a, T b);
    static abstract T Zero { get; }
}

public struct int : INumber<int> {
    public static int operator +(int a, int b) { return a + b; }   // 编译期降为 AddInstr
    public static int operator -(int a, int b) { return a - b; }
    // ... 5 个运算符
    public static int Zero { get { return 0; } }
}

public struct Vec2 : INumber<Vec2> {
    int x; int y;
    public static Vec2 operator +(Vec2 a, Vec2 b) { return new Vec2(a.x+b.x, a.y+b.y); }
    // ...
    public static Vec2 Zero { get { return new Vec2(0, 0); } }
}

T Sum<T>(T[] xs) where T : INumber<T> {
    T acc = T.Zero;                    // 通过 T 约束派发到静态属性
    foreach (var x in xs) acc = acc + x;  // 通过 T 约束派发到静态 operator +
    return acc;
}
```

## 分阶段实施

### 阶段 1：Parser + AST — 接口允许 static abstract 成员
- [ ] 1.1 `MethodSignature` AST 加 `IsStatic: bool` / `IsAbstract: bool`（或用 FunctionModifiers）
- [ ] 1.2 `InterfaceDecl` 容纳 static 抽象方法和属性（可能需要 `PropertySignature` AST）
- [ ] 1.3 `ParseInterfaceDecl`：接受 `static abstract T operator +(T, T);`、
      `static abstract T Zero { get; }`、`static abstract T Parse(string);`
- [ ] 1.4 验证：interface body 内不允许 instance method 与 static 混合声明冲突
- [ ] 1.5 Parser 单元测试

### 阶段 2：语义层 — Z42InterfaceType 存储静态成员
- [ ] 2.1 `Z42InterfaceType` 加 `StaticMethods: Dictionary<string, Z42FuncType>`
      / `StaticProperties: Dictionary<string, Z42Type>`
- [ ] 2.2 `CollectInterfaces`：填充静态成员
- [ ] 2.3 `ExportedInterfaceDef` / TSIG 导出 / 导入静态成员

### 阶段 3：TypeChecker — 实现者验证
- [ ] 3.1 `SymbolCollector.Classes.cs` 第四遍接口验证：
      对 class 的 `: INumber<T>` 约束，要求 class 提供所有 interface 的 static abstract 成员
      （名字 + 签名匹配 + IsStatic 一致）
- [ ] 3.2 类型参数替换：`static abstract T operator +(T, T)` 在 `INumber<int>` 实现里
      应匹配 `static int operator +(int, int)`
- [ ] 3.3 错误码 E0412（InterfaceMismatch）扩展覆盖静态成员漏缺 / 签名不匹配

### 阶段 4：stdlib 重构 — INumber 迁移到 static abstract
- [ ] 4.1 `z42.core/INumber.z42` 改写：5 个 `static abstract T operator op(T, T)` + `Zero` 属性
- [ ] 4.2 `Int.z42`：`public struct int : INumber<int>` 提供 5 个 static operator + Zero
      （删除原实例 `op_Add` 等 body 方法）
- [ ] 4.3 同步 `Long.z42` / `Float.z42` / `Double.z42`
- [ ] 4.4 `./scripts/build-stdlib.sh` 通过

### 阶段 5：泛型调用派发 — `a + b` 和 `T.Member` on T: INumber<T>
- [ ] 5.1 `TryBindOperatorCall` 扩展：Z42GenericParamType 查约束接口的
      `StaticMethods[op_*]`（不是 Methods）
- [ ] 5.2 BoundCall 生成：新 kind `InterfaceStatic` 或复用 Static 并在 class name
      位置记录"通过 T 派发" 语义
- [ ] 5.3 新 AST / Bound 节点？或者 `BoundTypeParamMember`？用于 `T.Zero` 这种
      静态属性访问（`T.`当前解析不支持）
- [ ] 5.4 IrGen 策略：编译期 monomorphize 生成 site-specific 调用？或 VM 运行时
      用接收者的值类型 + 接口契约查找？
      **这是本迭代难点**，需要根据 z42 的 code-sharing 模型决定

### 阶段 6：VM 运行时支持
- [ ] 6.1 primitive + class 的 static method dispatch 已有基础（primitive_class_name）
- [ ] 6.2 需要：泛型调用点传递 type_args 到 callee（依赖 L3-R 子集）
      **或者**：值-驱动 dispatch（`a + b` 时从 a 的运行时类型反查静态 operator）
      —— 取决于阶段 5 的策略

### 阶段 7：用户层兼容 & 运算符路径合并
- [ ] 7.1 `12a3854` 添加的"static operator + 存 StaticMethods" 机制保留
- [ ] 7.2 当前"实例 op_Add 兜底 desugar"移除或保留？
      目标：唯一机制是"接口静态抽象 + 实现者静态"，但非泛型场景下 `a + b`
      on concrete Vec2 仍走 Vec2's static op_Add（同一路径）
- [ ] 7.3 移除 `TryLookupInstanceOperator` 中的 Z42GenericParamType 实例方法查询
      （改为只查接口 StaticMethods）

### 阶段 8：测试 + Golden + 文档 + 归档
- [ ] 8.1 `TypeCheckerTests` 新增用例：
      - 基础静态抽象接口声明 ✅
      - 实现者漏缺 static abstract 成员 ✘
      - 实现者签名不匹配 ✘
      - 泛型 `a + b` 通过约束派发 ✅
      - 泛型 `T.Zero` 通过约束派发 ✅（如 Phase 5 完成）
- [ ] 8.2 Golden test `89_static_abstract_interface`（interp + jit）
- [ ] 8.3 `docs/design/generics.md` 新增大节 "静态抽象接口成员"
- [ ] 8.4 `docs/roadmap.md` 标记 ✅
- [ ] 8.5 归档

## 关键设计决策

- **与 C# 11 对齐**：`static abstract` 关键字组合；接口可声明静态成员
- **取消 INumber 实例方法形态**：`2026-04-23` 的 INumber 迭代 1 被替代；stdlib primitive struct 移除 op_Add 实例方法
- **运算符的语法不变**：`public static T operator +(T, T)` 继续工作；它**同时**满足 `INumber<T>` 的 static abstract 契约
- **泛型派发策略**（待阶段 5 决定）：
  - 方案 A: **值驱动派发** —— `a + b` on T 编译为查 a 的运行时类型的静态 operator（类似当前 primitive_class_name）。限制：第一操作数必须非 null
  - 方案 B: **类型参数传递** —— 泛型函数隐式接收 T 的 TypeDesc；静态 member 调用通过它派发。需要 L3-R 的运行时 type_args
  - 方案 C: **Monomorphization** —— 每个 `Sum<int>` 生成一份专门代码。与 z42 "代码共享" 哲学冲突
  - **倾向方案 A**：对 operator 足够，代价最小；`T.Zero` 这类类型级 API 留给方案 B（L3-R）

## 依赖与前置

- ✅ L3-G3a 约束元数据（2026-04-21）
- ✅ primitive-as-struct (2026-04-23)
- ✅ operator 重载语法（commit `12a3854`，2026-04-24）
- ⏸ L3-R（反射 / 运行时 type_args）— 如方案 B 需要，否则跳过

## 规模估算

- Parser / AST / SymbolCollector / TSIG：~300 行
- TypeChecker：~200 行（验证 + 泛型派发）
- IrGen / VM：~100-300 行（取决于方案）
- stdlib 迁移：~50 行
- 测试：~100 行

**总计：~1000 行，预计 2-3 个迭代**
