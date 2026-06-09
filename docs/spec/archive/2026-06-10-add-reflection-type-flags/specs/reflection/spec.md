# Spec: Reflection — Type.IsAbstract / IsSealed

## ADDED Requirements

### Requirement: Type.IsAbstract

`Type.IsAbstract` 返回该类型是否声明为 `abstract`。

#### Scenario: 抽象类
- **WHEN** `abstract class Shape { }`，`typeof(Shape).IsAbstract`
- **THEN** `true`

#### Scenario: 普通类
- **WHEN** `class Circle { }`，`typeof(Circle).IsAbstract`
- **THEN** `false`

#### Scenario: handle-less 类型
- **WHEN** 对基础类型 / 数组的 Type（`typeof(int)`）调用
- **THEN** `false`（无句柄 → 默认 flags=0，绝不 bail）

### Requirement: Type.IsSealed

`Type.IsSealed` 返回该类型是否声明为 `sealed`。

#### Scenario: sealed 类
- **WHEN** `sealed class Token { }`，`typeof(Token).IsSealed`
- **THEN** `true`

#### Scenario: 非 sealed 类
- **WHEN** `class Open { }`，`typeof(Open).IsSealed`
- **THEN** `false`

### Requirement: 类修饰符持久化进 zbc TYPE section

每个类在 zbc TYPE section 记一个 `flags: u8`（bit0 abstract / bit1 sealed / bit2 struct / bit3 record），运行期 loader 载入 `TypeDesc.class_flags`。

#### Scenario: 往返一致
- **WHEN** `abstract class A`、`sealed class S`、`struct V`、`record R` 编译→zbc→VM 加载
- **THEN** `A.class_flags` bit0=1；`S` bit1=1；`V` bit2=1；`R` bit3=1；普通类 flags=0

#### Scenario: 继承不传播修饰符
- **WHEN** `class D : A`（A 抽象），`typeof(D).IsAbstract`
- **THEN** `false`（修饰符是声明级，不随继承传播）

## MODIFIED Requirements

### Requirement: zbc / zpkg 格式版本

**Before:** zbc 1.11 / zpkg 0.13（TYPE section 无 class flags）。
**After:** zbc 1.12 / zpkg 0.14（TYPE section 每类追加 `flags: u8`）。strict-pin：旧版本 zbc 不可读，全量 fixture regen。

## IR Mapping

- 无新 IR 指令。**zbc TYPE section wire 变更**：每类记录末尾追加 `flags: u8`（在 attr 块之后）。
- 版本：zbc minor 11→12，zpkg minor 13→14（联动）。

## Pipeline Steps

- [ ] Lexer / Parser —（不涉及；`ClassDecl.IsAbstract`/`IsSealed` 已有）
- [x] IR Codegen — `IrGen.EmitClassDesc` 填 flags；`IrClassDesc` 新字段
- [x] zbc 序列化 — ZbcWriter 写 / ZbcReader 读 flags 字节 + 版本 bump
- [x] VM 加载 — `read_type` → `ClassDesc.class_flags` → `build_type_registry` → `TypeDesc.class_flags`
- [x] VM interp — `__type_is_abstract` / `__type_is_sealed` builtin
- [x] stdlib — `Type.IsAbstract` / `Type.IsSealed`
