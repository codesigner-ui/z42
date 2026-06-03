# Proposal: bundle the `z42` launcher in release packages (portable, model A)

## Why

`scripts/package.sh` desktop packages ship `bin/{z42c, z42vm}` + `libs/` but
**not** the `z42` launcher (added in add-z42-launcher). A downloaded package
can't `z42 run <app.zpkg>` out of the box. We want **model A (portable)**:
unzip → `./bin/z42 run app.zpkg` just works, no install step, no `~/.z42`.

## What Changes

- **Package**: desktop package gains `bin/z42` (native trampoline) + a
  bundled `launcher.zpkg` (the z42 launcher core) at the package root.
- **Trampoline portable fallback**: when `$Z42_HOME/launcher/` is not set up,
  the trampoline resolves its runtime **relative to its own location** —
  `bin/z42vm` (sibling) + `<pkg>/launcher.zpkg` + `<pkg>/libs/` — and runs the
  launcher core there. It also hands the core a **portable runtime hint**
  (`Z42_PORTABLE_VM` / `Z42_PORTABLE_LIBS` = the package's own z42vm + libs) so
  `z42 run` uses the bundled runtime directly without a configured version.
  (Installed mode — `$Z42_HOME/launcher` present — is unchanged and takes
  precedence.)
- **Launcher core**: `run` / `which` honor the portable hint — when set and no
  explicit `--runtime`/default, use `Z42_PORTABLE_VM` + `Z42_PORTABLE_LIBS`
  directly (no `runtimes/<ver>` lookup). Keeps the package free of duplicated
  z42vm/libs or Windows-hostile symlinks.
- **Validation**: `test-dist.sh` gains a check that `<pkg>/bin/z42 run
  <a bundled exe-zpkg>` works from the unpacked package.

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/toolchain/launcher/src/main.rs` | MODIFY | portable fallback + portable-runtime env hints |
| `src/toolchain/launcher/core/launcher.z42` | MODIFY | `run`/`which` honor `Z42_PORTABLE_VM`/`_LIBS` |
| `scripts/_lib/package_desktop.sh` | MODIFY | build + copy trampoline `z42` → bin/ and `launcher.zpkg` → pkg root |
| `scripts/package.sh` | MODIFY | (if needed) ensure launcher build step / dispatch |
| `scripts/test-dist.sh` | MODIFY | add "package `z42 run` works" check |
| `docs/design/runtime/launcher.md` | MODIFY | document portable mode + package layout |

**只读引用**：`src/toolchain/launcher/core/z42.launcher.z42.toml`、`scripts/_lib/launcher-env.sh`（dev env，对照）。

## Out of Scope

- **Installed model (B)** — `install.sh` → `~/.z42`; separate follow-up.
- **P2 download / install / self-update** — still deferred.
- iOS/android/wasm packages (no desktop launcher there).

## Open Questions

- [ ] `launcher.zpkg` 放包根还是 `bin/`？本 spec：**包根**（`<pkg>/launcher.zpkg`），bin/ 只放可执行文件。
