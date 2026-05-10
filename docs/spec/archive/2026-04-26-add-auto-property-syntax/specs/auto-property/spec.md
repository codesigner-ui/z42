# Spec: Auto-property 语法 (class + interface + extern)

## ADDED Requirements

### Requirement: Class auto-property 解析

类 body 内 `<vis>? T Name { get; [set;] }` 解析为：

1. 隐藏 backing field：`private T __prop_<Name>;`
2. Getter method：`<vis> T get_<Name>() { return this.__prop_<Name>; }`
3. Setter method（仅当 `set;` 出现）：
   `<vis> void set_<Name>(T value) { this.__prop_<Name> = value; }`

#### Scenario: get-set property
- **GIVEN** `class Box { public int Width { get; set; } }`
- **WHEN** 编译
- **THEN** Box 类生成 `__prop_Width: int` field + `get_Width(): int` method +
  `set_Width(value: int): void` method

#### Scenario: get-only property
- **GIVEN** `class Box { public int Width { get; } }`
- **WHEN** 编译
- **THEN** Box 类生成 `__prop_Width: int` field + `get_Width(): int` method
- **AND** **不生成** `set_Width`（readonly）
- **AND** 用户代码 `box.Width = 5` 编译报错（找不到 setter）

#### Scenario: 多 property + 普通字段共存
- **GIVEN**
  ```z42
  class Foo {
      public int A { get; set; }
      public string Name;
      public bool B { get; }
  }
  ```
- **THEN** Foo 含 fields：`__prop_A: int` / `Name: string` / `__prop_B: bool`
- **AND** methods：`get_A` / `set_A` / `get_B`
- **AND** 用户代码 `f.Name = "x"` 走普通 field assignment（不被 property 识别）

### Requirement: Interface property 解析

接口 body 内 `T Name { get; [set;] }` 解析为方法签名（无 body，无 backing field）：

```
T get_<Name>();
void set_<Name>(T value);    // 仅当 set; 出现
```

#### Scenario: IEnumerator.Current 标准形式
- **GIVEN** `interface IEnumerator<T> : IDisposable { bool MoveNext(); T Current { get; } }`
- **WHEN** 编译
- **THEN** IEnumerator 接口含方法签名 `MoveNext(): bool` 和 `get_Current(): T`
- **AND** 实现类必须提供 `get_Current()` 方法或 auto-property `T Current { get; }`

#### Scenario: get-set interface property
- **GIVEN** `interface IFoo { int X { get; set; } }`
- **THEN** IFoo 含 `get_X(): int` + `set_X(int): void` 两个方法签名

### Requirement: Extern property

```z42
public extern T Name { get; }    // 仅 getter
public extern T Name { get; set; }  // getter + setter（罕见）
```

desugar 为 extern 方法签名：

```z42
public extern T get_Name();
public extern void set_Name(T value);   // 仅当 set; 出现
```

#### Scenario: extern getter
- **GIVEN** `public class String { public extern int Length { get; } }`
- **WHEN** 编译
- **THEN** String 含 `extern int get_Length()` 方法签名（无 body）
- **AND** 仍受现有 `[Native("__intrinsic")]` 注解配合（若需要 VM 绑定）

### Requirement: 用户代码 property 访问

`obj.PropName` 表达式 / 赋值在 TypeChecker binding 阶段：

#### Scenario: property 读 desugar 为方法调用
- **GIVEN** Foo 含 auto-property `int X { get; set; }`
- **WHEN** 用户代码 `var v = foo.X;`
- **THEN** TypeChecker 把 `foo.X` 视为 `foo.get_X()` 调用，类型为 X 的类型
- **AND** Codegen emit VCall 调用 get_X

#### Scenario: property 写 desugar 为方法调用
- **WHEN** 用户代码 `foo.X = 42;`
- **THEN** TypeChecker 把赋值视为 `foo.set_X(42)`
- **AND** Codegen emit VCall 调用 set_X

#### Scenario: readonly property 不能赋值
- **GIVEN** Foo 含 `int Y { get; }`（无 setter）
- **WHEN** 用户代码 `foo.Y = 5;`
- **THEN** 编译报错（找不到 set_Y 方法）

#### Scenario: property 优先于同名字段
- **GIVEN** 类同时声明同名字段 + auto-property（应是错误用例，但编译器需稳定）
- **THEN** 优先使用 property（method-based access）；字段被 shadow

### Requirement: backing field 不对外暴露

`__prop_<Name>` 字段 visibility 强制为 `private`，且 stdlib 之外的代码访问
该字段名报错或返回 unknown。

#### Scenario: 外部代码访问 backing field
- **GIVEN** Box 类含 auto-property `Width { get; set; }`
- **WHEN** 外部代码 `box.__prop_Width`
- **THEN** TypeChecker 报错（field private 不可访问；或 unknown member）

### Requirement: SkipAutoPropBody 完全移除

修复后，`TopLevelParser.cs` / `TopLevelParser.Helpers.cs` 中的
`SkipAutoPropBody` 函数不再被任何路径调用（保留为 dead code 待 PR review
确认或直接删除）。

#### Scenario: 验证 dead code
- **WHEN** PR review 时 `git grep "SkipAutoPropBody"`
- **THEN** 仅 declaration 一处；调用处全部清零（或函数已删）

## MODIFIED Requirements

### Requirement: BoundMember 语义

**Before:** `BoundMember` 始终表示**字段访问**，Codegen 生成 `FieldGet` /
`FieldSet`。

**After:** TypeChecker binding 阶段对 `obj.Name` 的成员访问：
- 类有 `get_<Name>` 方法 → 转换为 `BoundCall(receiver=obj, method=get_<Name>, args=[])`
- 否则 → 保持 `BoundMember`（普通字段访问）

赋值 `obj.Name = v` 同样：
- 类有 `set_<Name>` 方法 → 转换为 `BoundCall(receiver=obj, method=set_<Name>, args=[v])`
- 否则 → 保持普通字段写

### Requirement: IEnumerator.Current 升级

**Before:** Wave 2 退化为 `T Current()` 方法。
**After:** 升级回 `T Current { get; }` property 形式。stdlib `IEnumerator.z42`
源文件更新；端到端验证 property 全 pipeline 工作。

## IR Mapping

不引入新 IR 指令；property 通过 desugar → backing field + getter/setter
方法 → 现有 FieldGet/Set + Call/VCall 指令实现。

## Pipeline Steps

- [x] **Lexer** — 不涉及（`get`/`set` 已是 identifier）
- [x] **Parser** — 替换 SkipAutoPropBody 为完整 desugar（3 处：class / interface / extern）
- [x] **TypeChecker** — BoundMember binding 识别 property，转方法调用；assign target 同
- [ ] IR Codegen — 不涉及（desugar 后已是方法调用）
- [ ] VM — 不涉及
- [x] **stdlib 升级** — IEnumerator.Current 回 property 形式（端到端验证）
