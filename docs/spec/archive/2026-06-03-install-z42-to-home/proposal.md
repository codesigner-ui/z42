# Proposal: install z42 to $Z42_HOME (installed model B)

## Why
The portable package (bundle-launcher-in-release) runs in-place but isn't on
PATH and manages no versions. Model B: an `install.sh` shipped in the package
sets up `$Z42_HOME` (default ~/.z42) so `z42` is on PATH and multiple runtime
versions coexist (rustup-like). The launcher already prefers installed mode
($Z42_HOME/launcher) over portable, so this is mostly the installer + PATH.

## What Changes
- New `scripts/install.sh` template → copied into desktop packages at root.
- `install.sh` (run from the unpacked package): reads version from
  manifest.toml; lays out `$Z42_HOME/{bin/z42, bin/z42c, launcher/{z42vm,
  launcher.zpkg,libs}}`; registers this version as a runtime via
  `z42 link $Z42_HOME/launcher --as <ver>` + `z42 default <ver>` (no
  duplicated z42vm/libs); prints PATH instructions (no auto profile edit).
- `package_desktop.sh`: copy install.sh into the package.
- `test-dist.sh`: install into a temp $Z42_HOME, then `z42 run` (installed mode).
- Docs: launcher.md installed-mode section.

## Scope
| 文件 | 类型 | 说明 |
|------|------|------|
| `scripts/install.sh` | NEW | installer template (shipped in package) |
| `scripts/_lib/package_desktop.sh` | MODIFY | copy install.sh into package |
| `scripts/test-dist.sh` | MODIFY | installed-mode smoke (temp $Z42_HOME) |
| `docs/design/runtime/launcher.md` | MODIFY | installed model section |

## Out of Scope
- Windows `install.ps1` (parallel logic; follow-up — can't validate on this host).
- Auto PATH/profile editing (print instructions only).
- P2 download/install/self-update.

## Open Questions
- [ ] z42c 放 `$Z42_HOME/bin`(本 spec：是,便于 `z42c build` 上 PATH)还是只 runtimes/<ver>/。
