# Spec: interface

## ADDED Requirements

### Requirement: interface 类型与分派

#### Scenario: 声明与实现
- **WHEN** `interface IShape { int Area(); } class Box : IShape { ... public int Area() {...} }`
- **THEN** 0 错误；Box 可赋给 IShape 变量/参数；未实现类赋接口 → 错误

#### Scenario: 接口分派
- **WHEN** `int Compute(IShape s) { return s.Area(); }`
- **THEN** 绑定 instance call（ret int）；codegen VCall 短名；执行经 vtable 正确

#### Scenario: 字节
- **WHEN** ifacecheck 双编译
- **THEN** 逐字节一致（接口无 TYPE 条目、Box base=Std.Object）；zbc byte-compare 6/6

## Pipeline Steps
- [ ] TypeChecker / IrGen 判别 / TSIG（syntax 已有；zbc 写入器零改动）
