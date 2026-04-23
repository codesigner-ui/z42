# Tasks: primitive-as-struct（C# 风格 primitive 接口实现）

> 状态：🟢 已完成 | 完成：2026-04-23

**变更说明：** 参考 C# BCL 模式，在 stdlib 里以 `struct int : IComparable<int>, IEquatable<int> { ... }` 等声明让 primitive 类型实现接口。把 `PrimitiveImplementsInterface` 硬编码 switch 和 `primitive_method_builtin` 硬编码路由**替换为数据驱动**：TypeChecker 查 stdlib 的 class interface 列表；VM 查 module 的 `Std.{primname}.{method}` 函数表。

**原因：** 当前 z42 的 primitive → interface 桥接是两张 C# / Rust 侧的硬编码 switch 表，不通过语言机制声明。消除硬编码的同时也为未来加 INumber 等接口打下"只改 stdlib"的干净路径。

## 任务

### 阶段 1：Parser 接受 primitive 关键字作声明名 ✅
- [x] 1.1 `IsPrimitiveTypeKeyword(TokenKind)` 辅助函数
- [x] 1.2 `ExpectTypeDeclName` 接受 Identifier 或 primitive keyword
- [x] 1.3 `ParseClassDecl` 使用新助手
- [x] 1.4 Parser 单元测试（`struct int { }` / `struct double : IComparable<double>`）

### 阶段 2：stdlib 新增 primitive struct 声明 ✅
- [x] 2.1 `Int.z42`: `struct int : IComparable<int>, IEquatable<int>` + Parse/CompareTo/Equals/GetHashCode/ToString
- [x] 2.2 `Double.z42`: 同上模式
- [x] 2.3 `Bool.z42` 新建: `struct bool : IEquatable<bool>`
- [x] 2.4 `Char.z42` 新建: `struct char : IComparable<char>, IEquatable<char>`
- [x] 2.5 `String.z42`: class String 追加 `: IComparable<string>, IEquatable<string>` + CompareTo(string)/Equals(string) 方法
- [x] 2.6 删除 SymbolCollector "struct cannot implement interfaces" 硬性禁令（C# parity）
- [x] 2.7 `./scripts/build-stdlib.sh` 产出新 zpkg
- [x] 2.8 `int` / `double` helper class 并入 struct（删除 `class Int` / `class Double` 的 Parse 重复声明；Parse 现在是 `struct int` 的静态方法）

### 阶段 3：TypeChecker 数据驱动 ✅
- [x] 3.1 `PrimitiveImplementsInterface` 改为查 `_symbols.ClassInterfaces[canonical]`
- [x] 3.2 别名规范化：i8/i16/i32/sbyte/short/byte/ushort/uint → int；i64/ulong/u64 → long；f32 → float；string → String
- [x] 3.3 `TypeChecker.Calls.cs` 静态调用映射更新：`int → Std.int`、`double → Std.double`、`bool → Std.bool`、`char → Std.char`（均小写 struct；string 保留 `Std.String`）
- [x] 3.4 `ImportedSymbols.ClassInterfaces` 新增字段 + `ImportedSymbolLoader.Load` 填充 + `SymbolCollector.MergeImported` 消费
- [x] 3.5 `ExportedTypeExtractor` 写入 class 的 interface 列表到 TSIG（此前 `[]` 硬编码）
- [x] 3.6 `SemanticModel.ClassInterfaces` 新增字段（接通 SymbolTable → SemanticModel → Extractor）

### 阶段 4：VM 数据驱动 ✅
- [x] 4.1 `primitive_method_builtin` 重命名为 `primitive_class_name` — Value 变体→qualified class name 映射（5 条）
- [x] 4.2 VCall 路径重写：primitive receiver 构造 `{class}.{method}` 函数名，走 module.func_index 正常函数调用
- [x] 4.3 JIT `helpers_object.rs` 同步改走类方法派发
- [x] 4.4 删除 17 条硬编码 `(Value, method) → builtin_name` 映射

### 阶段 5：清理 + 验证 ✅
- [x] 5.1 相关 TypeCheckerTests 更新（inline-declare 主要 primitive structs 以解耦 stdlib）
- [x] 5.2 `TsigConstraintsTests.ImportedConstraint_AcceptsConformingTypeArg` 同步新 `ClassInterfaces` 字段
- [x] 5.3 GREEN：550 编译器测试 + 162 VM interp+jit 全绿
- [x] 5.4 `scripts/test-dist.sh` 端到端（packaged binaries）162 全绿

### 阶段 6：文档 + 归档 ✅
- [x] 6.1 `docs/design/generics.md` 新增 "primitive-as-struct" 小节（动机 / stdlib 示例 / 三层改动对比表 / 类型身份保守 / 未来新接口路径）
- [x] 6.2 `docs/roadmap.md` L3-G4b 描述更新为"数据驱动、stdlib struct 驱动"
- [x] 6.3 `src/libraries/z42.core/README.md` 列出新 struct 文件 + 设计小节
- [x] 6.4 归档本 openspec change

## 备注

- **类型身份保守**：`Z42PrimType("int")` 仍然是 int 在类型系统里的身份，没切成 `Z42ClassType`。避免 `IsAssignableTo` / `IsReferenceType` / 全局 `== Z42Type.Int` 比较大面积审计。仅"接口实现查询"和"方法派发查询"两条路径数据驱动化
- **struct 接口实现禁令解除**：`struct Foo : IBar` 现在 C# parity 允许；此前硬性禁止是过度保守
- **别名规范化**：i32/i64/i8/... 不生成独立 stdlib struct；`PrimitiveImplementsInterface` 通过规范化表把它们归一到 int/long/double，复用 struct int 等的接口声明
- **String 保留 uppercase class**：`class String` 已存在 stdlib 且有大量方法；仅追加 `: IComparable<string>, IEquatable<string>` + 两个方法，避免大规模重构。future iteration 可考虑合并到 `struct string`
- **下一步 INumber**：给 struct int 等加 `, INumber<int>` + 5 个 extern 方法即可；编译器 / VM 零改动
