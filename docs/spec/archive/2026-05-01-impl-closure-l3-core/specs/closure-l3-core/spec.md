# Spec: L3 闭包核心 — 捕获 + 档 C 堆擦除

> 用户视角行为契约由 archived `add-closures` R5 / R6 / R12 锁定。
> 本 spec 定义 `impl-closure-l3-core` 的实现层面可验证行为。

## ADDED Requirements

### Requirement L3-C-1: 捕获分析（编译期）

#### Scenario: 值类型捕获 → BoundCaptureKind.ValueSnapshot
- **WHEN** lambda body 引用外层值类型 local
- **THEN** TypeChecker 产生 BoundCapture { Kind=ValueSnapshot }，BoundLambda.Captures 包含此项

#### Scenario: 引用类型捕获 → BoundCaptureKind.ReferenceShare
- **WHEN** lambda body 引用外层引用类型 local（class instance / array / string ...）
- **THEN** BoundCapture { Kind=ReferenceShare }

#### Scenario: 多次引用同一名字 → 单个 capture slot
- **WHEN** lambda body 多次引用同一个外层名字 `k`
- **THEN** Captures list 中只有一个 entry；多次引用解析为同一 CaptureIndex

#### Scenario: 不捕获自身参数 + 全局符号
- **WHEN** lambda body 仅引用自身参数 / 静态字段 / 顶层函数 / 类型名
- **THEN** Captures list 为空，走无捕获路径（保留 LoadFn）

### Requirement L3-C-2: 闭包对象表示

#### Scenario: 无捕获 → Value::FuncRef（保留）
- **WHEN** lambda 无 captures
- **THEN** IR emit `LoadFn`，VM 推 `Value::FuncRef(name)`（与 L2 一致）

#### Scenario: 有捕获 → Value::Closure
- **WHEN** lambda 有 captures
- **THEN** IR emit `MkClos`，VM 分配 `Vec<Value>` env，构造 `Value::Closure { env, fn_name }` 推入 dst

### Requirement L3-C-3: 值类型快照语义

#### Scenario: 外层修改不影响闭包
- **WHEN** 源码：
  ```z42
  var x = 5;
  var f = () => x;
  x = 10;
  Console.WriteLine(f());
  ```
- **THEN** 输出 5（闭包 env 在 MkClos 时已快照值 5）

#### Scenario: 闭包内修改不影响外层
- **WHEN** lambda body 修改捕获的值类型 `x`（写操作）
- **THEN** 闭包 env 中的 x 被修改；同一闭包多次调用累积修改；外层 x 不变

### Requirement L3-C-4: 引用类型身份共享

#### Scenario: 闭包内修改对象 → 外层可见
- **WHEN** 源码：
  ```z42
  class Counter { public int n = 0; }
  var c = new Counter();
  var inc = () => c.n = c.n + 1;
  inc(); inc();
  Console.WriteLine(c.n);
  ```
- **THEN** 输出 2（c 是引用类型按身份共享）

#### Scenario: 外层重新指向不影响已创建闭包
- **WHEN** 源码：
  ```z42
  var c = new Counter();
  var inc = () => c.n = c.n + 1;
  c = new Counter();    // 重新指向新对象
  inc();
  Console.WriteLine(c.n);    // 0：闭包仍操作原对象，新 c 未变
  ```
- **THEN** 输出 0

### Requirement L3-C-5: MkClos IR 指令

#### Scenario: IR 指令格式
- **WHEN** 编译 capture-非空 lambda 字面量
- **THEN** emit `MkClos { dst, fn_name=<lifted>, captures=[reg1, reg2, ...] }`
- **WHERE** captures 是 capture 顺序的 reg 列表（与 BoundLambda.Captures 同序）

#### Scenario: ZBC 序列化
- **WHEN** zbc emit/load round-trip
- **THEN** MkClos 完整保留 dst type tag、fn_name pool index、captures count + 每个 reg index

### Requirement L3-C-6: VM Closure 调用

#### Scenario: CallIndirect on Closure
- **WHEN** `Value::Closure { env, fn_name }` 在 callee reg；执行 CallIndirect
- **THEN** 调用 fn_name 对应函数，**实参列表 = [env] + 用户实参**（env 作隐式第一参）

#### Scenario: CallIndirect on FuncRef（兼容）
- **WHEN** `Value::FuncRef(name)` 在 callee reg；执行 CallIndirect
- **THEN** 直接调 name 对应函数，参数与原 L2 行为一致（不 prepend env）

### Requirement L3-C-7: Lifted body 的 env 参数

