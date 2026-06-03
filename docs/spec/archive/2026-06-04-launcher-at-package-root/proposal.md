# Proposal: launcher trampoline at package root

## Why
The `z42` trampoline is the package's entry point. Putting it at the package
root (instead of `bin/`) makes `bin/` a clean home for apps (z42c, z42vm,
future tools) and lets a bare `<pkg>/z42` be the obvious "run me" entry that
loads `launcher.zpkg`. Foundation for the bootstrap (`z42-bootstrap`) which
lays the downloaded package into `<repo>/.z42`.

## What Changes
- Package layout: trampoline `z42` moves from `<pkg>/bin/z42` to `<pkg>/z42`.
  `z42vm` / `z42c` stay in `bin/`. `launcher.zpkg` / `libs/` stay at root.
- Trampoline portable resolution: exe at `<pkg>/z42` → `exe.parent()` = pkg
  root (one level, not bin→pkg); vm = `<pkg>/bin/z42vm`, core =
  `<pkg>/launcher.zpkg`, libs = `<pkg>/libs`. Installed mode ($Z42_HOME) unchanged.
- Bare `z42` already runs `launcher.zpkg` (no behavior change there).

## Scope
| 文件 | 类型 | 说明 |
|------|------|------|
| `src/toolchain/launcher/src/main.rs` | MODIFY | resolve_runtime portable: pkg root = exe.parent() |
| `scripts/_lib/package_desktop.sh` | MODIFY | step 2c: trampoline → `$PKG_DIR/z42` (root) |
| `scripts/install.sh` | MODIFY | trampoline source `$PKG/bin/$tramp` → `$PKG/$tramp` |
| `docs/design/runtime/launcher.md` | MODIFY | resolution order + 布局图 |
| `docs/design/compiler/build-artifacts-layout.md` | MODIFY | package 形态(根 z42 / bin = apps) |

只读引用: `release.yml`(经 package.sh 自动继承,无需改)。

## Out of Scope
- bootstrap / xtask / migration (后续 spec).
- release.yml (inherits via package.sh).
