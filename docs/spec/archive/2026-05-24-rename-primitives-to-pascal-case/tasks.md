# Tasks: rename primitives to BCL PascalCase

> 状态：🟢 已完成 | 创建：2026-05-24 | 完成：2026-05-24 | 类型：lang
> Spec 类型：完整流程（lang —— 引入 keyword→class alias 映射机制）

## 进度概览
- [x] 阶段 1: TypeRegistry 扩展 + 行为保持
- [x] 阶段 2: Rust well_known_names.rs 加新常量
- [x] 阶段 3: stdlib 12 文件 rename + Native binding + corelib Rust 函数
- [x] 阶段 4: well_known_names 清理 + exec_vcall.rs 更新
- [x] 阶段 5: 跨包引用 + 文档同步 + naming-conventions.md 升级
- [x] 阶段 6: 验证全绿 + 静态检查

## 阶段 1: TypeRegistry 扩展（行为保持）

- [x] 1.1 `src/compiler/z42.Semantics/TypeCheck/TypeRegistry.cs` — `TypeEntry` 加 `StdlibClassName` nullable 字段（默认参数）
- [x] 1.2 同文件 — 13 条 `TypeEntry` 数据更新（含 `string` / `object`）；`StdlibClassName` 暂时填**旧 lowercase 值**（`Std.int / Std.long / Std.bool / Std.i8 / ...`）保持行为不变
- [x] 1.3 `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Calls.cs` — `BindMemberCallOnUnknownTarget` 删硬编码 switch，改为 `TypeRegistry.GetTypeEntry(tgtName)?.StdlibClassName ?? tgtName`
- [x] 1.4 grep `"Std." +` / `"Std.int" / "Std.long" / "Std.bool"` 等字符串拼接 in `src/compiler/` — 把任何 `BindMemberCallOnPrimitive` / 其他位置的"keyword → Std.<keyword>"拼接也改为查 TypeRegistry
- [x] 1.5 `dotnet build src/compiler/z42.slnx` 全绿
- [x] 1.6 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 100% 通过（行为应当不变）

## 阶段 2: Rust well_known_names.rs 加新常量

- [x] 2.1 `src/runtime/src/metadata/well_known_names.rs` 加 6 个新常量：`STD_INT32 / STD_INT64 / STD_SINGLE / STD_BOOLEAN`（值用 PascalCase）+ `STD_DOUBLE / STD_CHAR`（值不变，但新常量名为 PascalCase，旧名作为 alias 同时保留）
- [x] 2.2 旧常量 `STD_INT / STD_LONG / STD_FLOAT / STD_BOOL` 暂保留（值不变，作 alias，待 step 4 删除）
- [x] 2.3 `cargo build --manifest-path src/runtime/Cargo.toml --release` 全绿（仅多了新常量，未触发其他变化）

## 阶段 3: stdlib + Native binding rename（原子单步）

> 本阶段是变更核心；step 内部按文件块顺序写，但**不分多 commit**。

### 3a. Stdlib 12 primitive 文件

- [x] 3.1 `Primitives/Bool.z42` → `Primitives/Boolean.z42`：`struct bool` → `struct Boolean`；`__bool_*` → `__boolean_*`；`IEquatable<bool>` 保持（keyword 不变）
- [x] 3.2 `Primitives/Char.z42` (filename 不变)：`struct char` → `struct Char`；`__char_*` 全不变
- [x] 3.3 `Primitives/Int.z42` → `Primitives/Int32.z42`：`struct int` → `struct Int32`；`__int_*` → `__int32_*`
- [x] 3.4 `Primitives/Long.z42` → `Primitives/Int64.z42`：`struct long` → `struct Int64`；`__long_*` → `__int64_*`
- [x] 3.5 `Primitives/I8.z42` → `Primitives/SByte.z42`：`struct i8` → `struct SByte`；`__i8_*` → `__sbyte_*`
- [x] 3.6 `Primitives/I16.z42` → `Primitives/Int16.z42`：`struct i16` → `struct Int16`；`__i16_*` → `__int16_*`
- [x] 3.7 `Primitives/U8.z42` → `Primitives/Byte.z42`：`struct u8` → `struct Byte`；`__u8_*` → `__byte_*`
- [x] 3.8 `Primitives/U16.z42` → `Primitives/UInt16.z42`：`struct u16` → `struct UInt16`；`__u16_*` → `__uint16_*`
- [x] 3.9 `Primitives/U32.z42` → `Primitives/UInt32.z42`：`struct u32` → `struct UInt32`；`__u32_*` → `__uint32_*`
- [x] 3.10 `Primitives/U64.z42` → `Primitives/UInt64.z42`：`struct u64` → `struct UInt64`；`__u64_*` → `__uint64_*`
- [x] 3.11 `Primitives/Float.z42` → `Primitives/Single.z42`：`struct float` → `struct Single`；`__float_*` → `__single_*`
- [x] 3.12 `Primitives/Double.z42` (filename 不变)：`struct double` → `struct Double`；`__double_*` 全不变

