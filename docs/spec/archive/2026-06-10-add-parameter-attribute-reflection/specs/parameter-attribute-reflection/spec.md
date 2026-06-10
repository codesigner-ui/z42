# Spec: 参数级用户 attribute 反射

## ADDED Requirements

### Requirement: 参数声明捕获 attribute

#### Scenario: 形参带单个 attribute
- **WHEN** 源码 `void M([Tag("x")] int p) {}`，`Tag : Attribute`
- **THEN** 编译器捕获 `p` 上的 `[Tag("x")]`（不再丢弃），合成 factory `__attr$param$<M-qualified>$0$0`，并经 SIGS 持久化

#### Scenario: 多形参，部分带 attribute
- **WHEN** `void M([A] int p0, int p1, [B][C] int p2)`
- **THEN** SIGS 每参数 attr 块分别为 `[A]` / `空(count=0)` / `[B,C]`；wire 恒按 ParamCount 写 count 字段

#### Scenario: 无参 / 无 attribute 函数
- **WHEN** 函数无参数，或所有参数无 attribute
- **THEN** 无参 → 不写 per-param 块；有参无 attr → 每参数写 `count=0`。空 module（函数数=0）的 zbc 仅版本字节相对 1.14 变化

### Requirement: ParameterInfo 反射参数 attribute

#### Scenario: GetCustomAttributes 返回活实例
- **WHEN** `typeof(C).GetMethods()` 取到 `M` 的 `MethodInfo`，`m.GetParameters()[0].GetCustomAttributes()`
- **THEN** 返回该参数的 attribute 活实例数组（构造参数已应用，经 factory thunk 物化 + 缓存）；实例字段可读

#### Scenario: GetAttribute(Type) 按类型取
- **WHEN** `p.GetAttribute(typeof(Tag))`
- **THEN** 命中则返回该 attribute 实例，否则返回 null（镜像 `FieldInfo.GetAttribute`）

#### Scenario: 无 attribute 的参数
- **WHEN** 参数无 attribute
- **THEN** `GetCustomAttributes()` 返回空数组（非 null），`GetAttribute(T)` 返回 null

#### Scenario: 跨 zpkg 参数 attribute
- **WHEN** attribute 类型与被标注方法在不同 zpkg
- **THEN** factory ref 的 (type-name, factory-func) 跨包解析正确，反射取回实例（同 C3b/field 跨包路径）

## MODIFIED Requirements

### Requirement: zbc / zpkg wire 格式

**Before:** SIGS 每函数记录终止于 C3b 方法级 attr 块（zbc 1.14 / zpkg 0.16）。
**After:** 方法级 attr 块之后追加每参数 attr 块（`for i in 0..ParamCount: attr_count u16 + (typeName idx, factory idx) 对`）。zbc 1.15 / zpkg 0.17，strict-pin（reader 精确匹配）。

## IR Mapping

- `Param.Attributes` → `IrFunction.ParamAttributes[i]` → SIGS per-param `WriteAttrRefs` 块 → runtime `(qualifiedFunc, paramIdx)` 索引。
- factory thunk：`__attr$param$<qualifiedFunc>$<paramIdx>$<n>` parameterless func，返回 `new <AttrType>(...)`，经 `call_attribute_factories` / `run_returning` 物化。

## Pipeline Steps

- [x] Lexer — 无变化（`[` `]` 已有）
- [ ] Parser / AST — `Param.Attributes` 捕获
- [ ] TypeChecker — 无新规则（attribute 类型校验复用现有）
- [ ] IR Codegen — `ParamAttributes` 填充 + factory 合成
- [ ] Binary (ZbcWriter/Reader + ZpkgWriter) — SIGS per-param 块 + 版本 bump
- [ ] VM (zbc_reader / loader / reflection builtin)
- [ ] z42c writer 同步（**Decision 1 排程后**）
