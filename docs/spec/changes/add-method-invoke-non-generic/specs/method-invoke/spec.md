# Spec: 反射调用原语（Method.Invoke / Type.GetType / Activator.CreateInstance）

## ADDED Requirements

### Requirement: Type.GetType(fqn)

`Type.GetType(string fqn)` 返回该完全限定名对应的 `Type`；未知名返回 `null`。

#### Scenario: 已知类型
- **WHEN** `Type t = Type.GetType("Demo.Point");`（Demo.Point 存在）
- **THEN** `t != null` 且 `t.Name == "Point"`（与 `typeof(Demo.Point)` 等价）

#### Scenario: 未知类型
- **WHEN** `Type t = Type.GetType("No.Such.Type");`
- **THEN** `t == null`

### Requirement: Activator.CreateInstance(Type)（无参）

`Activator.CreateInstance(Type t)` 用无参构造创建实例（复用对象分配 + 无参 ctor）。

#### Scenario: 无参构造
- **WHEN** 类 `Counter { int N; }` 有隐式/无参 ctor，`object o = Activator.CreateInstance(typeof(Counter));`
- **THEN** `o` 是 `Counter` 实例（字段默认值），`o.GetType() == typeof(Counter)`

### Requirement: MethodInfo.Invoke(obj, args)

`m.Invoke(object obj, object[] args)` 反射执行方法：实例方法 `obj` 为接收者、静态方法 `obj` 传
`null`；`args` 按序映射形参；返回方法返回值（`void` → `null`）。

#### Scenario: 静态方法 Invoke
- **WHEN** `static int Add(int a, int b)`；`MethodInfo m = ...Add；object r = m.Invoke(null, new object[]{2, 3});`
- **THEN** `(int)r == 5`

#### Scenario: 实例方法 Invoke
- **WHEN** `class C { int Twice(int x){return x*2;} }`；`object c = Activator.CreateInstance(typeof(C));`
  `MethodInfo m = ...Twice；object r = m.Invoke(c, new object[]{21});`
- **THEN** `(int)r == 42`

#### Scenario: void 方法 Invoke 返回 null
- **WHEN** `void Log()`（静态）；`object r = m.Invoke(null, new object[0]);`
- **THEN** `r == null`

#### Scenario: 被调方法抛异常 → 传播且可捕获
- **WHEN** 被 Invoke 的方法体内 `throw new InvalidOperationException("boom");`
- **THEN** 异常经 Invoke **原样传播**，调用方 `try { m.Invoke(...) } catch (InvalidOperationException e)` 可捕获
- **注**：区别于装箱拆箱失败的不可捕获 `Convert` 错误 —— 被调方法的 throw 是正常 z42 异常（`ExecOutcome::Thrown`）

#### Scenario: 实参个数不符
- **WHEN** `Add(int,int)` 调 `m.Invoke(null, new object[]{1})`（少一个）
- **THEN** 抛带信息异常（arity 不符），不静默错误执行

## IR Mapping

无新 IR 指令。三者均为 `[Native(...)]` extern → 编译为现有 `Builtin` 指令；运行期新增 3 个 builtin：

| z42 API | builtin | 运行期复用 |
|---------|---------|-----------|
| `MethodInfo.Invoke` | `__method_invoke` | MethodInfo.`__qualified` → `try_lookup_function` → `exec_function` |
| `Type.GetType` | `__type_get_type` | `make_type_from_name(ctx, fqn)` |
| `Activator.CreateInstance` | `__activator_create` | `ObjNew`（分配 + 无参 ctor） |

无 zbc/zpkg 格式 bump。

## Pipeline Steps

- [ ] Lexer / Parser / AST：无（复用 `[Native]` extern + 普通方法调用语法）
- [ ] TypeChecker：无（extern 方法签名既有机制）
- [ ] IR Codegen：无（extern → 现有 Builtin 指令）
- [ ] VM interp：新增 `__method_invoke` / `__type_get_type` / `__activator_create` 三 builtin + 注册

## 边界

- 仅**非泛型**方法；MethodInfo 不记 TypeArgs，泛型 Invoke 0.4.x G。
- `CreateInstance` 仅**无参**构造；有参（重载决议）延后。
- ref/out 参数 Invoke 延后。
