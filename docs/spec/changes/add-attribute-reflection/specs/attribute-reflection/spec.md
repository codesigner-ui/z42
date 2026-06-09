# Spec: 用户自定义 Attribute + 反射

## ADDED Requirements

### Requirement: 声明 attribute 类

#### Scenario: 继承 Std.Attribute
- **WHEN** 用户写 `class Route : Attribute { public string Path; public Route(string path) { this.Path = path; } }`
- **THEN** 编译通过；`Route` 是合法 attribute 类（直接/间接派生 `Std.Attribute`）

#### Scenario: 非 attribute 类用作 attribute → 报错
- **WHEN** `class Plain {}` 后写 `[Plain] class X {}`
- **THEN** 编译错误（E09xx：`Plain` 不派生 `Std.Attribute`，不能用作 attribute）

### Requirement: 应用 attribute（class / method）

#### Scenario: 按真实类名应用（无后缀魔法）
- **WHEN** `[Route("/users")] class UsersController {}`
- **THEN** 编译通过；`UsersController` 携带一个 `Route` attribute。**不**接受 `[RouteAttribute]`（无后缀约定）

#### Scenario: 命名参数 + 默认值走单一 ctor 路径
- **WHEN** attribute `Route(string path, string method = "GET")`，应用 `[Route("/u", method: "POST")]`
- **THEN** 绑定到构造器（path="/u", method="POST"）；无"旁路 public 字段直写"路径

#### Scenario: 非常量参数 → 报错
- **WHEN** `[Route(someVariable)]`（someVariable 非编译期常量）
- **THEN** 编译错误（E09xx：attribute 参数须为编译期常量——字面量 / enum 成员 / typeof）

#### Scenario: 无匹配构造器 → 报错
- **WHEN** attribute 的参数与任何构造器签名都不匹配
- **THEN** 编译错误（E09xx：无匹配构造器），复用既有 ctor 重载解析 + named-arg 绑定诊断

#### Scenario: method 上的 attribute
- **WHEN** `[Doc("lists users")] public void List() {}`
- **THEN** 该方法携带一个 `Doc` attribute，可经 `MethodInfo` 反射

### Requirement: 反射读取 attribute（活实例）

#### Scenario: GetCustomAttributes 返回活实例
- **WHEN** `Type t = typeof(UsersController); Attribute[] attrs = t.GetCustomAttributes();`
- **THEN** `attrs` 含一个 `Route` 实例；`((Route)attrs[0]).Path == "/users"`（实例字段可读，非字符串/字典）

#### Scenario: 缓存单例（同一实例）
- **WHEN** 对同一 `Type` 连续两次 `GetCustomAttributes()`
- **THEN** 两次返回的对应 attribute 是**同一实例**（首次实例化后缓存；不每次重建）

#### Scenario: GetAttribute(Type) 单查
- **WHEN** `Attribute? r = t.GetAttribute(typeof(Route));`
- **THEN** 存在则返回该 `Route` 实例，不存在返回 `null`

#### Scenario: 跨 zpkg 反射
- **WHEN** 包 A 定义 `[Route("/x")] class C`，包 B 导入 C 并 `typeof(C).GetCustomAttributes()`
- **THEN** B 能取得 `Route` 实例（attribute 持久化进 A 的 .zbc/.zpkg；工厂 func 跨模块可解析）

## IR / 元数据 Mapping

- **无新 IR 指令**。每个 `[Foo(args)]` → 编译期合成无参工厂 `Std.Attribute __attr_factory_N() { return new Foo(args); }`（普通 FunctionDecl，正常 IR + func_index）。
- **元数据新增**：`ExportedClassDef` / `ExportedMethodDef` 加 `Attributes: List<ExportedAttributeRef>`，每项 `{ TypeName, FactoryFunc }`。
- **格式 bump**：`ZbcWriter.VersionMinor` 9→10、`ZpkgWriter.VersionMinor` 11→12、runtime `ZBC_VERSION_MINOR` 同步（见 version-bumping.md checklist）。

## Pipeline Steps

- [ ] Lexer —（不涉及；`[` `]` token 已有）
- [ ] Parser / AST — `AttributeApp` 节点 + class/method 收集
- [ ] TypeChecker — `AttributeBinder`：attribute 类校验 + ctor 解析 + 常量参数
- [ ] IR Codegen — 工厂合成（`AttributeFactorySynthesizer`）+ 元数据 emit
- [ ] zbc/zpkg — attribute refs 序列化 + 版本 bump
- [ ] VM interp — 载入 attribute refs + `builtin_type_custom_attributes`（调工厂 + 缓存）
- [ ] stdlib — `Std.Attribute` + Type/MethodInfo 反射方法
