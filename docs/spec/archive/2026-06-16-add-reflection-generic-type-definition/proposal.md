# Proposal: typeof 携带泛型实例化 args → IsGenericTypeDefinition / GetGenericTypeDefinition

## Why

反射列表 #1。`typeof(Box<int>)` 当前在编译期就丢掉了实例化 type args
（`Z42TypeName(Z42InstantiatedType)` 只取定义名 `"Box"`），运行期解析到**定义**
TypeDesc，导致：

- `typeof(Box<int>).GetGenericArguments()` 返回**空数组**（本该 `[int]`）—— 已是可见 bug，
  与 `IsGenericType==true` 自相矛盾。
- `IsGenericTypeDefinition`（开放定义 `Box<>` vs 构造型 `Box<int>`）运行期不可区分。
- `GetGenericTypeDefinition()`（从 `Box<int>` 拿回 `Box<>`）无法实现。

根因是 typeof 的 type args 在 codegen 处被结构性丢弃。修复一处即同时兑现上述三项。

## What Changes

- **新增 IR 指令 `Typeof`**（opcode），携带 `TypeName` + 结构化 `TypeArgs`（FQ 名列表，
  镜像既有 `ObjNewInstr.TypeArgs` 的 count + STRS 索引编码）。所有 `typeof(...)` 统一走它，
  移除 `__typeof` builtin（type args 是编译期类型元数据，不该 materialize 成 ConstStr 运行期值）。
- **zbc 1.17→1.18 / zpkg 0.19→0.20**（新 opcode = wire 格式变更，按 version-bumping.md 同步）。
- **运行期**：interp + jit 求值 `Typeof`；非空 type-args → 构造型 `Std.Type` 挂 type-args 槽
  （镜像数组 `__elementName` 先例）。
- **stdlib**：`Std.Type.IsGenericTypeDefinition` / `GetGenericTypeDefinition()`；
  `GetGenericArguments()` 改读构造型槽（修空数组 bug）。
- **z42c writer 同步延后**（z42c 锁被 port-z42c-self-compile 占）：沿用
  add-reflection-array-element-type / get-interfaces 先例，C# 侧先落地，
  `xtask test compiler-z42` byte-identical gate 暂红，follow-up 跟踪。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.IR/BinaryFormat/Opcodes.cs` | MODIFY | 新增 `Typeof` opcode 常量 |
| `src/compiler/z42.IR/IrModule.cs` | MODIFY | 新增 `TypeofInstr(Dst, TypeName, TypeArgs)` record |
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.Instructions.cs` | MODIFY | 写 Typeof（opcode + TypeName idx + count + TypeArgs idx[]）+ STRS intern |
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs` | MODIFY | `VersionMinor` 17→18 + 注释 |
| `src/compiler/z42.Project/ZpkgWriter.cs` | MODIFY | `VersionMinor` 19→20 + 注释 |
| `src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs` | MODIFY | `VisitTypeof` emit `TypeofInstr`（base 名 + Z42TypeName 化的 TypeArgs）；`Z42TypeName(InstantiatedType)` 仍只产定义名（args 走 instr 字段，不进名字串）|
| `src/runtime/src/metadata/zbc_reader.rs` | MODIFY | 读 Typeof opcode；`ZBC_VERSION_MINOR` 18 / `ZPKG_VERSION_MINOR` 20 |
| `src/runtime/src/metadata/mod.rs` | MODIFY | `Instruction::Typeof` 变体 + `TypeofInsn` 结构 |
| `src/runtime/src/interp/exec_instr.rs` | MODIFY | 求值 Typeof → make_type + 挂 type-args |
| `src/runtime/src/jit/translate.rs` | MODIFY | translate Typeof（可走 runtime helper） |
| `src/runtime/src/jit/helpers/object.rs` | MODIFY | `jit_typeof` host helper |
| `src/runtime/src/jit/helpers/registry.rs` | MODIFY | `jit_typeof` reg + decl + HelperIds 字段 |
| `src/runtime/src/jit/helpers/call.rs` | MODIFY | **根因修（Scope 扩展）**：`jit_builtin` 把 builtin 错误包装成 `Std.Exception`（镜像 interp make-corelib-errors-catchable，此前 JIT 设原始 Value::Str → `catch (Exception)` 不匹配）。本 change 的 `GetGenericTypeDefinition` 抛异常首次触发该 latent JIT bug |
| `src/runtime/src/metadata/zbc_reader_tests.rs` | MODIFY | version-pin 测试 18/20 |
| `src/runtime/src/corelib/reflection.rs` | MODIFY | 构造型 Type build + type-args 槽；`__type_is_generic_definition` / `__type_generic_definition` builtin；`builtin_type_generic_args` 改读槽 |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 注册新 builtin |
| `src/libraries/z42.core/src/Type.z42` | MODIFY | `IsGenericTypeDefinition` / `GetGenericTypeDefinition()` extern；`GetGenericArguments` 文档 |
| `docs/design/language/reflection.md` | MODIFY | 主体节 + Deferred 更新（generic-type-definition 标落地）|
| `docs/design/runtime/zbc.md` | MODIFY | Minor changelog 1.18 |
| `docs/design/runtime/zpkg.md` | MODIFY | Minor changelog 0.20 |
| `docs/design/runtime/ir.md` | MODIFY | 新 Typeof 指令文档 |
| `docs/roadmap.md` | MODIFY | Deferred Backlog Index 更新 |
| `src/tests/types/generic_type_definition.z42` | NEW | golden（interp+jit）|
| `src/tests/zbc-format/**`（regen） | MODIFY | 6 fixture regen（格式 delta）|
| `src/tests/zpkg-format/**`（regen） | MODIFY | fixture regen |
| `src/compiler/z42.Tests/IrGenTests.cs` 或对应 | MODIFY | TypeofInstr codegen 单测 |

**只读引用**：

- `src/compiler/z42.IR/BinaryFormat/ZbcWriter.Instructions.cs:164-171`（ObjNew TypeArgs 编码先例）
- `src/runtime/src/metadata/zbc_reader.rs:1010-1016`（ObjNew type_args 读法先例）
- `docs/spec/archive/2026-06-1?-add-reflection-array-element-type/`（__elementName 槽 + wire 字段先例）
- `.claude/rules/version-bumping.md`（bump checklist）

## Out of Scope

- **嵌套泛型 type-args**（`typeof(Box<Map<K,V>>)`）：MVP 仅一层（args 为非泛型或裸类）；
  嵌套 → Deferred。
- **开放语法 `typeof(Box<>)`**：z42 无此语法；开放定义只经 `GetGenericTypeDefinition()` 取得。
- **MakeGenericType / Activator / Method.Invoke**：0.5.x，依赖 generic instantiation。
- **z42c writer 同步**：延后 follow-up（z42c 锁被占）。

## Open Questions

- [x] 统一所有 typeof 走新 opcode（移除 __typeof builtin） vs 仅泛型实例化走新 opcode
  —— **User 已定（2026-06-16）：统一**（design.md Decision 1 选 A）。
