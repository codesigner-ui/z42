# Spec: `typeof(T)` 返回 `Std.Type`

## MODIFIED Requirements

### Requirement: typeof 求值结果类型

**Before:** `typeof(T)` 在 parser 阶段 desugar 成 `LitStrExpr`，求值为 **string**（类型名）。
**After:** `typeof(T)` 经 `TypeofExpr` → `BoundTypeof`（结果类型为 `Std.Type` 类）→ FunctionEmitter emit `__typeof` builtin 调用，求值为 **`Std.Type`** 对象。

## ADDED Requirements

### Requirement: typeof 返回 Type 对象

#### Scenario: 基础类型
- **WHEN** 程序求值 `typeof(int)` 并取 `.Name`
- **THEN** 得到 `Std.Type`，其 `Name == "int"`（i32→int 规范化别名）

#### Scenario: 主模块用户类型带真句柄（成员可枚举）
- **WHEN** 对主模块类 `Point { int X; int Y; }` 求值 `typeof(Point)` 并调 `.GetFields()`
- **THEN** `Name == "Point"`，且 `GetFields().Length == 2`（FunctionEmitter emit 限定名 `Demo.Point` → `make_type_from_name` 命中主模块 `type_registry` → 真 `TypeDesc` 句柄）

#### Scenario: 与 GetType 一致
- **WHEN** 对类 `Point` 的实例 `p`，比较 `typeof(Point).Name` 与 `Type viaGetType = p.GetType(); viaGetType.Name`
- **THEN** 两者相等（同一类型身份）；`FullName` 同理

## IR Mapping

**无新 IR 指令**。FunctionEmitter `VisitTypeof` emit：`ConstStrInstr`（类型限定名）+ `BuiltinInstr(dst, "__typeof", [nameReg])`——复用既有 builtin 调用 lowering。

## Pipeline Steps

- [x] Lexer —（不涉及；`typeof` token 已有）
- [x] Parser / AST — `ParseTypeof` 产 `TypeofExpr(Target)`（新 AST 节点）
- [x] TypeChecker — `TypeofExpr` → `BoundTypeof`；结果类型解析为 Std.Type 类
- [x] IR Codegen — FunctionEmitter `VisitTypeof`：限定名 ConstStr → `__typeof` BuiltinInstr
- [x] VM interp — 新增 `__typeof` builtin（复用 `make_type_from_name`）
- [ ] stdlib —（不涉及；复用反射 MVP 已落地的 Type 类）