#### Scenario: 无捕获 lifted body 签名不变
- **WHEN** 无捕获 lambda 提升
- **THEN** lifted IrFunction 参数列表 = 原 lambda 参数（不添加 env）

#### Scenario: 有捕获 lifted body 加 env
- **WHEN** 有捕获 lambda 提升
- **THEN** lifted IrFunction 参数列表 = `[env: Ref] ++ 原参数`；ParamCount + 1

#### Scenario: BoundCapturedIdent → ArrayGet
- **WHEN** lifted body 内访问 captured `k`（CaptureIndex=2）
- **THEN** IrGen emit `ArrayGetInstr(dst, env_reg=0, idx_reg)`，其中 idx_reg 持 const 2

### Requirement L3-C-8: Closure 设计文档调整

#### Scenario: closure.md §4.4 重写
- **WHEN** 本变更归档
- **THEN** §4.4 不再提 `Ref<T>`/`Box<T>`；改为说明"如需共享可变状态，使用 class（引用类型按身份共享）；C# `ref` 关键字是参数级独立特性"

#### Scenario: 决议 #10 删除
- **WHEN** 本变更归档
- **THEN** "共享可变值类型用 `Ref<T>` / `Box<T>`" 决议从决议表移除

### Requirement L3-C-9: 同名嵌套捕获

#### Scenario: 内层 lambda 通过外层 lambda env 捕获
- **WHEN** 源码：
  ```z42
  var k = 10;
  var f = () => {
      var g = () => k;
      return g();
  };
  Console.WriteLine(f());
  ```
- **THEN** 输出 10。f 捕获 k；g 通过 f 的 env 捕获 k（即 g 的 captures 含 k，对应 ArrayGet from f's env）

### Requirement L3-C-10: Local function 捕获

#### Scenario: 一层 local fn 捕获外层 local 合法
- **WHEN** 源码：
  ```z42
  int Outer() {
      var k = 10;
      int Helper(int x) => x + k;
      return Helper(3);
  }
  ```
- **THEN** 编译通过（L2 阶段拒绝；L3 解锁）；Helper 走 capture 路径

#### Scenario: Local function 不捕获时仍走 L2 路径
- **WHEN** local fn 不引用外层 local
- **THEN** 沿用现有 L2 lifted Call 路径（无 env）

### Requirement L3-C-11: spawn 闭包暂不强制 Send

#### Scenario: spawn 闭包按普通规则捕获
- **WHEN** spawn 闭包捕获任意类型 local（含非 Send 类型）
- **THEN** 编译通过（本变更不做 Send 检查）；Send 强制留待 concurrency 工作

---

## MODIFIED Requirements

### TypeChecker BindIdent

**Before:** `_lambdaOuterStack.Peek().LookupVar(name)` 命中时报 Z0301
**After:** 命中时记录 capture 到 `_lambdaBindingStack.Peek().Captures` 并返回 `BoundCapturedIdent`

### Local function 一层嵌套限制（impl-local-fn-l2）

**Before:** local fn 一层嵌套限制中包括"不允许 capture"
**After:** 一层嵌套限制保留（不允许 local fn 内嵌 local fn）；但 local fn 体内引用外层 local 现在是 capture（合法）

### closure.md §4.4 + 决议表

见 L3-C-8。

---

## IR Mapping

| 源码 / Bound 模式 | IR |
|------------------|-----|
| 无捕获 lambda | `LoadFn <fn_name>` (保留 L2 行为) |
| 有捕获 lambda | `MkClos <dst>, <fn_name>, [capture_regs...]` |
| 捕获 ident（lifted body 内）| `ArrayGet <dst>, env_reg=0, <const_idx_reg>` |
| Closure 调用 | `CallIndirect <dst>, <closure_reg>, [args]`（VM 自动 prepend env）|

`MkClos` 二进制格式：
```
[opcode 0x57][type_tag Object][dst u16][fn_idx u32][num_captures u16][cap_reg u16 * N]
```

## Pipeline Steps

- [x] **Lexer**：无改动
- [x] **Parser / AST**：无改动（语法不变）
- [x] **TypeChecker**：BindIdent capture 路径；BindLambda 收集 Captures；local fn 解锁 capture
- [x] **Bound AST**：BoundCapture / BoundCaptureKind / BoundCapturedIdent / BoundLambda.Captures
- [x] **IR Codegen**：MkClos 生成；BoundCapturedIdent → ArrayGet；lifted body env param
- [x] **VM Interp**：Value::Closure 变体；MkClos 解释；CallIndirect 分叉
- [x] **VM JIT**：MkClos bail (interp_only)；本变更不补完
