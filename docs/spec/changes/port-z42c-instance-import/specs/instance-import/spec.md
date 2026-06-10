# Spec: instance-import

## ADDED Requirements

### Requirement: 实例跨包调用

#### Scenario: imported 类对象 → VCall + DEPS
- **WHEN** `var sb = new StringBuilder(); sb.Append("a")`（using Std.Text）
- **THEN** emit VCall（vtable 赢）且 DEPS 含 (z42.text.zpkg, [Std.Text])

#### Scenario: prim receiver → DepIndex Call
- **WHEN** `s.Substring(0, 2)`（s: string）
- **THEN** typecheck 通过（吸收）；emit CallInstr FQ（如 Std.String.Substring），recv 前置实参

#### Scenario: receiver 自有方法不被劫持
- **WHEN** 本地类自有同名方法（如用户类 ContainsKey）
- **THEN** 走 VCall，不被 stdlib 同形方法劫持

### Requirement: byte-identical
#### Scenario: textapp 全文件对账
- **WHEN** 第 4 工程（StringBuilder+Substring+Console）双编译
- **THEN** 全文件逐字节一致（gate 常驻）且 z42vm 直跑输出正确
