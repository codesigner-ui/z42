# Spec: JIT 闭包指令翻译

> 用户视角行为契约由 archived `impl-closure-l3-core/specs/closure-l3-core/spec.md`
> L3-C-2 / L3-C-5 / L3-C-6 锁定。本 spec 定义 `impl-closure-l3-jit-complete`
> 的实现层面可验证行为：JIT 翻译必须与 interp 行为一致。

## ADDED Requirements

### Requirement JIT-1: LoadFn 翻译

#### Scenario: JIT 模式下 LoadFn 行为与 interp 一致
- **WHEN** JIT 翻译 `LoadFn { dst, func: "Demo.Helper" }`
- **THEN** 运行时 dst reg 持 `Value::FuncRef("Demo.Helper")`，与 interp 等价

#### Scenario: 不再 bail
- **WHEN** 任意函数体含 LoadFn
- **THEN** JIT 编译通过（不报"L3+ closure work"错误）

### Requirement JIT-2: MkClos 翻译

#### Scenario: 0-capture lambda 不走 MkClos
- **WHEN** 无捕获 lambda 编译
- **THEN** IR 仍为 LoadFn（与 L2 一致），不走 MkClos 路径——本规则保留

#### Scenario: 有 capture lambda → MkClos
- **WHEN** JIT 翻译 `MkClos { dst, fn_name, captures: [r1, r2] }`
- **THEN** 运行时：
  1. 分配 `Vec<Value>` env，依次拷入 `frame.regs[r1]`, `frame.regs[r2]`
  2. 通过 `vm_ctx.heap().alloc_array(env)` 获 `Value::Array(GcRef)`
  3. 构造 `Value::Closure { env: rc, fn_name: "..." }`
  4. 写入 `frame.regs[dst]`

#### Scenario: GC root 注册
- **WHEN** MkClos 执行
- **THEN** 新分配的 env Vec<Value> 通过现有 `heap.alloc_array` 已经登记到 GC root

### Requirement JIT-3: CallIndirect 翻译 — FuncRef 路径

#### Scenario: JIT 调用 FuncRef
- **WHEN** callee reg 持 `Value::FuncRef(name)`，执行 CallIndirect
- **THEN** 直接通过 `ctx.fn_entries[name]` 调用对应 JitFn，参数与 interp 一致

#### Scenario: 未定义函数报错
- **WHEN** callee 是 FuncRef 但 ctx.fn_entries 不含此 name
- **THEN** 抛运行时异常（与 interp 行为一致）

### Requirement JIT-4: CallIndirect 翻译 — Closure 路径

#### Scenario: JIT 调用 Closure
- **WHEN** callee reg 持 `Value::Closure { env, fn_name }`
- **THEN** 调用顺序：
  1. 构造 callee args = `[Value::Array(env.clone())]` ++ 用户实参
  2. 通过 `ctx.fn_entries[fn_name]` 找 JitFn
  3. 创建 callee frame（registered for GC scanning）
  4. 调用 JitFn；处理返回值 / 异常

#### Scenario: 类型错误
- **WHEN** callee reg 既非 FuncRef 也非 Closure
- **THEN** 抛运行时异常 "CallIndirect: expected FuncRef or Closure"

### Requirement JIT-5: Helper 注册

#### Scenario: HelperIds 包含 3 个新字段
- **WHEN** 检视 `HelperIds` struct
- **THEN** 包含 `load_fn`, `call_indirect`, `mk_clos` 三个 FuncId 字段

#### Scenario: 模块编译时注册
- **WHEN** `compile_module` 设置 JIT linker
- **THEN** 三个 helper 符号都通过 `builder.symbol(...)` 注册到 JIT module

### Requirement JIT-6: Golden 解锁

#### Scenario: 4 个 closure golden 移除 interp_only 标记
- **WHEN** 本变更归档
- **THEN** lambda_l2_basic / local_fn_l2_basic / closure_l3_capture /
  closure_l3_loops 的 `interp_only` 文件已删除；JIT 模式 100% 通过

## Pipeline Steps

- [x] **Lexer / Parser / TypeChecker / IR / Interp**：无改动（已实现）
- [x] **VM JIT**：补全 LoadFn / CallIndirect / MkClos 翻译；helper 注册
- [x] **测试基础设施**：4 个 golden 解锁 JIT 模式
