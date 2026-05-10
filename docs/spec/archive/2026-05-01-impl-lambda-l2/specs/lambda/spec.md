# Spec: L2 无捕获 Lambda 实现

> 本 spec 定义 `impl-lambda-l2` 的**实现层面**可验证行为。
> 用户视角语义契约由 [closure.md](../../../../docs/design/closure.md) +
> archived [add-closures spec](../../../archive/2026-05-01-add-closures/specs/closure/spec.md) R1–R4 + R9 + R14 锁定。

## ADDED Requirements

### Requirement IR-L1: Lambda AST 节点

#### Scenario: AST 携带参数类型信息
- **WHEN** parser 解析 `(int x, string y) => x + y.Length`
- **THEN** 生成 `LambdaExpr { Params = [LambdaParam("x", IntType, span), LambdaParam("y", StringType, span)], Body = BinaryExpr(...), Span }`

#### Scenario: AST 隐式参数类型为 null
- **WHEN** parser 解析 `(x, y) => x + y`
- **THEN** `LambdaParam.Type` 为 `null`（推断由 TypeChecker 完成）

#### Scenario: 单参省括号
- **WHEN** parser 解析 `x => x + 1`
- **THEN** 生成单 LambdaParam 的 LambdaExpr，无 ParenExpr 嵌套

#### Scenario: Block body
- **WHEN** parser 解析 `x => { var y = x; return y; }`
- **THEN** `LambdaExpr.Body` 是 `BlockStmt`（区别于普通 Expr）

---

### Requirement IR-L2: 函数类型 AST 节点

#### Scenario: FuncType TypeExpr
- **WHEN** parser 解析类型 `(int, string) -> bool`
- **THEN** 生成 `FuncType { ParamTypes = [IntType, StringType], ReturnType = BoolType, Span }`

#### Scenario: 嵌套函数类型
- **WHEN** parser 解析类型 `((int) -> int) -> int`
- **THEN** 嵌套合法，`ReturnType` 为 `IntType`，`ParamTypes[0]` 为另一个 `FuncType`

#### Scenario: 作为泛型实参
- **WHEN** parser 解析类型 `List<(int) -> bool>`
- **THEN** `GenericType("List", [FuncType(...)])`

#### Scenario: void 返回类型
- **WHEN** parser 解析 `(int) -> void`
- **THEN** `FuncType.ReturnType` 为 `VoidType`

---

### Requirement IR-L3: Parser 消歧 `(...)` 括号 vs lambda

> z42 现有 `(expr)` 是括号表达式；新增 lambda 后 `(x, y) => ...` 也以 `(` 开头。

#### Scenario: 括号 + `=>` → lambda
- **WHEN** parser 看到 `(x, y) => ...`
- **THEN** 整体解析为 LambdaExpr

#### Scenario: 括号 + 非 `=>` → ParenExpr 或 TupleExpr
- **WHEN** parser 看到 `(x + y) * 2`
- **THEN** 解析为 `ParenExpr(BinaryExpr(...))`，不当作 lambda

#### Scenario: 显式类型 + `=>` → typed lambda
- **WHEN** parser 看到 `(int x) => x * 2`
- **THEN** 解析为 LambdaExpr 含类型参数

#### Scenario: 函数类型注解位置
- **WHEN** 类型位置（如变量类型）出现 `(int) -> int`
- **THEN** 由 TypeParser 处理为 FuncType，不与 ExprParser 冲突

---

### Requirement IR-L4: TypeChecker lambda 类型推断

#### Scenario: 上下文驱动推断
- **WHEN** 上下文期望 `(int) -> int`，lambda 字面量为 `x => x + 1`
- **THEN** `x` 推断为 `int`；body 类型为 `int`；整体合成 `Z42FuncType([int], int)`

#### Scenario: 显式参数类型驱动推断
- **WHEN** lambda `(int x) => x.ToString()`
- **THEN** `x: int`；body 类型 `string`；整体 `Z42FuncType([int], string)`