### 3b. TypeRegistry StdlibClassName 值更新

- [x] 3.13 `src/compiler/z42.Semantics/TypeCheck/TypeRegistry.cs` — 13 条 `StdlibClassName` 值改为 PascalCase（`"Std.int" → "Std.Int32"` 等，完整映射见 proposal.md）

### 3c. Rust corelib

- [x] 3.14 `src/runtime/src/corelib/convert.rs` — 函数 rename：`builtin_int_*` → `builtin_int32_*` / `builtin_long_*` → `builtin_int64_*` / `builtin_i8_*` → `builtin_sbyte_*` / `builtin_i16_*` → `builtin_int16_*` / `builtin_u8_*` → `builtin_byte_*` / `builtin_u16_*` → `builtin_uint16_*` / `builtin_u32_*` → `builtin_uint32_*` / `builtin_u64_*` → `builtin_uint64_*` / `builtin_bool_*` → `builtin_boolean_*` / `builtin_float_*` → `builtin_single_*`；`builtin_double_*` / `builtin_char_*` 不变；同时改函数内部错误消息中的 prefix 字符串
- [x] 3.15 `src/runtime/src/corelib/convert_tests.rs` — 调用 string key（`"__int_parse"` 等）和函数引用同步
- [x] 3.16 `src/runtime/src/corelib/mod.rs` — BUILTINS 表条目 rename（string key + 函数引用同步）
- [x] 3.17 `src/runtime/src/corelib/char.rs` — `__char_*` 不变（仅确认无遗漏）
- [x] 3.18 `src/runtime/src/corelib/tests.rs` — `__char_*` 不变（仅确认无遗漏）

### 3d. 中间验证

- [x] 3.19 `dotnet build` + `cargo build --release` 全绿
- [x] 3.20 `dotnet test` 全绿
- [x] 3.21 `./scripts/regen-golden-tests.sh` 跑通（stdlib `.zpkg` 重建）
- [x] 3.22 `./scripts/test-vm.sh` 全绿

## 阶段 4: well_known_names 清理 + exec_vcall 更新

- [x] 4.1 `src/runtime/src/metadata/well_known_names.rs` — 删除旧常量 `STD_INT / STD_LONG / STD_FLOAT / STD_BOOL`；保留 `STD_DOUBLE / STD_CHAR`（值与新名同；可重命名为新形式 `STD_DOUBLE / STD_CHAR`，名字与值都不变）
- [x] 4.2 `src/runtime/src/interp/exec_vcall.rs` — `primitive_class_name()` 使用新常量名（`STD_INT32 / STD_DOUBLE / STD_BOOLEAN / STD_CHAR / STD_STRING / STD_ARRAY`）
- [x] 4.3 `src/runtime/src/corelib/object.rs` — 检查是否引用 `STD_LONG / STD_FLOAT / STD_BOOL` 等旧名，更新到新名
- [x] 4.4 grep `STD_(INT|LONG|FLOAT|BOOL)\b` in src/runtime 应为零（除文件 well_known_names.rs 自身的注释）
- [x] 4.5 `cargo build --release` 全绿
- [x] 4.6 `cargo test` 全绿

## 阶段 5: 跨包引用 + 文档 + naming-conventions 升级

### 5a. 跨包引用

- [x] 5.1 `src/libraries/z42.core/src/Array.z42` — 注释 `Std.int / Std.String` → `Std.Int32 / Std.String`
- [x] 5.2 `src/tests/generics/generic_inumber.z42` — `Std.int` 等 FQN → PascalCase；如有 expected.txt 同步
- [x] 5.3 `src/compiler/z42.Semantics/TypeCheck/ImportedSymbolLoader.Phase3.cs` — 注释示例同步

### 5b. 文档

