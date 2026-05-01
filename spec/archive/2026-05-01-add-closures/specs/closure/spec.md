# Spec: Closure (闭包)

## ADDED Requirements

### Requirement R1: Lambda 字面量语法

z42 提供 C# 风格 lambda 字面量，使用 `=>` 作为参数与体的分隔符。

#### Scenario: 单参表达式 lambda
- **WHEN** 源码出现 `var f = x => x + 1;`
- **THEN** 解析为 lambda 字面量；参数 `x` 类型由上下文 / 注解推断；返回类型由 body 表达式推断

#### Scenario: 多参表达式 lambda
- **WHEN** 源码出现 `var g = (x, y) => x * y;`
- **THEN** 参数列表用 `(...)` 包围，逗号分隔

#### Scenario: 无参 lambda
- **WHEN** 源码出现 `var h = () => 42;`
- **THEN** 空参列表 `()` 必须显式

#### Scenario: 语句体 lambda
- **WHEN** 源码出现 `var f = x => { var y = x * 2; return y + 1; };`
- **THEN** body 为 block 时必须显式 `return`；表达式 body 隐式返回

#### Scenario: 显式参数类型
- **WHEN** 源码出现 `var f = (x: int, y: int) => x + y;`
- **THEN** 参数可显式标注类型，覆盖推断

---

### Requirement R2: 函数类型语法 `(T) -> R`

闭包 / 函数引用的类型用箭头形式表达，不使用 `Func<T,R>` / `Action<T>`。

#### Scenario: 单参函数类型
- **WHEN** 类型注解为 `(int) -> int`
- **THEN** 表示接受 int 返回 int 的可调用值（含 lambda、local function、闭包、函数引用）

#### Scenario: 多参函数类型
- **WHEN** 类型注解为 `(int, string) -> bool`
- **THEN** 表示接受 (int, string) 返回 bool

#### Scenario: 无返回值函数类型
- **WHEN** 类型注解为 `(int) -> void`
- **THEN** `void` 表示无返回值（与 z42 现有 void 类型一致）

#### Scenario: 高阶函数类型
- **WHEN** 类型注解为 `((int) -> int) -> int`
- **THEN** 嵌套合法

#### Scenario: 函数类型作为泛型实参
- **WHEN** 类型注解为 `List<(int) -> bool>`
- **THEN** 合法

---

### Requirement R3: 函数表达式短写（C# 7+ expression-bodied）

C# 风格函数声明支持表达式 body 短写 `=>`。

#### Scenario: 顶层函数短写
- **WHEN** 源码 `int Square(int x) => x * x;`
- **THEN** 等价于 `int Square(int x) { return x * x; }`

#### Scenario: 短写需分号结尾
- **WHEN** 源码 `int Square(int x) => x * x`（缺分号）
- **THEN** 编译错误：表达式短写必须以 `;` 结尾

#### Scenario: 短写不能用 block
- **WHEN** 源码 `int F(int x) => { return x; };`
- **THEN** 编译错误：`=>` 后必须是表达式；要语句体请用 `{ ... }` 形式

---

### Requirement R4: Local function（嵌套函数声明，C# 7+）

C# 风格函数声明可出现在另一函数 body 内，作为局部辅助函数。

#### Scenario: 局部函数基本定义
- **WHEN** 源码：
  ```z42
  int Outer() {
      int Helper(int x) => x * 2;
      return Helper(3);
  }
  ```
- **THEN** `Helper` 是 Outer 内可见的局部函数，返回 6

#### Scenario: 直接递归
- **WHEN** 局部函数 body 内引用自身名字
- **THEN** 合法（名字在自身 body 内可见）

#### Scenario: 可见性局限
- **WHEN** 局部函数从所属函数外部引用
- **THEN** 编译错误：name not found

#### Scenario: L2 阶段不允许捕获外层
- **WHEN** L2 阶段，局部函数 body 引用外层 local var
- **THEN** 编译错误：捕获是 L3 特性

#### Scenario: L3 阶段允许捕获
- **WHEN** L3 阶段，局部函数 body 引用外层 local var
- **THEN** 局部函数升级为闭包，按 R5/R6 规则处理

