# Spec: closures

## ADDED Requirements

### Requirement: lambda 整链（MVP）

#### Scenario: 无捕获
- **WHEN** `Func<int,int> dbl = x => x * 2; int y = dbl(21);`
- **THEN** lift `Main__lambda_0`（模块函数表末尾、IsStatic）；LoadFn + CallIndirect；执行 y==42

#### Scenario: 捕获
- **WHEN** `int k = 2; Func<int,int> mul = x => x * k;`
- **THEN** BoundCapture(k)；lift env 版（env reg0，k→env[0] array_get）；MkClos 带捕获寄存器；执行正确

#### Scenario: 字节
- **WHEN** closcheck 第 7 zbc 源双编译
- **THEN** 逐字节一致（0x55/0x56/0x57 编码 + lift 命名/位次/SIGS）；byte-compare 7/7

## Pipeline Steps
- [ ] Lexer（如缺 FatArrow）/ Parser / TypeChecker / Codegen / ZbcWriter
