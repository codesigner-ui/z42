# Tasks: Remove stale __assert_* builtin tests

> 状态：🟢 已完成 | 创建：2026-04-28 | 完成：2026-04-28
> 类型：fix（最小化模式）

**变更说明：** 删除 `src/runtime/src/corelib/tests.rs` 中 2 个测试遗留（`assert_eq_success` / `assert_eq_failure`），它们引用已于 2026-04-27 `wave1-assert-script` 迁移到脚本时删除的 `__assert_eq` builtin。

**原因：**
- `__assert_*` 6 个 builtin 已在 2026-04-27 wave1-assert-script 删除（mod.rs:24 注释明确记录），但 tests.rs:118–128 两个测试未同步删除
- `assert_eq_success` 期望 `is_ok()`，实际 `__assert_eq` 不存在→ unknown-builtin 报错 → 测试失败
- `assert_eq_failure` 期望 `is_err()`，因 unknown-builtin 也是 err → 误打误撞通过（**仍是无效测试**，应一并删除）

**文档影响：** 无。这是清理遗留测试，不改任何对外行为。`mod.rs:24` 已记录 builtin 删除事实。

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/corelib/tests.rs` | MODIFY | 删除 line 118–128 的 "── assert ──" 测试段 |

## 任务

- [x] 1.1 删除 `src/runtime/src/corelib/tests.rs:118–128`（含 section 注释 + 两个 #[test] 函数）
- [x] 1.2 `cargo test --manifest-path src/runtime/Cargo.toml` 全绿（60 passed → 59 passed，0 failed）
- [x] 1.3 commit + push

## 备注

由删除孤儿 `binary.rs`（spec `remove-orphan-binary-reader`）的验证步骤暴露。本 spec 与之独立，互不阻塞。
