# Spec: z42c codegen — Bound → IR lowering

## ADDED Requirements

### Requirement: Bound 树降级为 IR 内存模型

z42c.semantics 能把已类型检查的 Bound 树（SemanticModel）降级为寄存器式 SSA `IrModule`
（IrFunction → IrBlock → IrInstr），并以 .zasm-like 文本 dump 供断言。

#### Scenario: 返回 int 字面量的函数（CG-1A）
- **WHEN** 源 `int F() { return 5; }`
- **THEN** 产 IrFunction `@F`，entry 块含 `%0 = const.i64 5` + 终结符 `ret %0`（reg 从 paramOffset 起算，静态 free func paramOffset=0）

#### Scenario: 二元算术（CG-1A）
- **WHEN** 源 `int Add(int a, int b) { return a + b; }`（实例方法，reg0=this，a=%1 b=%2）
- **THEN** entry 块含 `%3 = add i64 %1, %2` + `ret %3`

#### Scenario: 局部变量声明 + 赋值（CG-1A）
- **WHEN** 源 `int F() { var x = 1; x = x + 2; return x; }`
- **THEN** const.i64 1 → 绑 x 寄存器；后续 `x + 2` 算到新寄存器后 copy 回 x 寄存器；`ret` x 寄存器

#### Scenario: double / bool / string 字面量（CG-1A）
- **WHEN** 源含 `1.5` / `true` / `"hi"`
- **THEN** 分别 emit `const.f64 1.5` / `const.bool true` / `const.str` 引用 StringPool 索引（字符串入池去重）

#### Scenario: 隐式 return（CG-1A）
- **WHEN** 源 `void F() { }`（void 方法无显式 return）
- **THEN** entry 块以 `ret`（无值）终结

#### Scenario: if / while → 多基本块（CG-1B）
- **WHEN** 源含 `if (c) {...} else {...}` 或 `while (c) {...}`
- **THEN** 当前块以 `br.cond %c, <then>, <else/end>` 终结，then/else/end 各为独立带 label 的 IrBlock；then/else 末尾 `br <end>` 回合流块

#### Scenario: 错误源不产 IR 崩溃（健壮性）
- **WHEN** 源有类型错误（如 undefined ident）
- **THEN** 类型检查阶段已报诊断；codegen 对 BoundError 节点走兜底（不 ICE 崩溃整个 dump）

## IR Mapping

| Bound 节点 | IR 指令 |
|-----------|---------|
| BoundLitInt/Float/Bool/Str/Null | ConstI64 / ConstF64 / ConstBool / ConstStr(pool idx) / ConstNull |
| BoundIdent（local/param） | 直接寄存器引用（无指令）|
| BoundIdent（实例字段） | FieldGetInstr(reg0, name)（CG-1C）|
| BoundBinary（+−×÷%） | Add/Sub/Mul/Div/Rem |
| BoundBinary（比较/逻辑/位） | Eq..Ge / And·Or·Not / BitAnd..（CG-1E）|
| BoundAssign | CopyInstr / FieldSetInstr |
| BoundVarDeclStmt | EmitExpr(init) + 绑 local 寄存器 |
| BoundReturn | RetTerm 终结符 |
| BoundIf / BoundWhile | BrCondTerm + BrTerm + 多 IrBlock（CG-1B）|
| BoundBreak / BoundContinue | BrTerm（_loopLabels 目标，CG-1B）|
| BoundCall | CallInstr / VCallInstr（CG-1C）|
| BoundNew / BoundCast / BoundIsExpr / BoundIndex | ObjNew / Convert / IsInstance / ArrayGet（CG-1D）|

## Pipeline Steps

受影响的 pipeline 阶段（codegen 是 typecheck 之后、emit 之前）：
- [ ] Lexer —（无）
- [ ] Parser / AST —（无）
- [ ] TypeChecker —（无，消费其产物 SemanticModel）
- [x] **IR Codegen** — 本 change 主体（FunctionEmitter / IrGen → IrModule）
- [ ] VM interp —（无；IR 仅内存模型 + 文本 dump，未到执行/zbc）

## 测试覆盖

- z42c.semantics/tests/codegen/codegen_tests.z42：每 scenario 一个 [Test]，IR 文本（多行字符串）断言。
- CG-1A 起；后续增量按上表逐条加 [Test]。
