# Spec: 字段级用户 attribute 反射

## ADDED Requirements

### Requirement: 字段可标注用户 attribute

字段声明前可写 `[Attr(args)]`，与类/方法同形（继承 `Std.Attribute` 的类，按真实类名）。

#### Scenario: 字段 attribute 解析 + 合成工厂
- **WHEN** `class C { [Doc("id")] public int x; }`
- **THEN** 编译成功；合成无参工厂 `__attr$fld$C$x$0() => new Doc("id")`；TYPE section 字段 `x` 记 (Doc, factory) 引用

#### Scenario: 实例 + 静态字段均支持
- **WHEN** `[A] public int inst;` 与 `[A] public static int stat;`
- **THEN** 两者的 FieldInfo 反射都能读回 attribute 活实例

### Requirement: FieldInfo.GetCustomAttributes() / GetAttribute(Type)

`FieldInfo` 经反射读回该字段的 attribute 活实例（缓存），镜像 `Type` / `MethodInfo`。

#### Scenario: 读字段 attribute
- **WHEN** `[Route("/u")] public int handler;`，`typeof(C).GetFields()` 找到 `handler` 的 FieldInfo，`f.GetCustomAttributes()`
- **THEN** 返回 `[ Route 实例 ]`，`r.Path == "/u"`

#### Scenario: 按类型单查
- **WHEN** `f.GetAttribute(typeof(Route))`
- **THEN** 返回该 Route 实例；不存在返 null

#### Scenario: 无 attribute 的字段
- **WHEN** `public int plain;`，`f.GetCustomAttributes()`
- **THEN** 空数组

#### Scenario: 缓存
- **WHEN** 对同一 FieldInfo 重复调 `GetCustomAttributes()`
- **THEN** 返回同一批实例

## MODIFIED Requirements

### Requirement: zbc / zpkg 格式版本
**Before:** zbc 1.13 / zpkg 0.15。
**After:** zbc 1.14 / zpkg 0.16（TYPE section 每个字段记录在 `type_str` 后追加 `attr_count: u16` + attr refs）。strict-pin，全量 fixture + stdlib regen。

### Requirement: FieldInfo 结构
**Before:** `FieldInfo { Name, FieldType, IsStatic }`。
**After:** 加隐藏 `__qualified`（`<Class>.<Field>`）+ `GetCustomAttributes()` / `GetAttribute(Type)`。

## IR Mapping

- 无新 IR 指令。**zbc TYPE section wire 变更**：每个字段记录（实例块 + 静态块）在 `type_str: u32` 之后追加 `attr_count: u16` + 每条 (`type_name: u32`, `factory: u32`)，与 class/method attr refs 同形。
- 版本：zbc 13→14，zpkg 15→16（联动）。

## Pipeline Steps

- [x] Parser / AST — `FieldDecl.Attributes` + 字段分支附 `pendingUserAttrs`
- [x] Synthesis — `AttributeFactorySynthesizer.ProcessClass` 遍历字段合成工厂
- [x] IR Codegen — `IrFieldDesc.Attributes`；`EmitClassDesc` 填充
- [x] zbc 序列化 — ZbcWriter/Reader 字段记录 attr refs + 版本 bump
- [x] VM 加载 — `read_type` → `FieldDesc.attributes`
- [x] VM interp — `__field_custom_attributes` builtin
- [x] stdlib — `FieldInfo.GetCustomAttributes()` / `GetAttribute(Type)`
