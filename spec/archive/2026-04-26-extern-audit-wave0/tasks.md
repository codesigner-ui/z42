# Tasks: extern-audit-wave0

> 状态：🟢 已完成 | 类型：refactor + docs | 创建：2026-04-26 | 完成：2026-04-26

**变更说明：** 落地 BCL/Rust 对标的 "Primitive vs Feature" 准则到设计文档，并清理 Wave 0 死代码（13 个不再被编译器 emit 的 `__list_*` / `__dict_*` builtin）。

**原因：** stdlib-organization.md / stdlib.md / libraries/README.md 三处已分别表述过 Script-First 原则，但缺少明确的 BCL/Rust 对标 + 现存 extern 审计清单。同时 [corelib/mod.rs:122-137](../../../src/runtime/src/corelib/mod.rs#L122) 的 13 个 list/dict builtin 在 L3-G4h step3 后已是死代码（[SymbolTable.cs:258](../../../src/compiler/z42.Semantics/TypeCheck/SymbolTable.cs#L258) 注释 + 全 `src/compiler` 无引用证实），需要清理。

**文档影响：**
- `docs/design/stdlib-organization.md` 新增 §"Primitive vs Feature (BCL/Rust 对标)"
- `src/libraries/README.md` 新增"Extern 现状审计表"
- `docs/design/philosophy.md` §8 末尾追加一句"先看审计表"

## Tasks

- [x] 1.1 docs/design/stdlib-organization.md：新增 §"Primitive vs Feature (BCL/Rust 对标)"，含核心准则、BCL/Rust 对标表、与"不回溯迁移"正交声明
- [x] 1.2 src/libraries/README.md：新增"Extern 现状审计表"（~80 行 builtin 三列分类）+ Wave 进度记录
- [x] 1.3 docs/design/philosophy.md §8：末尾补"stdlib 改动先看审计表"约束
- [x] 1.4 docs/design/stdlib.md：清理 `__sb_*` / `__list_*` / `__dict_*` 等 stale 列表，把 file layout 从 `interp/builtins/` 修正为 `corelib/`，把 intrinsic SoT 委托给审计表
- [x] 2.1 删除 src/runtime/src/corelib/collections.rs（整个文件）
- [x] 2.2 src/runtime/src/corelib/mod.rs：删除 13 行 dispatch + `pub mod collections;` 声明 + 注释更新
- [x] 3.1 重生 golden test（96 个全部 OK，覆盖 20_dict / 40_list_operations 等）
- [x] 3.2 dotnet build && cargo build —— 无编译错误
- [x] 3.3 dotnet test —— 712 passed, 0 failed
- [x] 3.4 ./scripts/test-vm.sh —— 188 passed (94 interp + 94 jit), 0 failed
- [ ] 4.1 commit + push（含 .claude/、spec/、docs/、src/）
- [ ] 4.2 归档到 spec/archive/2026-04-26-extern-audit-wave0/

## 备注

- 实施中扩展了 Scope：发现 `docs/design/stdlib.md` 也含直接受 Wave 0 删除影响的 stale 内容（intrinsic 列表 + file layout + StringBuilder 违规行），按文档同步规则一并修正
- Wave 1（19 个 feature → 脚本迁移）和 Wave 2（3 个 codegen 特化）按包独立 spec 后续起，不在本变更内
- 2026-04-26 起，新增 extern 必须在 PR 描述里回答"BCL/Rust 把它当 primitive 吗？"，否则拒绝
