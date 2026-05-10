# Spec: parameter-modifiers

## ADDED Requirements

### Requirement: `ref` 参数（双向引用，调用方可见修改）

#### Scenario: ref 修改原语 local
- **WHEN** 声明 `void Increment(ref int x) { x += 1 }`；调用方 `var c = 0; Increment(ref c)`
- **THEN** 调用后 `c == 1`

#### Scenario: ref 修改数组元素 / 对象字段
- **WHEN** 调用方 `Increment(ref arr[i])` 或 `Increment(ref obj.field)`（Q5=允许）
- **THEN** 数组槽 / 字段值在调用后已修改

#### Scenario: ref 重置引用类型变量（reseat）
- **WHEN** 声明 `void Reseat(ref string s) { s = "new" }`；调用方 `var name = "old"; Reseat(ref name)`
- **THEN** 调用后 `name == "new"`（caller 的 slot 被改写指向新 string）

#### Scenario: ref 实参必须 lvalue
- **WHEN** 调用 `Increment(ref f())` 或 `Increment(ref 42)`
- **THEN** 编译错误 E0xxx：`ref` 实参必须是 lvalue（变量 / 字段 / 数组元素）

#### Scenario: callsite 必须显式 `ref`
- **WHEN** 函数签名带 `ref`，调用方写 `Increment(c)` 省略 `ref`
- **THEN** 编译错误 E0xxx：callsite 缺修饰符

#### Scenario: ref 类型严格匹配
- **WHEN** 函数签名 `void Foo(ref int x)`；调用方 `long n; Foo(ref n)`
- **THEN** 编译错误 E0xxx：`ref` 跨类型边界禁止隐式转换（`long` 不接 `ref int`）

---

### Requirement: `out` 参数（单向输出 + DefiniteAssignment）

#### Scenario: `out var x` callsite 内联声明
- **WHEN** 声明 `bool TryParse(string s, out int v) { v = 42; return true }`；调用方 `if (TryParse("x", out var n)) print(n)`
- **THEN** 编译通过；`n` 类型推断为 int，作用域延伸至 `if` 包含块（Q4=是）；运行时 `n == 42`

#### Scenario: callee 必须在 normal-return 路径赋值
- **WHEN** `void Foo(out int x) { /* 无赋值就 return */ }`
- **THEN** 编译错误 E0xxx：`out` 参数 `x` 在 normal-return 路径未赋值

#### Scenario: caller 调用前 var 可未初始化
- **WHEN** `int v; TryParse("x", out v); print(v)`
- **THEN** 编译通过；`print(v)` 不触发"v 未初始化"诊断（caller post-call DA 视为已赋）

#### Scenario: throw 路径不算赋值（Q6）
- **WHEN** `void Foo(out int x) { if (cond) x = 1; else throw new Err() }`
- **THEN** 编译通过（throw 路径不要求赋值，仅 normal-return 路径要求）
- **WHEN'** `void Foo(out int x) { if (cond) throw new Err(); /* 落到此处仍未赋值 */ }`
- **THEN'** 编译错误（`cond=false` 后 fall-through 路径未赋值）

---

### Requirement: `in` 参数（只读引用，零拷贝）

#### Scenario: in 参数可读
- **WHEN** `double Norm(in BigVec v) { return sqrt(v.x*v.x + v.y*v.y) }`
- **THEN** 函数体内 `v.x` / `v.y` 可读取

#### Scenario: in 参数禁止写
- **WHEN** 函数体 `void Foo(in int x) { x = 5 }` 或 `void Foo(in BigVec v) { v.x = 5 }`
- **THEN** 编译错误 E0xxx：`in` 参数不可写

#### Scenario: callsite 必须显式 `in`（修正 C#）
- **WHEN** 声明 `double Norm(in BigVec v)`；调用方写 `Norm(myVec)` 省略 `in`
- **THEN** 编译错误 E0xxx：callsite 缺修饰符（z42 不允许 C# 风格的省略）

---

### Requirement: 4 条交互限制

