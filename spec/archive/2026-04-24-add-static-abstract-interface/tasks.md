# Tasks: 静态抽象接口成员（C# 11 对齐）

> 状态：🟢 已完成（iter 1） | 创建：2026-04-24 | 更新：2026-04-24

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

### 阶段 1：Parser + AST — 接口允许 static abstract 成员 ✅
- [x] 1.1 `MethodSignature` AST 增加 `IsVirtual` + `Body`（三档编码）
- [x] 1.2 `ParseInterfaceDecl`：识别 `static abstract / static virtual / static`
      三档；`operator +` 符号 → `op_Add` 名字；tier ↔ body 强一致性校验
- [x] 1.3 拒绝 `override` 于接口 body 内；拒绝 `abstract + virtual` 组合；
      拒绝 instance body（iter 1 不支持 DIM）
- [ ] 1.4 Properties / `T Zero { get; }` 静态抽象属性 — iter 2 延后

### 阶段 2：语义层 — Z42InterfaceType 三档存储 ✅
- [x] 2.1 `Z42InterfaceType.StaticMembers: Dict<string, Z42StaticMember>`
      + `enum StaticMemberKind { Abstract, Virtual, Concrete }`
- [x] 2.2 `CollectInterfaces`：按 (IsVirtual, Body is null) 推导 Kind
- [x] 2.3 `ExportedTypeExtractor`：TSIG 导出静态成员（IsStatic + IsAbstract + IsVirtual）
- [x] 2.4 `ImportedSymbolLoader`：TSIG 还原 StaticMembers + Kind

### 阶段 3：TypeChecker — 实现者验证 ✅
- [x] 3.1 `SymbolCollector.Classes.cs` 第五遍 `VerifyStaticOverrides`：
      - `static override` 须对应接口的 abstract/virtual 成员
      - 缺 `override` 的同名 static 方法 → 错
      - override 目标不存在 → 拼写防护错
      - override Concrete 档 → sealed 错
      - 抽象档无 override → 漏缺错
- [x] 3.2 宽容规则：当 class 的所有接口都未知（同包 sibling / TSIG 无静态信息）
      跳过 override 目标检查
- [x] 3.3 TypeCheckerTests 5 个新用例覆盖正常+4 种错误路径
- [ ] 3.4 签名严格匹配（T → self-type 替换后的参数/返回类型精确校验）— 迭代 2

### 阶段 4：stdlib 重构 — INumber 迁移到 static abstract ✅
- [x] 4.1 `z42.core/INumber.z42` 改写：5 个 `static abstract T op_Add(T, T)` 等
- [x] 4.2 `Int.z42`：`static override int op_Add(int a, int b) { return a + b; }` 等
- [x] 4.3 同步 `Long.z42` / `Float.z42` / `Double.z42`
- [x] 4.4 `./scripts/build-stdlib.sh` 通过

### 阶段 5：泛型调用派发 — `a + b` on T: INumber<T> ✅
- [x] 5.1 `TryLookupInstanceOperator` 扩展：Path A 查 `iface.Methods`（保留），
      Path B 查 `iface.StaticMembers[op_*]`（Kind ∈ {Abstract, Virtual, Concrete}）
- [x] 5.2 方案：**复用 BoundCallKind.Virtual + 现有 VCall IR 指令**
      （VCall 的运行时接收者派发天然对应值驱动派发 — 见阶段 6 说明）
- [x] 5.3 `x.op_Add(y)` 方法调用形式 — TypeChecker.Calls 也添加 StaticMembers 分支
- [ ] 5.4 `T.Zero` / `T.Parse(s)` 类型级访问 — 延后到迭代 2（需 L3-R 子集）

### 阶段 6：IR + VM 运行时支持 ✅（复用 VCall，无新指令）
- [x] 6.1 **不需要新 IR 指令**：`BoundCall(Virtual)` → VCallInstr → VM 已有的 VCall 派发
      天然满足语义（接收者运行时类型 → `{primitive_class}.{method}`）
- [x] 6.2 VM 执行路径既有：`primitive_class_name(obj)` 解析原语类；
      对象走 vtable → 找不到则 fallback `{class}.{method}` 直接拼接
- [x] 6.3 VCall 传参约定 (receiver, ...extras) 正好匹配 2 参静态 op 的 (a, b) 签名

### 阶段 7：用户层兼容 & 运算符路径合并 🟡
- [x] 7.1 `12a3854` 静态 operator 机制保留；非泛型 `a + b` on Vec2 仍走 BoundCall(Static)
- [ ] 7.2 移除 `TryLookupInstanceOperator` 里的 Z42GenericParamType 实例方法查询
      — 目前保留 Path A 以支持历史实例 op_Add 形式（不影响新代码）

### 阶段 8：测试 + Golden + 文档 + 归档 ✅
- [x] 8.1 TypeCheckerTests 5 个用例（Phase 3）
- [x] 8.2 Golden test `89_static_abstract_operator`（interp + jit；
      涵盖 Add/Sub/Mul + chain `a + b + c`）
- [x] 8.3 golden test `87_generic_inumber` 注释更新为 static abstract 语义
- [ ] 8.4 `docs/design/generics.md` 新增 "静态抽象接口成员" 章节 — **本次提交完成**
- [ ] 8.5 `docs/roadmap.md` 标记 ✅ — **本次提交完成**
- [ ] 8.6 归档

## 已完成 commit 点

- `66571fb`：Phase 1 + 2 + 3 Parser / AST / SymbolCollector 三档 + TypeCheckerTests
- `3a33147`：Phase 2.3 + 2.4 TSIG 导出/导入 + 宽容验证
- 本次提交：Phase 4 + 5 + 6 + 7（stdlib 迁移 + 泛型 a+b 派发 + golden test 89）

## 关键决策：值驱动派发 = VCall

**不需要新 IR 指令** — 设计文档原计划 `StaticCallViaIface`，实际实现用 VCall 复用：
- VCall 已按接收者运行时类型派发（primitive_class_name + vtable）
- 2 参静态 op_Add 签名 `(T, T) -> T` 与 VCall 调用约定 `(obj, extras)` 兼容
- TypeChecker 只需把 Z42GenericParamType 的 `a + b` 翻译为 `BoundCall(Virtual, a, iface, op_Add, [b])`
- IrGen 按现有 Virtual 分支生成 VCallInstr
- VM interp / JIT 无需改动

这种极简设计本质上符合 z42 "代码共享 + 值驱动" 哲学。

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
