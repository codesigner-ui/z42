# Tasks: 错误码体系完善

> 状态：🟢 已完成 | 创建：2026-04-19 | 完成：2026-04-19

**变更说明：** 让 ParseException 携带具体错误码，替代所有错误统一用 E0201 的现状

**原因：** M6 要求"统一错误码（E####）、友好错误消息"；当前 E0202-E0205/E0301/E0405 已定义但未使用

**文档影响：** 无（错误码已在 DiagnosticCatalog 中定义）

---

## 实施清单

- [x] 1.1 ParseException 添加可选 Code 属性
- [x] 1.2 catch 站点使用 ex.Code ?? DiagnosticCodes.UnexpectedToken 替代硬编码
- [x] 1.3 parser throw 站点使用具体错误码：
  - ExpectKind 失败 → E0202 (ExpectedToken)
  - Feature disabled → E0301 (FeatureDisabled)
  - Invalid modifier → E0405 (InvalidModifier)
- [x] 1.4 enum member 修饰符错误 → E0405 (InvalidModifier)
- [x] 2.1 验证：dotnet build + dotnet test + ./scripts/test-vm.sh 全绿
- [x] 2.2 更新 golden error tests 的 expected_error.txt（3个文件更新了错误码）

## 变更的错误码映射

| 场景 | 旧错误码 | 新错误码 |
|------|---------|---------|
| expected `X`, got `Y` | E0201 | E0202 |
| feature disabled | E0201 | E0301 |
| cannot combine modifiers | E0201 | E0405 |
| enum members cannot have modifiers | E0201 | E0405 |
