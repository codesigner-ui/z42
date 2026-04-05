# Spec: Native InternalCall

## ADDED Requirements

---

### Requirement: `extern` 关键字作为方法修饰符

#### Scenario: extern 方法声明（正常）
- **WHEN** z42 源文件包含：
  ```z42
  [Native("__println")]
  public static extern void WriteLine(string value);
  ```
- **THEN** Parser 产出 `FunctionDecl { IsExtern: true, NativeIntrinsic: "__println", Body: BlockStmt([]) }`

#### Scenario: extern 方法无函数体（分号结尾）
- **WHEN** `extern` 方法后跟 `{` 而非 `;`
- **THEN** Parser 报错：`` expected `;` but got `{` ``

#### Scenario: extern 缺少 [Native] 属性
- **WHEN** 方法声明带 `extern` 但无 `[Native]` 属性
- **THEN** TypeChecker 报 `Z0092: extern method 'Foo' requires [Native] attribute`

#### Scenario: [Native] 缺少 extern 关键字
- **WHEN** 方法声明有 `[Native("__println")]` 但无 `extern`，且有函数体
- **THEN** TypeChecker 报 `Z0093: [Native] attribute requires extern modifier`

---

### Requirement: NativeTable 注册与验证

#### Scenario: 已知 intrinsic name（正常）
- **WHEN** `[Native("__println")] extern void Foo(string s)` 且 `__println` 在 NativeTable 中（ParamCount=1）
- **THEN** TypeChecker 通过，无诊断

#### Scenario: 未知 intrinsic name
- **WHEN** `[Native("__nonexistent")] extern void Foo()`
- **THEN** TypeChecker 报 `Z0090: unknown intrinsic '__nonexistent'`

#### Scenario: 参数数量不匹配
- **WHEN** `[Native("__println")] extern void Foo()` 但 `__println` 要求 ParamCount=1
- **THEN** TypeChecker 报 `Z0091: intrinsic '__println' expects 1 parameter(s), got 0`

---

### Requirement: Codegen — extern 方法体注入

#### Scenario: 无返回值的 extern 方法
- **WHEN** `[Native("__println")] extern void WriteLine(string value)` 编译
- **THEN** IR 函数体为：
  ```json
  [
    {"op": "builtin", "dst": 0, "name": "__println", "args": [0]},
    {"op": "ret", "reg": -1}
  ]
  ```
  其中参数寄存器按声明顺序从 `%0` 开始

#### Scenario: 有返回值的 extern 方法
- **WHEN** `[Native("__readline")] extern string ReadLine()` 编译
- **THEN** IR 函数体为：
  ```json
  [
    {"op": "builtin", "dst": 0, "name": "__readline", "args": []},
    {"op": "ret", "reg": 0}
  ]
  ```

#### Scenario: 多参数 extern 方法
- **WHEN** `[Native("__str_substring")] extern string Substring(string s, int start, int length)` 编译
- **THEN** IR 函数体中 args 为 `[0, 1, 2]`（依序对应三个参数寄存器）

---

### Requirement: VM — HashMap dispatch

#### Scenario: 已知 intrinsic 正常调度
- **WHEN** VM 执行 `{"op": "builtin", "dst": 2, "name": "__println", "args": [1]}`
- **THEN** 从 builtins HashMap 取到函数指针，以 `&[args[1]]` 调用，结果存入寄存器 2

#### Scenario: 未知 intrinsic 报错
- **WHEN** VM 执行 `{"op": "builtin", "name": "__nonexistent", ...}`
- **THEN** 运行时报 `RuntimeError: unknown builtin '__nonexistent'`

#### Scenario: HashMap 在 VM 启动时一次性构建
- **WHEN** `Vm::new(module, builtins)` 被调用
- **THEN** `builtins` HashMap 已包含全部内置函数，后续执行不再做任何字符串拼接或反射

---

### Requirement: VM — stdlib 自动加载

#### Scenario: z42.core 无条件加载
- **WHEN** VM 启动，`resolve_libs_dir()` 返回有效路径，`z42.core.zpkg` 存在
- **THEN** z42.core 的所有函数并入运行模块，用户代码可调用 `z42.core.*` 中的函数

#### Scenario: z42.core 不存在时不崩溃
- **WHEN** libs 目录不存在或 `z42.core.zpkg` 缺失
- **THEN** VM 以警告继续启动（`warn: z42.core not found, stdlib unavailable`），不 panic

#### Scenario: 用户 zpkg 依赖加载
- **WHEN** 用户 zpkg 的 `dependencies` 字段为 `[{file: "z42.io.zpkg", namespaces: ["z42.io"]}]`
- **THEN** VM 在执行用户代码前加载 `z42.io.zpkg` 并合并，用户代码可调用 `z42.io.Console.WriteLine`

#### Scenario: 依赖文件不存在
- **WHEN** `dependencies` 中指定的 zpkg 文件不在 libs_dir
- **THEN** VM 启动报 `LoadError: dependency 'z42.io.zpkg' not found in libs path`

---

### Requirement: 端到端 — stdlib 调用

#### Scenario: Console.WriteLine 调用链
- **WHEN** 编译并运行：
  ```z42
  using z42.io;
  class Program {
      static void Main() {
          Console.WriteLine("hello stdlib");
      }
  }
  ```
- **THEN** 标准输出打印 `hello stdlib`，进程退出码 0

#### Scenario: Assert.Equal 成功
- **WHEN** 运行包含 `Assert.Equal(42, 42)` 的程序（z42.core 已加载）
- **THEN** 不抛出异常，程序正常完成

#### Scenario: Assert.Equal 失败
- **WHEN** 运行包含 `Assert.Equal(1, 2)` 的程序
- **THEN** VM 抛出 AssertionError，进程退出非零码

---

## Pipeline Steps

受影响的 pipeline 阶段（按顺序）：

- [x] Lexer（新增 `extern` token）
- [x] Parser / AST（属性收集 + `IsExtern` + `NativeIntrinsic`）
- [x] TypeChecker（NativeTable 验证）
- [x] IR Codegen（注入 Builtin + Ret）
- [ ] VM interp（HashMap dispatch + stdlib 加载）

## Error Codes

| 代码 | 含义 |
|------|------|
| Z0090 | 未知 intrinsic 名称 |
| Z0091 | Intrinsic 参数数量不匹配 |
| Z0092 | `extern` 方法缺少 `[Native]` 属性 |
| Z0093 | `[Native]` 属性出现在非 `extern` 方法上 |
