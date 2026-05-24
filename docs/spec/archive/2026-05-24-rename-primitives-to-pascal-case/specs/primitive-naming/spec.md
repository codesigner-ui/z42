# Spec: primitive-naming — BCL-style PascalCase

## ADDED Requirements

### Requirement: stdlib primitive struct 名采用 PascalCase BCL 等价标识符

#### Scenario: 各 primitive struct 类型名匹配 BCL 命名
- **WHEN** 读取 `src/libraries/z42.core/src/Primitives/<File>.z42`
- **THEN** struct 名为 BCL 对应名（`Boolean / Char / SByte / Int16 / Int32 / Int64 / Byte / UInt16 / UInt32 / UInt64 / Single / Double`），文件名 = struct 名 + `.z42`

#### Scenario: keyword 用户代码不受影响
- **WHEN** 用户写 `int x = 5; var s = x.ToString();`
- **THEN** 编译通过；运行时 dispatch 到 `Std.Int32.ToString`，`s == "5"`

#### Scenario: 静态方法 keyword 调用形式仍合法
- **WHEN** 用户写 `int.Parse("42")`
- **THEN** TypeChecker 将 `int` keyword 解析到 `Std.Int32.Parse` 静态方法，返回 `42`

#### Scenario: 全部 12 primitive 都支持 keyword 调用形式
- **WHEN** 用户依次写以下调用：
  - `bool.Parse("true")` / `bool` 实例 `.ToString()`
  - `char.IsWhiteSpace(' ')` (实例) / `char` 实例 `.ToLower()` `.ToUpper()`
  - `i8.Parse("-1") / i16.Parse / int.Parse / long.Parse`
  - `u8.Parse / u16.Parse / u32.Parse / u64.Parse`
  - `float.Parse / double.Parse`
- **THEN** 全部解析到对应 PascalCase struct 的静态或实例方法，正确返回

#### Scenario: 字段访问（Static const）通过 keyword 形式
- **WHEN** 用户写 `int.MaxValue`（如未来补充该字段；当前 stdlib 暂无 MaxValue 但模型相同）
- **THEN** keyword `int` 通过 alias 解析到 `Std.Int32`，访问其静态 const 字段；同样适用于 `Std.Int32.MaxValue` 等价形式

### Requirement: BCL-class FQN 形式也合法

#### Scenario: PascalCase FQN 等价 keyword 形式
- **WHEN** 用户写 `Std.Int32.Parse("42")` 或在 `using Std;` 下写 `Int32.Parse("42")`
- **THEN** 与 `int.Parse("42")` 完全等价（同一 func_index 被 dispatch），结果均为 `42`

#### Scenario: Interface impl 跨 keyword/PascalCase 形式一致
- **WHEN** struct 声明形如 `public struct Int32 : IComparable<int>, INumber<int>`，TypeChecker 解析 generic 实参 `<int>`
- **THEN** `Z42PrimType("int")` 实例化路径与"等价于 `Std.Int32`"的查找结果一致；`Z42InstantiatedType` 在 vtable lookup 时正确 dispatch 到 `Std.Int32` 的成员

### Requirement: VM dispatch 通过更新的 STD_* 常量路由

#### Scenario: VCall 对 `Value::I32(5)` 调用 `.ToString()`
- **WHEN** VM 接收 `VCall ToString` on `Value::I32(5)`
- **THEN** 通过 `STD_INT32 = "Std.Int32"` 解析到 `Std.Int32.ToString` func_index，返回 `Value::String("5")`

#### Scenario: 12 primitive 都有对应 STD_* 常量
- **WHEN** 检查 `src/runtime/src/metadata/well_known_names.rs`
- **THEN** 至少包含 12 个 `STD_*` 常量：`STD_BOOLEAN / STD_CHAR / STD_SBYTE / STD_INT16 / STD_INT32 / STD_INT64 / STD_BYTE / STD_UINT16 / STD_UINT32 / STD_UINT64 / STD_SINGLE / STD_DOUBLE`，值为对应 `"Std.<Name>"`

