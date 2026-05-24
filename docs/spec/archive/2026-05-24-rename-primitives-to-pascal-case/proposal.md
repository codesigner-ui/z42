# Proposal: rename primitive types to BCL-style PascalCase

> 状态：🟡 进行中 | 创建：2026-05-24 | 类型：lang

## Why

z42 stdlib 当前 12 个 primitive 类型用小写 keyword 作 struct 名（`public struct int / bool / char / i8 / u8 / ...`），文件命名 `Int.z42` 与 `I8.z42` 不统一。

这与 [naming-conventions.md §1](../../../design/language/naming-conventions.md)（类型 PascalCase）+ §28（文件名匹配类型名 PascalCase）冲突；当前依赖 `naming-conv-4` Deferred 给的临时豁免（"primitive 是 keyword，不受规范管辖"）。

dotnet BCL 范式（`System.Int32` / `System.Boolean` / `System.SByte` / ...）经过 20+ 年验证：

- 用户代码层 keyword `int / bool / char / i8 / ...` 仍可写（C# 等价）
- stdlib struct 名全 PascalCase（`Int32 / Boolean / SByte / ...`）
- 文件名 = struct 名（`Int32.cs / Boolean.cs / SByte.cs`）

z42 跟进此约定，把当前 Deferred 例外升级为正式规则。

## What Changes

1. 12 个 primitive struct 从小写 keyword 名 rename 到 PascalCase BCL 等价名（`bool → Boolean`、`int → Int32`、`i8 → SByte` 等，完整映射见 §映射）
2. 12 个文件 rename 配对（`Bool.z42 → Boolean.z42`、`Int.z42 → Int32.z42`、`I8.z42 → SByte.z42` 等）
3. TypeChecker `BindMemberCallOnUnknownTarget` switch 从 5 条扩到 12 条 keyword→class alias 映射；Z42PrimType 内部 ID 保持小写不动
4. VM `well_known_names.rs` 6 个常量字符串更新，**Design phase 决定**是否补 6 个新常量（narrow int + long + float）
5. Native binding 名跟进（`__int_parse → __int32_parse`、`__bool_to_string → __boolean_to_string`、`__i8_parse → __sbyte_parse` 等），Rust 端 `builtin_*` 函数名同步
6. naming-conventions.md `naming-conv-4` 从 Deferred 段移除，§1 / §10 加正式条款"primitive 类型名 PascalCase（BCL 风格），keyword 是源代码 alias"
7. 6 个 design doc 中提到 `Std.int / Std.bool` 的示例同步大写

## 映射

| z42 keyword | 当前 struct | 当前文件 | → 新 struct | 新文件 | Native binding 前缀 |
|:----------:|:----------:|:------:|:---------:|:-----:|:-----------------:|
| `bool` | `bool` | `Bool.z42` | `Boolean` | `Boolean.z42` | `__bool_` → `__boolean_` |
| `char` | `char` | `Char.z42` | `Char` | `Char.z42` | `__char_` → `__char_`（不变）|
| `i8` | `i8` | `I8.z42` | `SByte` | `SByte.z42` | `__i8_` → `__sbyte_` |
| `i16` | `i16` | `I16.z42` | `Int16` | `Int16.z42` | `__i16_` → `__int16_` |
| `int` | `int` | `Int.z42` | `Int32` | `Int32.z42` | `__int_` → `__int32_` |
| `long` | `long` | `Long.z42` | `Int64` | `Int64.z42` | `__long_` → `__int64_` |
| `u8` | `u8` | `U8.z42` | `Byte` | `Byte.z42` | `__u8_` → `__byte_` |
| `u16` | `u16` | `U16.z42` | `UInt16` | `UInt16.z42` | `__u16_` → `__uint16_` |
| `u32` | `u32` | `U32.z42` | `UInt32` | `UInt32.z42` | `__u32_` → `__uint32_` |
| `u64` | `u64` | `U64.z42` | `UInt64` | `UInt64.z42` | `__u64_` → `__uint64_` |
| `float` | `float` | `Float.z42` | `Single` | `Single.z42` | `__float_` → `__single_` |
| `double` | `double` | `Double.z42` | `Double` | `Double.z42` | `__double_` → `__double_`（不变）|

## Scope（允许改动的文件）

### stdlib `.z42`（13 文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.core/src/Primitives/Bool.z42` | RENAME + MODIFY | → `Boolean.z42`；struct 名 + Native binding |
| `src/libraries/z42.core/src/Primitives/Char.z42` | MODIFY | struct 名（文件名 OK） |
| `src/libraries/z42.core/src/Primitives/Int.z42` | RENAME + MODIFY | → `Int32.z42` |
| `src/libraries/z42.core/src/Primitives/Long.z42` | RENAME + MODIFY | → `Int64.z42` |
| `src/libraries/z42.core/src/Primitives/I8.z42` | RENAME + MODIFY | → `SByte.z42` |
| `src/libraries/z42.core/src/Primitives/I16.z42` | RENAME + MODIFY | → `Int16.z42` |
| `src/libraries/z42.core/src/Primitives/U8.z42` | RENAME + MODIFY | → `Byte.z42` |
| `src/libraries/z42.core/src/Primitives/U16.z42` | RENAME + MODIFY | → `UInt16.z42` |
| `src/libraries/z42.core/src/Primitives/U32.z42` | RENAME + MODIFY | → `UInt32.z42` |
| `src/libraries/z42.core/src/Primitives/U64.z42` | RENAME + MODIFY | → `UInt64.z42` |
| `src/libraries/z42.core/src/Primitives/Float.z42` | RENAME + MODIFY | → `Single.z42` |
| `src/libraries/z42.core/src/Primitives/Double.z42` | MODIFY | struct 名（文件名 OK） |
| `src/libraries/z42.core/src/Array.z42` | MODIFY | 注释中 `Std.int / Std.String` 提及 |