---

### Requirement R5: 值类型捕获（快照）

闭包捕获值类型变量时按快照（创建闭包时拷贝），后续外部修改不影响闭包内值。

#### Scenario: int 捕获快照
- **WHEN** 源码：
  ```z42
  var x = 5;
  var f = () => Console.WriteLine(x);
  x = 10;
  f();
  ```
- **THEN** 输出 `5`，不是 `10`

#### Scenario: struct 捕获快照
- **WHEN** 闭包捕获 struct 类型局部
- **THEN** 快照拷贝整个 struct，后续外部修改不影响闭包

#### Scenario: 大型值类型 + expect_no_alloc
- **WHEN** 闭包捕获 >32 字节值类型，所在函数标注 `[expect_no_alloc]`
- **THEN** warning：大型值类型快照可能影响性能

---

### Requirement R6: 引用类型捕获（按身份共享）

闭包捕获引用类型变量时按身份共享，闭包内外见同一对象。

#### Scenario: 引用类型对象共享
- **WHEN** 源码：
  ```z42
  class Counter { public int n = 0; }
  var c = new Counter();
  var inc = () => c.n = c.n + 1;
  inc(); inc();
  Console.WriteLine(c.n);
  ```
- **THEN** 输出 `2`

#### Scenario: 外部重新指向不影响已创建闭包
- **WHEN** 源码：
  ```z42
  var c = new Counter();
  var inc = () => c.n = c.n + 1;
  c = new Counter();
  inc();
  ```
- **THEN** 闭包仍操作原对象（捕获时的身份）；外部 `c` 已指向新对象

---

### Requirement R7: 循环变量每次迭代新绑定

所有循环（`for` / `foreach` / `while`）每次迭代为循环变量创建新绑定。

#### Scenario: foreach 循环变量
- **WHEN** 源码：
  ```z42
  var fns = new List<() -> int>();
  foreach (var i in new[] { 1, 2, 3 }) {
      fns.Add(() => i);
  }
  ```
- **THEN** 三个闭包分别返回 1、2、3

#### Scenario: for 循环变量
- **WHEN** C 风格 `for (int i = 0; i < 3; i++)` 循环内创建闭包捕获 `i`
- **THEN** 每次迭代新 `i`，三个闭包分别捕获 0、1、2

#### Scenario: while 循环
- **WHEN** while 循环体内 `var` 声明每次迭代重新执行
- **THEN** 每次新绑定（沿用通用块作用域规则）

---

### Requirement R8: spawn / async 边界 move 捕获

逃逸到独立执行上下文（`spawn` / async task）的闭包强制 move 捕获，捕获项必须 `Send`。

#### Scenario: spawn 默认 move
- **WHEN** `spawn { Use(data); }` 捕获 `data`
- **THEN** `data` 移动到闭包；spawn 后外部再用 `data` 编译错误（use after move）

#### Scenario: 非 Send 捕获
- **WHEN** spawn 闭包捕获非 Send 类型
- **THEN** 编译错误 Z0809（已在 concurrency.md 定义）

#### Scenario: 来源
- **WHEN** 详细规则
- **THEN** 由 `concurrency.md §6.3` 维护；本 spec 引用之

---

### Requirement R9: 单目标闭包（无多播）

闭包是单目标值；不支持 `+=` / `-=` 多播组合。

#### Scenario: 拒绝 +=
- **WHEN** 源码 `f += x => ...;`（`f` 是函数类型）
- **THEN** 编译错误：函数类型不支持 `+=`

#### Scenario: 多订阅替代
- **WHEN** 用户需要多 handler
- **THEN** 应使用 `EventEmitter<T>` 库类型（独立 stdlib spec）

---

### Requirement R10: 闭包可比较

闭包支持 `==` / `!=`，按 (target, function) 对相等。

#### Scenario: 自身比较
- **WHEN** `f == f`
- **THEN** true

#### Scenario: 不同字面量
- **WHEN** `f = x => x + 1; g = x => x + 1; f == g`
- **THEN** false（不同字面量 → 不同对象）

