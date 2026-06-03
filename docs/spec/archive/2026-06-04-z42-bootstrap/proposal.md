# Proposal: z42-bootstrap (install-z42)

## Why
To run the z42-implemented dev tooling (xtask + migrated scripts), the repo
first needs a working z42 launcher present. That's a chicken-and-egg: you need
z42 to run z42 scripts. So ONE native bootstrap downloads the prebuilt launcher
package from GitHub Releases into a project-local `<repo>/.z42`. This is the
only non-z42 script that stays (besides the package's own install.sh).

## What Changes
- `scripts/install-z42.sh` (Linux/macOS) + `.bat` (Windows) + `.command`
  (macOS Finder double-click → wraps .sh).
- Version from `versions.toml [toolchain.z42].launcher` (default `nightly`,
  or a pinned `0.1.0`).
- Per-RID download: `z42-<ver>-<rid>.{tar.gz|zip}` from
  `releases/download/<tag>/`; SHA256-verify vs the release's SHA256SUMS.
- Install to `<repo>/.z42` (isolated; gitignored).
- Version check every run: nightly → re-download when the release changed
  (compare published_at stamp); pinned → skip if already installed.

## Scope
| 文件 | 类型 | 说明 |
|------|------|------|
| `versions.toml` | MODIFY | `[toolchain.z42].launcher` |
| `scripts/install-z42.sh` | NEW | bash bootstrap (RID detect, download, verify, extract, stamp) |
| `scripts/install-z42.bat` | NEW | Windows bootstrap (.zip) |
| `scripts/install-z42.command` | NEW | macOS double-click → exec install-z42.sh |
| `.gitignore` | MODIFY | `.z42/` |
| `docs/design/runtime/launcher.md` | MODIFY | bootstrap + project-local .z42 |

## Out of Scope
- xtask CLI / script migration (later specs).
- Self-update of the bootstrap itself.