#### Scenario: `exec_vcall.rs::primitive_class_name()` 对 12 个 Value variant 都路由
- **WHEN** VM 接收 VCall on `Value::Bool / Char / I8 / I16 / I32 / I64 / U8 / U16 / U32 / U64 / F32 / F64`
- **THEN** 各自路由到对应 `STD_*` 常量；无 fallthrough panic

## MODIFIED Requirements

### Requirement: TypeChecker `BindMemberCallOnUnknownTarget` keyword→class 映射

**Before:** switch 仅 5 条（`string → Std.String / int → Std.int / double → Std.double / bool → Std.bool / char → Std.char`），narrow int + long + float 走 fallback `_ => tgtName`，依赖 stdlib struct 名等于 keyword

**After:** switch 13 条（含 string），全部映射到 PascalCase：
```
"string" => "Std.String"
"bool"   => "Std.Boolean"
"char"   => "Std.Char"
"i8"     => "Std.SByte"
"i16"    => "Std.Int16"
"int"    => "Std.Int32"
"long"   => "Std.Int64"
"u8"     => "Std.Byte"
"u16"    => "Std.UInt16"
"u32"    => "Std.UInt32"
"u64"    => "Std.UInt64"
"float"  => "Std.Single"
"double" => "Std.Double"
_        => tgtName       // 用户自定义类型 / PascalCase FQN 直传
```

### Requirement: Native binding 命名

**Before:** `[Native("__int_parse")] / [Native("__bool_to_string")] / [Native("__i8_parse")]` 等

**After:** Native binding 字符串 key 与 BCL 名称对齐：
- `__int_*`     → `__int32_*`
- `__long_*`    → `__int64_*`
- `__i8_*`      → `__sbyte_*`
- `__i16_*`     → `__int16_*`
- `__u8_*`      → `__byte_*`
- `__u16_*`     → `__uint16_*`
- `__u32_*`     → `__uint32_*`
- `__u64_*`     → `__uint64_*`
- `__bool_*`    → `__boolean_*`
- `__float_*`   → `__single_*`
- `__char_*`    → `__char_*`（不变）
- `__double_*`  → `__double_*`（不变）

Rust 端 `corelib/*.rs` 中 `builtin_int_*` 等函数名同步 rename 到 `builtin_int32_*` 等。

## Pipeline Steps

受影响的 pipeline 阶段：
- [ ] Lexer — 无变化（keyword token 不变）
- [ ] Parser / AST — 无变化（struct 名是普通 identifier，PascalCase 已合法）
- [x] TypeChecker — `BindMemberCallOnUnknownTarget` switch 表 + primitive→struct 名解析
- [x] IR Codegen — 仅 zbc 内嵌字符串值变化（class name field），wire format 不动
- [x] VM interp — `exec_vcall.rs::primitive_class_name()` 路由 + `well_known_names.rs` 常量
- [x] VM corelib — BUILTINS 表 + `builtin_*` 函数名

## IR Mapping

无新 IR 指令。已有指令引用的 class name 字符串值变化（生效靠 stdlib regen）：
- `VCall Std.int.X` zbc 编码 → `VCall Std.Int32.X`（ConstStrPool 项目变化）
- 用户 zpkg 引用 `IComparable<int>` 时，编译期解析仍走 `Z42PrimType("int")` 内部 ID（不变），仅最终 dispatch 阶段映射到新 PascalCase struct

## 验证规模

- **单元测试**：C# TypeChecker keyword→class 映射 13 条全覆盖；Rust VM dispatch 12 primitive × （ToString / Equals / GetHashCode / 静态方法）路径
- **Golden**：`./scripts/test-all.sh --scope=full` 全绿（含 cross-zpkg + stdlib dogfood + dist regen）
- **regen**：`generate-fixtures.sh`（zbc + zpkg）跑通；`regen-golden-tests.sh` 完成；diff 显示字符串改动符合预期
- **静态检查**：grep `Std\.\(int\|bool\|char\|float\|double\|long\|i8\|i16\|u8\|u16\|u32\|u64\)\b` 在所有 Scope 文件中应为零；`docs/spec/archive/**` 不算（历史记录保留原貌）