#### Scenario: 无上下文 + 隐式参数类型 → 错误
- **WHEN** `var f = x => x + 1;`（var 无上下文，参数类型未标）
- **THEN** 编译错误：无法推断 lambda 参数类型

#### Scenario: 显式类型即使在 var 也可推断
- **WHEN** `var f = (int x) => x + 1;`
- **THEN** 推断成功，`f` 类型为 `(int) -> int`

#### Scenario: 块体 lambda 返回类型推断
- **WHEN** `(int x) => { return x * 2; }`
- **THEN** 通过 block 内 `return` 表达式推断返回类型

---

### Requirement IR-L5: L2 无捕获检查

#### Scenario: 无捕获 lambda 通过
- **WHEN** lambda body 仅引用自身参数 + 全局静态成员（如 `Math.Sqrt`）
- **THEN** 编译通过

#### Scenario: 引用外层 local 变量 → 错误
- **WHEN** 源码：
  ```z42
  void Main() {
      var threshold = 10;
      var f = (int x) => x > threshold;   // 引用外层 local
  }
  ```
- **THEN** 编译错误：`Z0820: 闭包捕获是 L3 特性，当前 L2 阶段不支持。请改用静态成员或参数传递。`

#### Scenario: 引用外层参数 → 同样错误
- **WHEN** 在某函数内 lambda 引用该函数的参数
- **THEN** 同 Z0820

#### Scenario: 引用全局 / 静态成员通过
- **WHEN** lambda body `x => Math.Sqrt(x)` / `x => Console.WriteLine(x)`
- **THEN** 编译通过（全局符号不算捕获）

#### Scenario: 引用 lambda 自身参数嵌套 lambda
- **WHEN** `(int x) => (int y) => x + y`
- **THEN** 内层 lambda 引用外层 lambda 的 `x` —— 这**是**捕获，L2 报 Z0820

#### Scenario: 引用类的字段 / 方法（this 隐式）
- **WHEN** 在类方法内 lambda body 引用 `this.field` 或 `this.Method()`
- **THEN** 编译错误 Z0820（隐式捕获 this 是 L3）

---

### Requirement IR-L6: Local function 的 L2 限制 — **本变更内推迟**

实施阶段 7 期间发现，z42 现有 `StmtParser` / `SymbolCollector` 不识别"`Type IDENT (...)`"
为语句级函数声明。完整支持需要新增 `LocalFunctionStmt` AST + `SymbolCollector`
跨函数体扫描 + 命名 mangling，与"无捕获 lambda"目标偏离。

User 在阶段 7 实施前裁决：**Scope 收紧，IR-L6 全部 Scenario 推迟到 follow-up
变更 `impl-local-fn-l2`**。本变更不再实现 / 测试 local function。

> 设计文档（`docs/design/closure.md` §3.4 + `docs/roadmap.md` L3-C 表）保留 local
> function 章节作为长期规范；只是把实施时间线挪后。

---

### Requirement IR-L7: IR 生成 — 无捕获 lambda → 函数引用

#### Scenario: Lambda lifting
- **WHEN** TypeChecker 通过的 lambda 字面量 `(int x) => x + 1`
- **THEN** Codegen 将 lambda body 提升为模块级独立函数（命名 `<containing>$lambda$<index>`），原 lambda 字面量位置生成 `LoadFn <fn_index>` 指令

#### Scenario: 多个 lambda 字面量
- **WHEN** 一个函数内有 N 个 lambda 字面量
- **THEN** 生成 N 个独立函数，分别命名 `Owner$lambda$0` / `Owner$lambda$1` / ...

#### Scenario: Local function lifting
- **WHEN** 嵌套 `int Helper(int x) => ...`
- **THEN** Codegen 提升为模块级独立函数，命名 `Owner$Helper`（保留原名）；调用点 `Helper(3)` → 直接 `Call Owner$Helper`