### C# 编译器（2 文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Calls.cs` | MODIFY | `BindMemberCallOnUnknownTarget` switch 扩到 12 条 |
| `src/compiler/z42.Semantics/TypeCheck/ImportedSymbolLoader.Phase3.cs` | MODIFY | 注释中 `Std.int` FQ 示例 |

### Rust VM（7 文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/metadata/well_known_names.rs` | MODIFY | 6 个 STD_* 字符串更新 + 新增 6 个常量（narrow int + long + float）—— 最终条数由 Design phase 定 |
| `src/runtime/src/interp/exec_vcall.rs` | MODIFY | `primitive_class_name()` 路由扩展到 12 类型（Design phase 定增量）|
| `src/runtime/src/corelib/mod.rs` | MODIFY | BUILTINS 表条目 rename + Rust 函数引用更新 |
| `src/runtime/src/corelib/convert.rs` | MODIFY | `builtin_int_*` → `builtin_int32_*` 等函数名 + 内部错误消息字符串 |
| `src/runtime/src/corelib/convert_tests.rs` | MODIFY | 调用 builtin 字符串 key 同步 |
| `src/runtime/src/corelib/char.rs` | MODIFY | 错误消息字符串中的 `__char_*` 不变（char 不改）—— 标 MODIFY 留余地 |
| `src/runtime/src/corelib/tests.rs` | MODIFY | 测试中 builtin key 字符串（主要 `__char_*` 不变，但留余地）|

### 测试（1 文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/tests/generics/generic_inumber.z42` | MODIFY | 使用 `Std.int` FQN 引用 |

### 文档（7 文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `docs/design/language/naming-conventions.md` | MODIFY | naming-conv-4 升级；§1 / §10 加 primitive 条款 |
| `docs/design/language/object-protocol.md` | MODIFY | 示例 `Std.int / Std.double` 同步大写 |
| `docs/design/language/generics.md` | MODIFY | 同上 |
| `docs/design/language/static-abstract-interface.md` | MODIFY | 同上 |
| `docs/design/language/arrays.md` | MODIFY | 同上 |
| `docs/design/language/interop.md` | MODIFY | 同上 |
| `docs/design/compiler/compiler-architecture.md` | MODIFY | 多处 `Std.int` 示例 |

### Regen artifacts（不入 commit）

| 路径 | 变更类型 | 说明 |
|------|---------|------|
| `src/libraries/*/build/*.zpkg` | REGEN | stdlib `.zpkg` 重编译；不入 git，由 build 流程产 |
| `src/tests/zbc-format/fixtures/**` | REGEN | 跑 `generate-fixtures.sh`；diff 显示 class name 字符串变化 |
| `src/tests/zpkg-format/fixtures/**` | REGEN | 同上 |

### 只读引用（理解上下文必须读，但不修改）

- `/Users/d.s.qiu/Documents/codesigner-ui/runtime/src/libraries/System.Private.CoreLib/src/System/*.cs` — dotnet BCL 命名 reference
- `src/compiler/z42.Semantics/TypeCheck/Z42Type.cs` — 确认 `Z42PrimType` 内部 ID 保持小写
- `src/compiler/z42.Semantics/TypeCheck/TypeRegistry.cs` — 现有 keyword alias 数据表
- `docs/design/runtime/zbc.md` / `zpkg.md` — 确认 wire format 不需 minor bump

## Out of Scope

- **不改 keyword 本身**：`int / long / bool / char / float / double / i8 / i16 / u8 / u16 / u32 / u64` 仍是 z42 语言关键字；用户代码 `int x = 5` 不受影响
- **不引入 zbc / zpkg minor bump**：wire format 不变（只是其中存的字符串变了），strict-pin 不触发；但 stdlib `.zpkg` 必须 regen
- **不改 `Z42PrimType` 内部 ID**：仍 `"int" / "bool"` 等小写，避免上下游震荡
- **不动 B1–B4（其他规范违规）**：留给独立 spec `fix-stdlib-naming-violations` 处理
- **不改 `String` / `Array` / `Object`**：这些已是 PascalCase，本变更不动
- **不创建 alias / deprecation 路径**：直接 rename（[philosophy.md 不为旧版本提供兼容](../../../../.claude/rules/philosophy.md#不为旧版本提供兼容2026-04-26-强化)）

## Open Questions

- [ ] Q1：narrow int types（`i8 / i16 / u8 / u16 / u32 / u64`）在 `well_known_names.rs` 当前**未注册**STD_* 常量，今天 VCall 走什么路径？Design phase 调研后决定（很可能需要补 6 个常量 + `exec_vcall.rs` 的 6 个 case）
- [ ] Q2：commit 粒度 —— 一次性大 commit（13 stdlib + 7 VM + 7 docs 同提交）还是按 layer 切分（stdlib commit / VM commit / docs commit）？倾向一次性，因为中间状态不能跑（rename 必须原子）
