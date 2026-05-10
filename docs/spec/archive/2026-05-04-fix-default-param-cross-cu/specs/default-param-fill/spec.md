# Spec: cross-CU default param fill

## ADDED Requirements

### Requirement: 跨 CU 方法调用默认参数自动填充类型默认值

#### Scenario: bool 默认参数
- **WHEN** stdlib `void Invoke(T arg, bool flag = false)` 在 CU A 中定义；CU B 调 `bus.Invoke(arg)`
- **THEN** IR codegen 为缺位 bool param emit `ConstBoolInstr(false)`；函数体读 `flag` 拿到 false（非 Null）

#### Scenario: int 默认参数
- **WHEN** 跨 CU 方法 `void m(string s, int n = 0)`，调 `obj.m("foo")`
- **THEN** 缺位 int param emit `ConstI32Instr(0)`；函数体读 `n` 拿到 0

#### Scenario: 引用类型默认参数
- **WHEN** 跨 CU 方法 `void m(int x, string s = null)` 或 `void m(int x, Object o = null)`
- **THEN** 缺位 ref param emit `ConstNullInstr`；函数体读 `s/o` 拿到 null

### Requirement: 同 CU 行为保持原状

#### Scenario: 同 CU default value 仍传递
- **WHEN** 同一 CU 中 `void m(int x, string s = "hello")`，同 CU 调 `m(5)`
- **THEN** `BoundDefault` 路径生效，函数体读 `s` 拿到 `"hello"`（不退化为 type-default）

### Requirement: D2d-2 overload workaround 还原

#### Scenario: MulticastAction.Invoke 单签名形式
- **WHEN** `MulticastAction.Invoke(T arg, bool continueOnException = false)` 单签名（无 1-arg overload）
- **THEN** 跨 CU 调 `bus.Invoke(arg)` 走 D-9 fallback，正常工作；既有 multicast_action_basic / multicast_subscription_refs / multicast_unsubscribe / multicast_exception_aggregate / event_keyword_multicast / interface_event golden 仍 GREEN

## Pipeline Steps

- [ ] Lexer
- [ ] Parser / AST
- [ ] TypeChecker
- [x] IR Codegen（核心修改 — FillDefaults fallback + EmitTypeDefault）
- [ ] VM interp
