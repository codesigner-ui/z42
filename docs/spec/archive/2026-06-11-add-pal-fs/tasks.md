# Tasks: add-pal-fs (PAL Phase 2)

**变更说明：** 把 `corelib/fs.rs` 的 2 个 `#[cfg(unix)]` 块（`make_executable` / `symlink`）迁到新 `pal/fs.rs`，consumer 零 cfg。
**原因：** PAL Phase 2（review.md Part 1 P2 / pal.md migration plan）——平台 cfg split 集中到 `pal/` 层。
**类型：** refactor（行为保持，无格式 bump）。**文档影响：** `pal.md`（Phase 2 → done）+ `pal/README.md`。
**子系统锁：** runtime（与 align-type-memberinfo-hierarchy 例外共存，文件零重叠，见 ACTIVE.md）。

## 设计（镜像 Phase 1 `pal/system.rs`）

public OS-neutral 函数 → cfg-gated `*_impl()`；`corelib/fs.rs` 调 `pal::fs::*`，零 cfg：
- `pal::fs::make_executable(path) -> Result<()>`：unix 加 `u+x,g+x,o+x`（`mode|0o111`）；非 unix no-op
- `pal::fs::symlink(src, dst) -> Result<()>`：unix `std::os::unix::fs::symlink`；非 unix bail

> pal.md 原列 `read_permissions`/`set_permissions` 作为 make_executable 的 building block——
> 当前无独立 consumer，按 YAGNI 折进 make_executable（folded），单独 split 留待有 consumer 时。

## 任务

- [x] 1. 新建 `src/runtime/src/pal/fs.rs`：`make_executable` / `symlink` + cfg-gated impls
- [x] 2. `src/runtime/src/pal/mod.rs`：`pub mod fs;` + doc（fs 从 future→current）
- [x] 3. `corelib/fs.rs`：`builtin_file_make_executable` / `builtin_file_symlink` 改 call `crate::pal::fs::*`，删 cfg 块
- [x] 4. 文档：`pal.md` Phase 2 → ✅ landed（含 read/set_permissions folded 说明）；`pal/README.md` 加 fs.rs 行
- [x] 5. GREEN：cargo build + cargo test（runtime）+ VM goldens/stdlib（exercise make_executable/symlink 的用例不回归）
- [x] 6. commit + push + 释锁归档

## 备注

PAL Phase 3（signal）= 下一个 change。Phase 4（thread）/ 5（mem）是为**未实现特性**（多线程 /
GC bump allocator）引入新抽象——无 consumer，按 YAGNI 不现在做，consumer 落地时随之，pal.md 记延后。