---

### Requirement IR-L8: VM 解释 LoadFn

#### Scenario: LoadFn 推入函数引用值
- **WHEN** VM 解释 `LoadFn <fn_index>` 指令
- **THEN** 操作数栈顶推入一个函数引用值（指向目标函数的 entry）

#### Scenario: 函数引用调用
- **WHEN** 操作数栈顶有函数引用值，执行 `Call (operand_count)` 类指令
- **THEN** 通过引用调用对应函数，参数行为与直接 Call 一致

#### Scenario: 函数引用值可作为参数
- **WHEN** 函数 `void Apply((int) -> int f, int x)` 调用 `Apply(y => y + 1, 5)`
- **THEN** lambda 提升为 `Caller$lambda$0`，VM 通过 LoadFn 推入引用，Apply 内通过函数引用调用 → 返回 6

---

### Requirement IR-L9: 端到端示例

#### Scenario: examples/lambda.z42 全栈运行
- **WHEN** 运行 `examples/lambda.z42`（含 lambda 字面量、`(T)->R` 类型注解、表达式短写、local function）
- **THEN** 编译通过 + VM 执行 + 输出符合预期（具体输出待 examples 设计时定）

---

## MODIFIED Requirements

### LambdaExpr AST 节点

**Before:** `sealed record LambdaExpr(List<string> Params, Expr Body, Span Span)`
**After:** `sealed record LambdaExpr(List<LambdaParam> Params, LambdaBody Body, Span Span)`
- 新增 `sealed record LambdaParam(string Name, TypeExpr? Type, Span Span)`
- `LambdaBody` 是 `Expr` 或 `BlockStmt` 的 union（具体表达方式留 design 决定）

### TypeChecker.Exprs.cs `BindExpr` switch

**Before:** 无 `case LambdaExpr`，遇到 lambda 会落到 default 报"unsupported expr"
**After:** 新增 `case LambdaExpr` 分支，调用 `BindLambda(...)` 完成类型推断

---

## IR Mapping

| 源码模式 | IR 指令 |
|---------|---------|
| `x => expr`（无捕获）| Lambda body lifted → 独立函数 `Owner$lambda$N`；字面量位置生成 `LoadFn <fn_index>` |
| `int Helper(int x) => ...`（无捕获 local fn）| Lifted → `Owner$Helper`；调用点直接 `Call Owner$Helper` |
| `(int) -> int` 类型注解 | TypeChecker 内表示为 `Z42FuncType([int], int)`；IR 中作为 ABI 参数 / 字段类型 |

**LoadFn 指令格式（待 design 实证 Opcodes.cs 确认编号）**：
```
LoadFn <function_index: u32>     # 推入函数引用到操作数栈
```

如果 `Opcodes.cs` 已有等价指令（`LoadFunc` / `LoadFuncRef`），优先复用，避免新 opcode。

---

## Pipeline Steps

- [x] **Lexer**：`=>` token 已存在（FatArrow），无需改动
- [x] **Parser / AST**：新增 `FuncType` / `LambdaParam`；改造 `LambdaExpr`；TypeParser 加 func_type 分支；ExprParser 加 lambda 消歧
- [x] **TypeChecker**：`case LambdaExpr` 推断；无捕获检查 pass（Z0820 错误码）；`Func<T,R>` 与 `(T)->R` 等价桥接
- [x] **IR Codegen**：lambda lifting → `LoadFn`；local function lifting
- [x] **VM Interp**：`LoadFn` 解释执行；函数引用调用路径

---

## 错误码

新增（如 error-codes.md 没有）：
- **Z0820** — `闭包捕获是 L3 特性，当前 L2 阶段不支持。引用外层 local "{name}" 不允许。请改用参数传递或全局/静态成员。`

如已有相近错误码（如 Z0809 family），复用之；spec/design 阶段确认。