#### Scenario: lambda 不能捕获 ref/out/in 参数
- **WHEN** `void Foo(ref int x) { var f = () => x + 1 }`
- **THEN** 编译错误 E0xxx：lambda 不可捕获 `ref` / `out` / `in` 参数

#### Scenario: async / iterator 占位拒绝
- **WHEN** 一个 `async` 或 iterator 方法的签名包含 `ref` / `out` / `in` 参数
- **THEN** 编译错误 E0xxx：当前阶段 async / iterator 不支持此修饰符（占位诊断；async / iterator 引入时再开 spec 决定如何放开）

---

### Requirement: overload resolution（Q7）

#### Scenario: 修饰符参与 overload
- **WHEN** 同时声明 `void Foo(int x)` 与 `void Foo(ref int x)`
- **THEN** 视为两个不同重载；`Foo(c)` 选第一个，`Foo(ref c)` 选第二个；不报歧义

#### Scenario: 修饰符差异不可省 overload 选择
- **WHEN** 仅声明 `void Foo(ref int x)`；调用 `Foo(c)` 不带 `ref`
- **THEN** 编译错误（无匹配重载，缺 `ref`）

---

## IR Mapping（编译期视角）

| z42 形态 | 本 spec 处理 | 运行时映射 |
|---|---|---|
| `ref T x` 参数（签名）| `Param.Modifier = Ref` 入 AST；`Z42FuncType.ParamModifiers[i] = Ref` 入语义模型 | follow-up spec：`IrFunction.ParamModifiers` + `Value::Ref` |
| `out T x` 参数（签名）| 同上，Modifier = Out | 同上 |
| `in T x` 参数（签名）| 同上，Modifier = In | 同上 |
| `f(ref expr)` callsite | AST `ModifiedArg(Inner, Ref)`；TypeChecker 验证后 `BoundModifiedArg` 携带 modifier；当前 codegen 落到普通 `Call`（运行时按 by-value）| follow-up spec：codegen 检测 BoundModifiedArg → emit `LoadLocalAddr` / `LoadElemAddr` / `LoadFieldAddr` 产生 Ref 值 |
| callee 端读 ref/out/in 参数 | 编译期不变（透明）；当前运行时按普通参数读 | follow-up spec：VM 层透明 deref（frame.get/set 检测 Value::Ref 自动跟随）|

> 运行时 opcode 形态见 `design.md` Q1/Q2 决议（已写好，留给 follow-up spec `impl-ref-out-in-runtime` 直接实施）。

---

## Pipeline Steps

受影响的 pipeline 阶段（按顺序）：

- [x] **Lexer**：+`Ref` / +`Out` token kinds（`In` 复用 foreach 关键字）
- [x] **Parser / AST**：`Param.Modifier` 字段；`ModifiedArg` AST 节点；`OutVarDecl` 节点；参数列表 + callsite 实参解析
- [x] **TypeChecker**：修饰符一致性 / lvalue / 严格类型匹配 / DA 扩展（callee normal-return + caller post-call；throw 路径除外）/ 4 交互规则 / overload 区分（modifier-tagged key）/ `in` 写保护 / lambda 捕获禁止
- [ ] **IR Codegen**：ref-aware Call emission —— **延后到 follow-up spec `impl-ref-out-in-runtime`**
- [ ] **VM interp**：Value::Ref + RefKind + 透明 deref —— **延后到 follow-up spec `impl-ref-out-in-runtime`**
- [ ] JIT / AOT：本 spec 不涉及（CLAUDE.md "interp 全绿前不碰"）

## 运行时不一致警告（过渡状态）

本 spec 落地后到 `impl-ref-out-in-runtime` 落地之前的过渡期：用户写 `Increment(ref c)` 编译期通过所有验证（修饰符一致 + lvalue + DA 等），运行时 callee 修改不传回 caller（codegen 走普通 by-value Call）。`docs/design/parameter-modifiers.md` 的 "Runtime Implementation" 段会明确说明此过渡状态，并指引使用 tuple 多返回（已支持）作为临时替代。
