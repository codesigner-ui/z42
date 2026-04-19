# Spec: 调试变量名 + 调用栈

## ADDED Requirements

### Requirement: 局部变量名表

#### Scenario: 编译器生成变量名映射
- **WHEN** 编译含局部变量的函数 `int x = 42; string name = "hello";`
- **THEN** IrFunction.LocalVarTable 包含 `[("x", regId), ("name", regId)]`
- **THEN** 参数也包含在内：`("this", 0)` 或 `("paramName", N)`

#### Scenario: 变量名表序列化到 zbc
- **WHEN** `--emit zbc` 生成二进制
- **THEN** DBUG section 包含每个函数的变量名表
- **THEN** ZbcFlags.HasDebug 标志位被设置

#### Scenario: disasm 输出变量名表
- **WHEN** `z42c disasm file.zbc`
- **THEN** 每个函数输出 `.locals` section，格式：`%regId = varName`

### Requirement: 错误信息含变量名

#### Scenario: 运行时错误显示变量名
- **WHEN** VM 执行时发生错误，错误涉及寄存器 `%3`，该寄存器对应变量 `count`
- **THEN** 错误信息输出 `at FuncName (line 42)` （行号已有，变量名作为 locals 表补充信息）

### Requirement: 调用栈链

#### Scenario: 异常传播显示完整调用栈
- **WHEN** `Main()` 调用 `Foo()`，`Foo()` 调用 `Bar()`，`Bar()` 中抛出异常
- **THEN** 错误输出包含完整调用栈：
  ```
  Error: division by zero
    at Demo.Bar (line 15)
    at Demo.Foo (line 8)
    at Demo.Main (line 3)
  ```

#### Scenario: 跨函数异常传播
- **WHEN** 异常未被 catch 且跨越多层调用
- **THEN** 每层调用都追加到调用栈，最深的在最上面
