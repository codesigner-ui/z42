# Spec: try

## ADDED Requirements

### Requirement: try/catch/throw 整链

#### Scenario: 绑定
- **WHEN** `try { throw new Exception("x"); } catch (Exception e) { ... } finally { ... }`
- **THEN** 0 错误；e 在 catch 体可用

#### Scenario: 字节
- **WHEN** codegen 上式
- **THEN** FUNC excCount>0 + 条目编码与 C# 同源逐字节（含 intern 位）

#### Scenario: 执行
- **WHEN** trycheck（throw→分类 catch→finally→oracle）经 z42vm
- **THEN** 干净退出；zbc byte-compare 5/5

## Pipeline Steps
- [ ] TypeChecker / Codegen / ZbcWriter（syntax 已有）
