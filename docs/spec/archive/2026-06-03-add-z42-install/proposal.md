# Proposal: `z42 install <version|nightly>` — download from GitHub Releases (P2 v1)

## Why
Installed/portable modes need their runtimes placed manually. P2 v1: the
launcher fetches a runtime straight from GitHub Releases — `z42 install 0.1.0`
or `z42 install nightly` — download, verify (SHA256SUMS), extract into
`$Z42_HOME/runtimes/<id>`. All in z42 (Std.Net.HttpClient + Std.Compression +
Std.Crypto + Std.Encoding). No download-server infra beyond the existing
GitHub Releases (`package.sh`/release pipeline already publishes per-RID
`z42-<ver>-<rid>.tar.gz` + SHA256SUMS).

## What Changes
- `z42 install <version|nightly>`: detect host RID (Platform.OS/Arch →
  macos-arm64 / linux-x64 / linux-arm64), build the release-asset URL
  (`.../releases/download/<tag>/z42-<ver>-<rid>.tar.gz`; tag = `nightly` or
  `v<ver>`), GET (HttpClient follows redirects), verify against the SHA256SUMS
  asset, gunzip + untar into `$Z42_HOME/runtimes/<id>`.
- `z42 uninstall <id>`: remove `$Z42_HOME/runtimes/<id>`.
- Windows (.zip) install left for follow-up (this v1 = tar.gz: macos/linux).
- Auto-fetch on `z42 run` (missing version → install) = v2; v1 errors with a
  hint to `z42 install`.

## Scope
| 文件 | 类型 | 说明 |
|------|------|------|
| `src/toolchain/launcher/core/launcher.z42` | MODIFY | install/uninstall commands + host-RID + SHA256SUMS parse |
| `src/toolchain/launcher/core/z42.launcher.z42.toml` | MODIFY | deps: z42.net, z42.compression, z42.crypto, z42.encoding |
| `docs/design/runtime/launcher.md` | MODIFY | install section |

## Out of Scope
- Windows .zip; `self update`; auto-fetch-on-run; version index/`z42 list --available`.
- Configurable repo (hardcode codesigner-ui/z42 v1).

## Open Questions
- [ ] tag 约定:`nightly` → `nightly`,`X.Y.Z` → `vX.Y.Z`(本 spec 取此,匹配现有 release tags)。
