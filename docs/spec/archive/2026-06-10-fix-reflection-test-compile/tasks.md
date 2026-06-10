# Tasks: fix-reflection-test-compile

**变更说明：** `z42.core/tests/reflection.z42` 两处编译失败，修复使其编译通过、恢复 GREEN gate：
1. 变量名 `none` 撞 z42 关键字（`TokenKind.None`）→ 改名 `noneAttrs`。
2. `class FieldTag : Std.Attribute`（限定基名）触发 typechecker FQN-upcast 缺口（合成 attribute factory `return new FieldTag()` 无法 upcast 到 unqualified `Attribute` 返回类型）→ 改用 unqualified `: Attribute`（与 src/tests/attributes/*.z42 三个通过的 golden 一致）。
**原因：** add-field-attribute-reflection（da25dc3e）笔误 + 其 gate 漏编此 stdlib z42 测试（`none` parse error 证明该文件从未过 z42 编译路径；且它是唯一在 stdlib 测试里用用户 attribute 的文件）。
**类型：** fix（单文件 test 编译修复）。
**文档影响：** docs/design/language/reflection.md Deferred 段记录根因 compiler bug（限定基名 upcast）。
**子系统：** stdlib（z42.core test）+ docs。

- [x] 1.1 `reflection.z42` 变量 `none` → `noneAttrs`
- [x] 1.2 `reflection.z42` `class FieldTag : Std.Attribute` → `: Attribute`
- [x] 1.3 docs/design/language/reflection.md Deferred：限定基名 FQN-upcast 根因 compiler bug
- [x] 1.4 z42.core lib 编译 + 全绿（7/7 文件，含 test_field_custom_attributes）
- [x] 1.5 GREEN：dotnet test 1554/1554 + z42.core stdlib 7/7（前次 gate 另一红点 secp256k1 test zsym 缺失 = 我多次 partial 构建的陈旧产物，`rm -rf .../tests` 清理后 dotnet test 复绿）

> 状态：🟢 已完成 2026-06-10

## 根因延后（compiler，独立跟进）
合成 attribute factory 的返回类型 upcast 检查（`RequireAssignable` @ TypeChecker.Stmts.cs:421）在派生类基名为**限定 FQN**（`Std.Attribute`）时，与 factory 的 unqualified `Attribute` 返回类型不匹配——FQN 未规范化即比较。与 archived `fix-fqn-class-resolution` 同族。本变更先用 unqualified 基名绕过（test 与 golden 一致），根因留 compiler 独立 change。
