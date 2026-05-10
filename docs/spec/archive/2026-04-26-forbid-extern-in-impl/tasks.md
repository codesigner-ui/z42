# Tasks: forbid-extern-in-impl

> 状态：🟢 已完成 | 创建：2026-04-26 | 完成：2026-04-26 | 类型：refactor (decision lock-in)
> **变更说明：** `impl Trait for Type { ... }` 块永久禁止 `extern` 方法（之前是 deferred）。
> **原因：** extern = VM intrinsic / host FFI 绑定，属类型定义本身；impl 的动机
>           （组织性分离 + 跨包接口扩展）由脚本 body 完全覆盖，加 extern 会让
>           孤儿规则 + 跨包传播 native binding 注册冲突，复杂度不值。
> **文档影响：**
>   - `docs/design/generics.md` extern impl 章节：删除"+extern 方法"后续规划，
>     加"永久禁止"小节 + 4 条理由 + 替代写法（脚本 wrapper + 现有 extern）
>   - `docs/roadmap.md` L3-Impl2 重定义：仅 "跨 zpkg impl 传播（脚本 body）"
>   - `src/compiler/z42.Syntax/Parser/TopLevelParser.cs:ParseImplDecl` 错误消息从
>     "not yet supported (Change 1 scope)" → "not allowed; native bindings belong in the type definition"

- [x] 1.1 Parser 错误消息更新 + 注释解释 by-design ban
- [x] 1.2 `docs/design/generics.md` extern impl 小节 — 加"永久禁止"理由
- [x] 1.3 `docs/roadmap.md` L3-Impl2 描述更新 — 去掉 extern，强调脚本 body + 跨 zpkg
- [x] 1.4 全绿验证：dotnet build / 586 test / 188 test-vm
- [x] 1.5 commit + push（refactor 类）
