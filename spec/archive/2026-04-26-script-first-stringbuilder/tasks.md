# Tasks: script-first-stringbuilder

> 状态：🟢 已完成 | 创建：2026-04-26 | 完成：2026-04-26 | 类型：refactor (rule compliance + Script-First)

> **变更说明**：把 `Std.Text.StringBuilder` 从 5 个 VM extern intrinsic 改写为
> 纯 z42 脚本（基于 z42.core 的 `String.FromChars` + `Convert.ToString` + `string[]` buffer）。
>
> **原因**：违反两条 stdlib 规则：
>   1. `libraries/README.md` "VM 接口集中在 z42.core" — 非 core 包禁止声明 extern；
>      z42.text 当前有 5 个 `[Native(...)] public extern` 方法
>   2. `libraries/README.md` "Script-First：优先脚本实现" — StringBuilder 完全
>      靠 native，没有用 Script-First 路径
>
> **文档影响**：
>   - `src/libraries/z42.text/src/StringBuilder.z42` 重写为脚本
>   - `src/libraries/z42.text/README.md` 同步描述
>   - `src/runtime/src/corelib/string_builder.rs` 删除（dead code）
>   - `src/runtime/src/corelib/mod.rs` 移除 `__sb_*` 注册 + `pub mod string_builder`
>   - `src/runtime/src/metadata/types.rs` `NativeData::StringBuilder` 变体删除
>   - `src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs` 移除
>     `EmitBoundNew` 的 `case "StringBuilder"` 特例
>   - `src/compiler/z42.Semantics/Codegen/FunctionEmitterCalls.cs` 移除
>     `IsBuiltinCollectionType` 的 SB 分支 + ResolveBuiltinMethod 的 SB 方法映射
>   - `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Calls.cs` 同步移除
>   - `docs/design/compiler-architecture.md` "pseudo-class 策略与迁移" 小节同步

- [x] 1.1 重写 `StringBuilder.z42` — 基于 string[] + Convert.ToString + String.FromChars
- [x] 1.2 移除 IrGen `EmitBoundNew` 中 `case "StringBuilder"` 特例
- [x] 1.3 移除 IrGen `FunctionEmitterCalls.IsBuiltinCollectionType` / `ResolveBuiltinMethod` 的 SB 分支
- [x] 1.4 移除 TypeChecker.Calls.IsBuiltinCollectionType 的 SB 分支
- [x] 1.5 移除 VM `string_builder.rs` 模块（删整个文件）
- [x] 1.6 移除 `corelib/mod.rs` 的 `__sb_*` 注册 + `pub mod string_builder`
- [x] 1.7 移除 `NativeData::StringBuilder` 变体（保留 enum 用于未来 native types）
- [x] 1.8 stdlib clean rebuild (5/5)
- [x] 1.9 regen golden tests + dotnet test 597/597 + test-vm 188/188 全绿
- [x] 1.10 文档同步 + 归档 + commit

## 备注

### 实施过程

1. **依赖前置 fix**：原计划用 `List<string> _parts` 但 parser 字段类型对泛型实例化语法
   不支持（`List<string>` 被误识别为 method header）。改用 `string[]` + 手动 grow。
2. **依赖前置 fix2**：实现中发现 `"\n"` 字面量未被 parser 解码（lexer 跳过转义但
   parser 取 raw text）。先做了 `fix-string-literal-escape` (commit ed6b5ac) 修复
   parser，然后 SB 才能正确处理 AppendLine。
3. **`override` 语法**：z42 stdlib 用 `override string ToString()` 顺序（不是
   C# 的 `override public string ToString()`）— `public` 不能跟 override 同行。

### 解锁

- `z42.text` 包现在符合"VM 接口集中在 z42.core"规则，不含任何 extern
- 后续添加文本类（如 Regex 占位实现）也走纯脚本路径
- 移除 6 个 VM intrinsic 函数（__sb_new, __sb_append, __sb_append_line,
  __sb_append_newline, __sb_length, __sb_to_string）+ NativeData::StringBuilder 变体