#### Scenario: 同函数引用
- **WHEN** 两个 `myFn` 函数引用值
- **THEN** `==` 为 true（target=null + 同 function pointer）

---

### Requirement R11: 闭包不可序列化

闭包不实现序列化接口；尝试序列化编译错误。

#### Scenario: 序列化拒绝
- **WHEN** 调用 `serialize(closure)` 或类似 API
- **THEN** 编译错误：函数类型不可序列化

---

### Requirement R12: 编译器自动选实现策略

每个闭包字面量由编译器三档之一实现：栈 / 单态化 / 堆擦除。用户语法不变。

#### Scenario: 档 A 栈分配
- **WHEN** 闭包传给具体 `(T) -> R` 形参，未逃逸
- **THEN** env 在调用方栈帧，0 次堆分配

#### Scenario: 档 B 单态化
- **WHEN** 闭包传给泛型 `<F: (T) -> R>` 形参，单态 call site
- **THEN** 为该闭包类型生成专用版本，调用内联

#### Scenario: 档 C 堆擦除
- **WHEN** 闭包逃逸（存字段 / 跨 spawn / 作返回值 / 入集合）
- **THEN** env 进堆，胖指针 + vtable

#### Scenario: 决策可观测
- **WHEN** 启用 `--warn-closure-alloc`
- **THEN** 编译器列出所有走档 C 的闭包字面量位置

---

### Requirement R13: 共享可变值用 `Ref<T>`

值类型默认快照捕获，需要可变共享时用 `Ref<T>` / `Box<T>` 包装。

#### Scenario: Ref<T> 共享计数
- **WHEN** 源码：
  ```z42
  var counter = new Ref<int>(0);
  var inc = () => counter.Value = counter.Value + 1;
  inc(); inc();
  Console.WriteLine(counter.Value);
  ```
- **THEN** 输出 `2`

> `Ref<T>` / `Box<T>` 本身的类型设计属于独立 stdlib spec

---

### Requirement R14: L 阶段限定

| L 阶段 | 允许 |
|---|---|
| L2 | 无捕获 lambda + `(T) -> R` 函数类型 + 表达式短写 + 无捕获 local function |
| L3 | 完整闭包（捕获 / 三档实现 / Send 派生 / `--warn-closure-alloc` 诊断）|

#### Scenario: L2 拒绝捕获
- **WHEN** L2 编译器，lambda body 引用外层 local
- **THEN** 编译错误：闭包捕获是 L3 特性

#### Scenario: L3 全部允许
- **WHEN** L3 编译器
- **THEN** 支持本 spec 所有行为

---

## MODIFIED Requirements

### concurrency.md §6.3

**Before:** 自定义"闭包按 move 捕获 + 捕获变量必须 Send"，闭包概念未定义。
**After:** §6.3 改为引用本 spec R8；自身仅保留并发特定的部分（spawn / SpawnBlocking 的 task 调度语义）。

---

## IR Mapping

闭包相关 IR 指令草案（opcode 编号在 L3 实现时分配）：

| 指令 | 操作数 | 语义 |
|------|--------|------|
| `mkclos` | env_layout, fn_ref | 创建闭包对象（档 A 栈；档 C 堆）|
| `callclos` | closure, args... | 调用闭包（档 A/B 直调；档 C vtable）|
| `mkref` | value | 创建 `Ref<T>` |
| `loadref` / `storeref` | ref [, value] | 读/写 `Ref<T>` 内值 |

档 B 单态化不产生新 IR，直接 inline 展开。具体 IR 编码补到 `docs/design/ir.md`。

## Pipeline Steps

- [x] **Lexer**：`=>` token；`fn` 位置放宽（允许 block 内）
- [x] **Parser / AST**：`LambdaExpr` / `FnTypeExpr` / `ExprBodyFn` / 嵌套 `FnDecl`（sealed record）
- [x] **TypeChecker**：lambda 类型推断；捕获分析（值类型快照 / 引用类型按身份）；逃逸分析（档 A/B/C）；Send 派生
- [x] **IR Codegen**：`mkclos` / `callclos` 生成；三档代码模式
- [x] **VM interp**：闭包对象表示；vtable dispatch；栈/堆分配
