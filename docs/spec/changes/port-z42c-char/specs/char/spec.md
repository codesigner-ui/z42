# Spec: char

## ADDED Requirements

### Requirement: char 字面量整链

#### Scenario: 词法+语法
- **WHEN** 源含 `char c = 'A';` / `'\n'` / `'\\'` / `'\''`
- **THEN** lexer 出 charlit token；parser 出 CharLitExpr（解码值）；dump 稳定

#### Scenario: 类型检查
- **WHEN** `char c = 'A'; bool b = c == 'B';`
- **THEN** 0 错误；c:char，比较 → bool

#### Scenario: 字节编码
- **WHEN** codegen `'A'`
- **THEN** ConstChar（op 0x08 + tag 0x0C + dst + i32 65）；与 C# 同源 .zbc 逐字节一致

#### Scenario: e2e 执行
- **WHEN** charcheck 源（char 比较 + (int)c 算术 + oracle）经 z42c --emit-zbc → z42vm
- **THEN** 干净退出；zbc byte-compare 4/4

## Pipeline Steps
- [ ] Lexer / Parser / TypeChecker / Codegen / ZbcWriter（全链）
