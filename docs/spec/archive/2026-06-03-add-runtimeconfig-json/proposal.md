# Proposal: app `runtimeconfig.json` (version declaration + dynamic config)

## Why
P2 (download-on-demand) and per-app runtime config need a way for an app to
declare which runtime version it needs + tune runtime knobs. Adopt the .NET
model: a standalone `<app>.runtimeconfig.json` sidecar (NOT embedded in the
zpkg — so the version-agnostic launcher reads it without parsing the versioned
package format, and it's editable/dynamic).

## What Changes
- Format: `<app>.runtimeconfig.json` next to `<app>.zpkg`:
  ```json
  { "runtime": { "version": "0.3.4", "rollForward": "exact" },
    "configProperties": { "Z42_GC_MODE": "concurrent", "Z42_SAFEPOINT_THROTTLE": 1024 } }
  ```
- Launcher core `run`: if the sidecar exists, `runtime.version` feeds the
  version resolution (slot 2, previously empty); `configProperties` (arbitrary
  Z42_* knobs) are set as env on the spawned z42vm. Resolution order:
  `--runtime` > runtimeconfig `runtime.version` > default > sole-installed.
- `rollForward` parsed but P1 honors only `exact`.

## Scope
| 文件 | 类型 | 说明 |
|------|------|------|
| `src/toolchain/launcher/core/launcher.z42` | MODIFY | `run` reads sidecar: version → resolve; configProperties → env |
| `src/toolchain/launcher/core/z42.launcher.z42.toml` | MODIFY | add z42.json dep |
| `docs/design/runtime/launcher.md` | MODIFY | runtimeconfig.json section |

## Out of Scope
- P2 download (the "version not installed → fetch" half). This declares +
  resolves locally; auto-fetch is P2.
- rollForward semantics beyond `exact`.

## Open Questions
- [ ] sidecar 命名：`<app>.runtimeconfig.json`（.NET 同款，本 spec 取此）。
