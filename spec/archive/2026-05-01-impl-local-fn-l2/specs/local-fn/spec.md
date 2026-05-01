# Spec: L2 Local Function 实现

> 用户视角行为契约由 `docs/design/closure.md` §3.4 + archived
> [add-closures spec](../../../archive/2026-05-01-add-closures/specs/closure/spec.md) R4 锁定。
> 本 spec 定义 `impl-local-fn-l2` 的实现层面可验证行为。

## ADDED Requirements

### Requirement LF-1: AST `LocalFunctionStmt`

#### Scenario: AST 包装 FunctionDecl
- **WHEN** parser 解析 `int Helper(int x) => x * 2;` 在 block 内
- **THEN** 生成 `LocalFunctionStmt(FunctionDecl { Name="Helper", Params=[Param(int,x)], ReturnType=int, Body=...}, Span)`

### Requirement LF-2: Parser 检测 `Type IDENT (` 形式

#### Scenario: 简单 local fn
- **WHEN** block 内 `int Helper(int x) => x * 2;`
- **THEN** 解析为 LocalFunctionStmt，不与 var-decl 冲突

#### Scenario: block-body local fn
- **WHEN** `int Compute(int x) { return x + 1; }` 在 block 内
- **THEN** 同上，body 是 BlockStmt

#### Scenario: 与 var-decl 消歧
- **WHEN** `int x = 5;`（非函数）
- **THEN** 解析为 VarDeclStmt（既有路径）

#### Scenario: 与 `(T)->R` 函数类型变量消歧
- **WHEN** `(int) -> int f = inc;`
- **THEN** 解析为 VarDeclStmt with FuncType 注解（既有路径）

### Requirement LF-3: TypeChecker 两阶段 BindBlock

#### Scenario: 前向引用合法
- **WHEN** 源码：
  ```z42
  int Outer() {
      int CallFwd() => Helper(10);
      int Helper(int x) => x * 2;
      return CallFwd();
  }
  ```
- **THEN** 编译通过，CallFwd 在声明处即可 resolve Helper

#### Scenario: 直接递归合法
- **WHEN** `int Fact(int n) => n <= 1 ? 1 : n * Fact(n - 1);`
- **THEN** 编译通过（自身名字在 body 内可见）

#### Scenario: 重复声明
- **WHEN** 同 block 内两次 `int Helper(int x) => ...;`
- **THEN** 编译错误：duplicate local function `Helper`

### Requirement LF-4: 可见性局限

#### Scenario: 外部不可见
- **WHEN** 在 Outer 外部尝试调用 `Helper(3)`
- **THEN** 编译错 Z0401 undefined symbol

#### Scenario: shadow 顶层同名 fn
- **WHEN** 顶层定义 `int Foo() => 0;`，Outer 内 local fn 也叫 `Foo`
- **THEN** Outer body 内 `Foo()` 调 local 版本（lifted `Outer__Foo`）；Outer 外仍调顶层 `Foo`

### Requirement LF-5: L2 无捕获检查

#### Scenario: local fn 引用外层 local 变量 → Z0301
- **WHEN** 源码：
  ```z42
  int Outer() {
      var k = 10;
      int Helper(int x) => x + k;   // 引用外层 k
      return Helper(3);
  }
  ```
- **THEN** 编译错 Z0301（与 lambda 捕获同错误码，"closure capture is L3 feature"）

#### Scenario: 引用顶层 / 静态符号合法
- **WHEN** local fn 调用 `Math.Sqrt(...)` / 顶层函数 / 静态字段
- **THEN** 编译通过（全局符号不算 capture）

#### Scenario: local fn 间互调（同 block 内）合法
- **WHEN** local fn A 内调用 local fn B（B 也在同一 block 声明）
- **THEN** 合法（local fn 名字在 block 范围内的"局部函数表"中）

### Requirement LF-6: 一层嵌套限制（L2）

#### Scenario: 双层嵌套报错
- **WHEN** local fn 内再声明 local fn
- **THEN** 编译错：L2 不支持多层嵌套，请使用顶层函数

### Requirement LF-7: IR 提升（lifting）+ 调用改写

#### Scenario: 命名 mangling
- **WHEN** Outer 内有 local fn Helper
- **THEN** Codegen 产生模块级 IrFunction `Outer__Helper`，签名与 Helper 一致

#### Scenario: call site 改写
- **WHEN** Outer 内 `Helper(3)` 调用
- **THEN** IR 生成 `Call Outer__Helper, %3`（直接静态调用，不是 CallIndirect）

#### Scenario: 嵌套 method 内 local fn 命名
- **WHEN** class `Calc` 的方法 `Compute` 内有 local fn `Inner`
- **THEN** lifted name `Demo.Calc.Compute__Inner`（保留 Owner 全限定）

### Requirement LF-8: 端到端

#### Scenario: examples/local_fn.z42 全栈运行
- **WHEN** 运行 `examples/local_fn.z42`（含直接递归 + 前向引用 + 多 local fn）
- **THEN** 编译通过 + VM 执行 + 输出符合预期

---

## MODIFIED Requirements

### TypeChecker.BindBlock

**Before:** 单阶段 —— `forEach stmt: BindStmt(stmt, scope)`，无前向引用支持
**After:** 两阶段 —— pass 1 扫描 LocalFunctionStmt 注入符号；pass 2 绑定所有 stmt

### TypeEnv.LookupFunc

**Before:** 仅查全局函数表
**After:** 优先查当前 scope chain 内的 local fn 表，未命中再查全局

---

## Pipeline Steps

- [x] **Lexer**：无改动
- [x] **Parser / AST**：新增 `LocalFunctionStmt`；StmtParser 加 lookahead
- [x] **TypeChecker**：BindBlock 两阶段；TypeEnv 分层 fn 表；L2 capture check 复用
- [x] **IR Codegen**：BoundLocalFunction → 模块级 IrFunction 提升；call site 改写
- [x] **VM Interp**：无改动（已有 `Call` 指令足够）
