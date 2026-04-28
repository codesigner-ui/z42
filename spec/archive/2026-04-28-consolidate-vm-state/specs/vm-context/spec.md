# Spec: VmContext

## ADDED Requirements

### Requirement: VmContext 集中持有 VM 运行时可变状态

#### Scenario: 静态字段读写穿过 VmContext

- **WHEN** 用户代码 `MyClass.SomeStatic = 42`，IR 发射 `StaticSet { field, src }`
- **THEN** interp / JIT 都通过 `&mut VmContext` 调用 `ctx.static_set(field, val)`，
  数据存入 `ctx.static_fields: HashMap<String, Value>`，**不再有任何 thread_local 介入**

#### Scenario: 同一进程多 VmContext 实例独立

- **WHEN** 宿主代码创建两个 `VmContext` 实例 `ctx1` / `ctx2`，分别在各自上下文
  跑同一份 Module
- **THEN** `ctx1.static_set("X", v1)` 后 `ctx2.static_get("X")` 必须返回 `Null`
  （或 ctx2 自身 set 的值），两个 ctx 的静态字段互不可见

#### Scenario: 同一线程多 VmContext 实例顺序运行不污染

- **WHEN** 同一线程先用 `ctx1` 跑完一个 Module，再用 `ctx2` 跑另一个 Module
- **THEN** `ctx2` 启动时 static_fields 必须是空（除非显式继承），不被 `ctx1`
  的 static_fields 残留污染

### Requirement: 异常传播路径单一化

#### Scenario: interp 抛出在 interp 中捕获

- **WHEN** 脚本里 `throw new Exception("oops")` 在 try/catch 块内
- **THEN** `exec_function` 返回 `ExecOutcome::Thrown(value)`，外层 try block
  的 `find_handler` 找到对应 catch 跳转，**不经过 thread_local**

#### Scenario: JIT 抛出经 ctx 传到 interp 边界

- **WHEN** JIT 编译的函数抛出（`extern "C"` 返回 `1`），上游是 interp loop
- **THEN** JIT helper 调 `ctx.set_exception(val)`，JitModule::run 检查 ret==1
  时调 `ctx.take_exception()` 取出并 wrap 成 `ExecOutcome::Thrown(val)` 给 interp
- **AND** 全程不使用 `thread_local PENDING_EXCEPTION` / `UserException`
  sentinel / `anyhow::Error` 包装异常值

#### Scenario: 跨 ctx 异常隔离

- **WHEN** `ctx1` 上的 JIT 调用过程中抛出未捕获异常，但 `ctx2` 同时也有 in-flight
  调用（多 VM 实例场景）
- **THEN** `ctx1.take_exception()` 拿到 `ctx1` 的异常值，`ctx2` 的 pending 槽
  独立不受影响

### Requirement: Lazy loader 状态绑定到 VmContext

#### Scenario: install / uninstall 通过 ctx 方法

- **WHEN** main 入口调用 `ctx.install_lazy_loader(libs_dir, main_pool_len)`
- **THEN** lazy_loader 实例存入 `ctx.lazy_loader: Option<LazyLoader>`，
  `ctx.try_lookup_function(name)` / `ctx.try_lookup_type(name)` /
  `ctx.declared_namespaces()` 全部走 ctx 方法
- **AND** 不再存在 `lazy_loader::STATE` thread_local 单例，也不再有自由
  函数 `try_lookup_function` / `declared_namespaces`

#### Scenario: 跨 ctx 加载隔离

- **WHEN** `ctx1.install_lazy_loader(libsA)` + `ctx2.install_lazy_loader(libsB)`
  在同一线程串行执行
- **THEN** `ctx1.try_lookup_function("Std.Foo")` 只在 libsA 中找；
  `ctx2.try_lookup_function("Std.Foo")` 只在 libsB 中找

### Requirement: 删除已知死代码

#### Scenario: interp PENDING_EXCEPTION 槽不再存在

- **WHEN** 检查 `interp/mod.rs` 源码
- **THEN** 不再有 `thread_local! { static PENDING_EXCEPTION: ... }` 声明
- **AND** 不再有 `pub(crate) fn user_throw(...)` /
  `pub(crate) fn user_exception_take(...)` /
  `struct UserException` / `impl Display for UserException`
- **AND** `interp/exec_function` 内对 `e.is::<UserException>()` 的检查路径删除

#### Scenario: JIT 路径错误传播改用 ctx 字段

- **WHEN** 检查 `jit/helpers.rs` 源码
- **THEN** `set_exception` / `take_exception_error` 接受 `&VmContext` 或
  `&mut VmContext` 而不是访问 thread_local
- **AND** 不再有 `thread_local! { static PENDING_EXCEPTION: ... }` 声明

## Pipeline Steps

受影响的 pipeline 阶段（按顺序）：

- [ ] Lexer — 不受影响
- [ ] Parser / AST — 不受影响
- [ ] TypeChecker — 不受影响
- [ ] IR Codegen — 不受影响（IR 指令集 / wire format 零变化）
- [ ] VM interp — **重写状态访问**：所有 thread_local 读/写改为 `&mut VmContext` 方法
- [ ] VM JIT — **ABI 扩展**：JIT helpers 通过 `JitModuleCtx::vm_ctx: *mut VmContext`
      访问 ctx；helper 内部读写改 ctx 字段而非 thread_local

## IR Mapping

无 IR 变化。`StaticGet` / `StaticSet` / `Throw` / 异常 catch 等 IR 指令的字节
格式、opcode 编号、参数布局完全不变；只是 VM 端的解释/JIT 实现内部状态访问
方式改写。

zbc 二进制格式：完全不变。
