# Design: launcher trampoline at package root

## Decision: pkg root = exe.parent() (z42 at root)
Before: `current_exe → parent (bin/) → parent (pkg root)`; vm=`pkg/bin/z42vm`.
After:  `current_exe → parent (pkg root)`; vm=`pkg/bin/z42vm` (unchanged relative).
Only the trampoline location changes (root vs bin/); z42vm/core/libs paths
relative to pkg root are identical. Installed mode ($Z42_HOME/launcher/...) and
bare-run (`cmd.arg(launcher.zpkg)`) are untouched. pre-1.0: no compat for old
`bin/z42` layout.

## Why z42vm stays in bin (asymmetry)
`bin/` = executables/apps collection (z42c, z42vm, future apps). Root `z42` =
single entry/trampoline. The trampoline resolves siblings: `bin/z42vm`,
`./launcher.zpkg`, `./libs`.

## Testing
- cargo build launcher; minimal portable layout smoke (root z42 + bin/z42vm +
  launcher.zpkg + libs) → `z42 list` / bare run resolves + runs launcher.
- `package.sh release` produces root `z42`; CI/next nightly is authoritative.
