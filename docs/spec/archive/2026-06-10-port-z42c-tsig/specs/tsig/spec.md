# Spec: tsig — 导出类型签名段

## ADDED Requirements

### Requirement: TSIG/IMPL 段写出

#### Scenario: 有导出模块时 9 段
- **WHEN** `z42c build` 编译含类/函数的工程
- **THEN** zpkg 含 TSIG+IMPL 段（secCount=9），布局逐字段 = C# BuildTsigSection/BuildImplSection

#### Scenario: 全文件 byte-identical（本线退出标准）
- **WHEN** 同工程（buildapp / demo.minimal）分别经 z42c 与 C# CLI（--strip-symbols=false）构建
- **THEN** 两 `.zpkg` **全文件逐字节一致**（xtask e2e cmp 常驻 gate）

### Requirement: 提取确定性

#### Scenario: CU 声明序
- **WHEN** 同一源文件多次提取
- **THEN** ExportedModule 类/成员/函数序恒等于声明序（不经 hashed map 迭代）

## Pipeline Steps
- [ ] ExportedTypeExtractor（semantics）
- [ ] ZpkgWriter TSIG/IMPL（project）
- [ ] driver 接线 + xtask e2e 升级