- [x] 5.4 `docs/design/language/object-protocol.md` — `Std.int / Std.double` 示例同步
- [x] 5.5 `docs/design/language/generics.md` — `Std.int.op_Add` 等同步
- [x] 5.6 `docs/design/language/static-abstract-interface.md` — `"Std.int" / "Std.double"` 示例同步
- [x] 5.7 `docs/design/language/arrays.md` — `Std.int / Std.String` 同步
- [x] 5.8 `docs/design/language/interop.md` — 同步（具体 grep 后改）
- [x] 5.9 `docs/design/compiler/compiler-architecture.md` — 多处示例同步

### 5c. naming-conventions.md 升级

- [x] 5.10 `docs/design/language/naming-conventions.md` — 删除 Deferred 段的 `naming-conv-4: 内置类型别名是否大写`
- [x] 5.11 同文件 §1 / §10 加正式条款："Primitive 类型的 struct 名采用 BCL 风格 PascalCase（`Boolean / Int32 / SByte / ...`）；keyword（`bool / int / i8 / ...`）是 source-level alias，与 C# `int` ⟷ `System.Int32` 等价。文件名 = struct 名 + `.z42`。"

## 阶段 6: 验证 + 静态检查

- [x] 6.1 `./scripts/test-all.sh --scope=full` 全绿（必经门禁）
- [x] 6.2 静态 grep 检查：`grep -rn "Std\.\(int\|bool\|char\|float\|double\|long\|i8\|i16\|u8\|u16\|u32\|u64\)\b" src/libraries src/compiler src/runtime src/tests docs/design 2>/dev/null | grep -v 'docs/spec/archive'` 应为零
- [x] 6.3 spec.md 中每条 Scenario 逐条对照 ✓
- [x] 6.4 提交前自检 `grep "naming-conv-4" docs/design/language/naming-conventions.md` 应为零
- [x] 6.5 commit 单 commit；message：`refactor(stdlib+vm): rename primitives to BCL PascalCase`；含 `.claude/` + 全部 docs/spec 变更
- [x] 6.6 mv `docs/spec/changes/rename-primitives-to-pascal-case/` → `docs/spec/archive/2026-05-24-rename-primitives-to-pascal-case/`
- [x] 6.7 push 到 origin/main

## 备注

实施期发现 / 决策：

- **Stage 3 fix-1 (intra-scope)**：[`TypeChecker.BindClassMethods`](../../../../src/compiler/z42.Semantics/TypeCheck/TypeChecker.cs#L307) 用 `TypeRegistry.GetZ42Type(cls.Name)` 把 struct 名映射到 Z42PrimType for `this`。rename 后 `cls.Name="Int32"` 不在 TypeRegistry name lookup 表里 → fallback 成 `Z42ClassType`。修复：把 BCL PascalCase 名加进 `TypeEntry.Aliases`（`int → ["i32","Int32"]` 等），新增 12 条 alias，副带启用了 BCL form `Int32 x = 5` 作为合法类型注解。

- **Stage 3 fix-2 (intra-scope)**：[`TypeChecker.Calls.cs:415`](../../../../src/compiler/z42.Semantics/TypeCheck/TypeChecker.Calls.cs#L415) 的 `CapitalizeFirst(primT.Name)` 在 rename 后产 `"Int"`（不是 `"Int32"`）。改为查 `TypeRegistry.GetTypeEntry(primT.Name).StdlibClassName` 取 simple name。

- **Stage 4 fix (intra-scope)**：[`TypeChecker.Generics.cs::PrimitiveImplementsInterface`](../../../../src/compiler/z42.Semantics/TypeCheck/TypeChecker.Generics.cs#L356) 的 hardcoded `switch`（i8/i16/... → "int" / i64 → "long" / ...）失效。改为 TypeRegistry 驱动。

- **Out-of-scope 阻塞（fix-memorystream-override-visibility 独立 spec 解决）**：测试期发现 z42.io / z42.compression 编译失败。挖出 3 个 pre-existing TypeChecker bug（per-CU 处理顺序 / MergeImported 不 populate `_virtualMethods` / ExportedTypeExtractor.FuncToMethod 硬编码 IsVirtual=false）。开独立 minimal-mode spec [`2026-05-24-fix-memorystream-override-visibility`](../../archive/2026-05-24-fix-memorystream-override-visibility/) 修复，先于本 spec commit。

- **Dev infra workaround**：z42-compression cdylib 需手动 symlink 到 `artifacts/build/runtime/{debug,release}/native/libz42_compression.dylib` 才能被 z42vm dlopen。这是 dev-only setup（package 流程会自动放对位置），不阻塞 rename。
