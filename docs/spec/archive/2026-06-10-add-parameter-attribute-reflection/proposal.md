# Proposal: 参数级用户 attribute 反射（ParameterInfo.GetCustomAttributes）

## Why

反射的用户 attribute 链已覆盖 class（C3a）、method（C3b）、field（add-field-attribute-reflection）三个目标，**唯独参数目标缺失**。`MethodInfo.GetParameters()` 返回的 `ParameterInfo` 当前只有 `Name` / `ParameterType` / `Position`，无法读取参数上的 `[Attr(...)]`。这是 attribute 反射 MVP 的最后一个声明目标（AttributeUsage / 泛型 attribute / 专用诊断仍属更后续）。补齐后，attribute 反射在「声明位置」维度完整（type / method / field / parameter）。

不做的代价：参数 attribute（如未来 DI/序列化/校验框架标注形参）无法经反射发现，用户必须改用 method-level 或外部约定 workaround。

## What Changes

- **语法**：参数声明前可写 `[Attr(...)]`（解析器当前**丢弃** param 上的 attribute——本变更改为捕获）。
- **zbc/zpkg 格式 bump**：SIGS section 每个函数记录在「方法级 attr 块」之后追加「每参数 attr 块」（`zbc 1.14→1.15` / `zpkg 0.16→0.17`，strict-pin）。
- **synthesizer**：为每个带 attribute 的参数合成 parameterless factory thunk（key `param$<func>$<paramIdx>$<n>`），镜像 field/method factory 机制。
- **runtime**：`zbc_reader` 读每参数 attr-ref；`loader` 按 `(qualified-func, paramIdx)` 建索引；新 builtin `__param_custom_attributes`；`ParameterInfo` 携带 `__qualified` + `__position` 以便反射定位。
- **stdlib**：`ParameterInfo` 增 `GetCustomAttributes()` / `GetAttribute(Type)`（镜像 `FieldInfo`）。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Syntax/Parser/Ast.cs` | MODIFY | `Param` record 增 `Attributes`（`List<UserAttribute>?`） |
| `src/compiler/z42.Syntax/Parser/TopLevelParser.Members.cs` | MODIFY | 参数解析捕获 leading `[Attr]`（现丢弃） |
| `src/compiler/z42.IR/IrModule.cs` | MODIFY | `IrFunction` 增 `ParamAttributes`（`List<List<IrAttributeRef>>?`，长度=ParamCount，每项 0+ 个 ref） |
| `src/compiler/z42.Semantics/Codegen/AttributeFactorySynthesizer.cs` | MODIFY | 合成 `param$<func>$<idx>$<n>` factory thunk |
| `src/compiler/z42.Semantics/Codegen/IrGen.*.cs` | MODIFY | EmitFunction 填充 `ParamAttributes` |
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs` | MODIFY | `VersionMinor` 14→15；SIGS 每函数尾部写每参数 attr 块；intern param attr 串 |
| `src/compiler/z42.IR/BinaryFormat/ZbcReader.cs` | MODIFY | SIGS 读每参数 attr 块 |
| `src/compiler/z42.Project/ZpkgWriter.cs` | MODIFY | `VersionMinor` 16→17（联动 zbc bump） |
| `src/runtime/src/metadata/bytecode.rs` | MODIFY | `FuncDesc`/Sig 增 per-param `attributes`（`Box<[Box<[AttributeRef]>]>`） |
| `src/runtime/src/metadata/zbc_reader.rs` | MODIFY | `ZBC_VERSION_MINOR` 15 / `ZPKG_VERSION_MINOR` 17；SIGS 读每参数 attr-ref（复用 `read_attr_refs`） |
| `src/runtime/src/metadata/loader.rs` | MODIFY | 建 `(qualified-func, paramIdx) → attr refs` 索引 |
| `src/runtime/src/metadata/types.rs` | MODIFY | 承载 param-attr 索引（func 元数据冷区） |
| `src/runtime/src/corelib/reflection.rs` | MODIFY | `ParameterInfo` 加 `__qualified`/`__position`；新 `__param_custom_attributes` |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 注册 `__param_custom_attributes` |
| `src/libraries/z42.core/src/Reflection/ParameterInfo.z42` | MODIFY | `GetCustomAttributes()` / `GetAttribute(Type)` + 内部 `__qualified`/`__position` |
| `docs/design/runtime/zbc.md` | MODIFY | Minor changelog +1 行（1.15） |
| `docs/design/runtime/zpkg.md` | MODIFY | Minor changelog +1 行（0.17） |
| `docs/design/language/reflection.md` | MODIFY | API 表 + 实现原理 + Deferred `reflection-future-parameter-attributes` 标记落地；移除 `reflection-future-parameter-names` 误关联 |
| `docs/design/language/attributes.md` | MODIFY | parameter 目标从 Deferred 移入「已支持目标」 |
| `src/tests/zbc-format/generate-fixtures.sh` | RUN | 6 fixture regen（含版本字节 delta） |
| `src/tests/zpkg-format/generate-fixtures.sh` | RUN | fixture regen |
| `src/tests/types/param_attributes.z42` | NEW | golden e2e：参数 attribute 反射 |
| `src/libraries/z42.core/tests/reflection.z42` | MODIFY | 追加参数 attribute [Test] 断言 |

**只读引用**：
- `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs:432-514`（SIGS 现布局 + WriteAttrRefs）— 镜像基准
- `docs/spec/archive/2026-06-10-add-field-attribute-reflection/` — 同构先例
- `.claude/rules/version-bumping.md` — bump checklist

## Out of Scope（关键：z42c 同步的处置）

- **z42c 自举 writer 同步（`src/z42c/z42c.ir/src/BinaryFormat/ZbcFormat.z42` 版本常量 + `ZbcWriter.z42` SIGS 镜像 + `src/z42c/z42c.semantics/tests/zbc/zbc_tests.z42` golden hex）**：version-bumping.md step 5 要求与本 bump 同 commit 同步，但 `z42c` 子系统**被 port-z42c-tsig 占用**。处置见 design.md「z42c 锁冲突」决策——**待 User 在 6.5 gate 裁决**（等 port 归档 / 授权共占 z42c / 拆分）。本 proposal 的 Scope **不含** `src/z42c/*`。
- AttributeUsage（参数目标校验）、泛型 attribute、参数名持久化（debug-info 路径，见 `reflection-future-parameter-names`）、`GetValue`/`Invoke`。

## Open Questions

- [ ] **z42c 锁冲突**：format bump 需同步 z42c，但 z42c 被占。如何排程？（design.md Decision 1，待 6.5 裁决）
- [ ] param attribute factory key 命名：`param$<qualifiedFunc>$<idx>$<n>` 是否与现有 `fld$` / method `__attr$` 命名空间无碰撞（design.md Decision 2 确认）
