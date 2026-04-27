# Tasks: Remove orphan binary.rs reader

> 状态：🟢 已完成 | 创建：2026-04-28 | 完成：2026-04-28
> 类型：refactor（最小化模式）

**变更说明：** 删除 `src/runtime/src/metadata/binary.rs`（863 行）。

**原因：** 该文件未在 `metadata/mod.rs` 中以 `mod binary;` 声明，rustc 看不到它；6 个 `pub fn`（`decode_zbc` / `decode_zpkg_packed` / `read_zpkg_entry` / `read_zpkg_is_exe` / `read_zpkg_deps` / `read_zpkg_namespaces`）在整个 runtime crate 零调用方。系历史遗留孤儿，与 `zbc_reader.rs` 功能完全重叠（review2 §1.2）。删除后唯一活 reader 是 `zbc_reader.rs`。

**文档影响：** 无。binary.rs 不在 `mod.rs` 注释列表中，也未被 `docs/design/` 任何文档引用。本 spec 在归档时记入 `docs/review2.md` 跟踪表（如有需要）。

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/metadata/binary.rs` | DELETE | 孤儿文件，零调用方 |

**只读引用**（用于核实零调用方）：
- `src/runtime/src/metadata/mod.rs` — 确认无 `mod binary;`
- `src/runtime/src/metadata/loader.rs` — 确认仅 `use super::zbc_reader::*`
- `src/runtime/src/metadata/zbc_reader.rs` — 唯一活 reader

## 任务

- [x] 1.1 删除 `src/runtime/src/metadata/binary.rs`
- [x] 1.2 `cargo build --manifest-path src/runtime/Cargo.toml` 通过
- [x] 1.3 `cargo test --manifest-path src/runtime/Cargo.toml` 全绿
- [x] 1.4 `dotnet build src/compiler/z42.slnx` 通过
- [x] 1.5 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 全绿
- [x] 1.6 `./scripts/test-vm.sh` 全绿
- [x] 1.7 归档到 `spec/archive/2026-04-28-remove-orphan-binary-reader/`
- [x] 1.8 自动 commit + push（含 `.claude/` 与 `spec/`）

## 备注

review2 §1.2 原建议"抽公共字节游标到 byte_cursor.rs"基于"两份并存合并"的前提。本 spec 探索后发现 binary.rs 是死代码、不存在两份并存，故该建议无意义，已移出 Scope。
