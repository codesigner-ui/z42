# Tasks: Operator 重载（C# 风 `operator` 关键字 + 静态方法）

> 状态：🟢 已完成 | 完成：2026-04-24

**变更说明：** 按 C# 标准语法新增 operator 重载。用户用 `public static T operator +(T a, T b) { ... }`
声明；编译器 desugar `a + b` 为 `op_Add(a, b)` 静态调用。支持异构算子
（`Vec2 * double` / `double * Vec2`），通过签名重载解析。

**运算符 → 方法映射**：
- `+` → `op_Add`
- `-` → `op_Subtract`
- `*` → `op_Multiply`
- `/` → `op_Divide`
- `%` → `op_Modulo`

## 任务

### 阶段 1：Lexer + AST ✅
- [x] 1.1 新 `TokenKind.Operator` + `"operator"` keyword 注册（Phase1）
- [x] 1.2 `IsPhase2ReservedKeyword` 不包含 Operator
- [x] 1.3 无新 AST 节点（通过方法名 mangling 复用 FunctionDecl）

### 阶段 2：Parser operator 方法声明 ✅
- [x] 2.1 `ParseFunctionDecl`：方法名位置识别 `operator <op>` 序列
- [x] 2.2 Mangle `operator +` → `op_Add`（helper `ParseOperatorSymbolAsMethodName`）
- [x] 2.3 `operator` 方法走 static 通路（由用户声明的 `static` modifier 保证）

### 阶段 3：SymbolCollector 存储 ✅
- [x] 3.1 复用现有 ClassDecl.Methods 收集；static operator 方法进 `StaticMethods` dict
- [x] 3.2 方法名按 mangled "op_Add" 键存储，与 INumber 实例方法共存不冲突（不同 dict）

### 阶段 4：TypeChecker CheckBinary desugar ✅
- [x] 4.1 `TryBindOperatorCall`：primitive 双方早退；否则查静态 op_Add；再查
      实例 op_Add（含 Z42ClassType / Z42InstantiatedType / Z42GenericParamType）
- [x] 4.2 `TryLookupStaticOperator`：签名匹配（`IsAssignableTo` 双参数），
      返回 `ResolveStubType(sig.Ret)`（处理第一遍 stub 类型）
- [x] 4.3 `TryLookupInstanceOperator`：generic param 查
      `gp.InterfaceConstraints` + `LookupEffectiveConstraints`；
      `SubstituteGenericReturnType` 把接口方法返回的 T 替换为 caller gp
- [x] 4.4 BoundCall 生成：静态 `BoundCallKind.Static`，实例 `BoundCallKind.Virtual`

### 阶段 5：测试 ✅
- [x] 5.1 `TypeCheckerTests` 新增 4 个 OpOverload 用例（
      基础 user struct ✅ / 异构 `Vec2 * int` ✅ / 类型不匹配 ✘ /
      泛型 instance 路径注记延后 demo）
- [x] 5.2 Golden test `88_operator_overload` 端到端（interp + jit 双绿）

### 阶段 6：文档 + 归档 ✅
- [x] 6.1 `docs/design/generics.md` 新增 "Operator 重载" 小节
      （语法 / 运算符映射 / desugar 优先级 / 后续迭代）
- [x] 6.2 `docs/roadmap.md` L3-G2.5 新增 Operator 重载 ✅ 行
- [x] 6.3 GREEN：566 编译器 + 168 VM (interp+jit) 全绿

## 关键设计决策

- **静态方法（C# 标准）**：operator 必须 `public static`；与 INumber 的
  实例方法独立。不同存储（`StaticMethods` vs `Methods`），不冲突
- **方法名对齐 INumber**：mangled 为 `op_Add` 而非 C# IL 的 `op_Addition`，
  方便两套机制共存时用户不必重复声明
- **Desugar 优先级**：primitive 双方早退保护 AddInstr 快路径；否则查静态 →
  实例。避免 `1 + 2` 走方法派发
- **签名匹配**：IsAssignableTo 双向（含 primitive widening），支持
  `Vec2 * int` / `Vec2 + Vec2` 同 class 内多个 op_Multiply 重载
- **生成的 IR 结构**：BoundCall 已支持 static / instance，无需新 IR 指令。
  IrGen / VM 零改动

## 已知限制（后续迭代）

- 比较 `<` / `<=` / `>` / `>=`（走 IComparable，需额外 desugar 规则）
- 相等 `==` / `!=`（走 IEquatable）
- 一元 `-x` / `!x` / `~x`
- 复合赋值 `+=` 等（纯语法糖，独立看）
- `(double s) * Vec2` 左侧为 primitive：用户需声明 `operator *(double s, Vec2 v)`
  在 Vec2 上（C# 惯例），OR 在 double 上加方法（需要 primitive-as-struct 扩展）
- 泛型路径跨 zpkg 端到端 demo（与 INumber 迭代 1 的独立 follow-up）
