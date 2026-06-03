# `artifacts/` build-output layout

> Authoritative map of the repo's `artifacts/` tree. The whole directory is
> git-ignored (`.gitignore` line `artifacts/`) — nothing here is committed;
> this doc is the single source of truth for *where build outputs go* and
> *why*. Established by `reorg-artifacts-layout` (2026-06-04).

## Three top-level buckets

```
artifacts/
├── build/      # compile intermediates + per-component outputs; subdirs MIRROR src/
├── packages/   # final assembled SDK packages + archives (copied/staged from build/)
└── deps/       # third-party tools/binaries the build fetches (see "deps" below)
```

The split is the conventional **intermediate / output / vendored** model
(cargo `target` + an install prefix + `vendor`):

- **`build/`** = "things we compiled." One subdir per `src/` component, so the
  path is predictable from the source path.
- **`packages/`** = "things we ship." Assembled by copying selected `build/`
  outputs into the per-RID SDK package layout
  (see [`docs/spec/archive/2026-05-13-define-package-layout`](../../spec/archive/2026-05-13-define-package-layout/)).
- **`deps/`** = "things someone else gave us." Reserved for build-fetched
  third-party binaries (a pinned `wasm-opt`/`binaryen`, cross sysroots, …).
  Currently unpopulated — cargo/NuGet caches live in `~/.cargo` / `~/.nuget`,
  not here. The convention exists so such fetches have a home and a one-shot
  `rm -rf artifacts/deps` reset.

## `build/` mirrors `src/`

| `src/`                     | `artifacts/build/`                          | contents |
|----------------------------|---------------------------------------------|----------|
| `src/compiler/`            | `build/compiler/`                           | dotnet `bin`/`obj` for `z42c` |
| `src/runtime/`             | `build/runtime/<cargo-target>/<profile>/`   | cargo target: `z42vm`, `libz42.*`, the `z42` trampoline, `z42-test-runner` |
| `src/libraries/<lib>/`     | `build/libraries/<lib>/<profile>/`          | per-stdlib-lib compile (`dist/<lib>.zpkg` + `cache/`) |
| (derived aggregate)        | `build/libs/<profile>/`                     | flat single-dir view of all stdlib `.zpkg` + `index.json` — the `Z42_LIBS` lookup target. Not a `src/` mirror; see note below. |
| `src/toolchain/launcher/`  | `build/toolchain/launcher/`                 | `z42.launcher.zpkg` (toml `out_dir`) + `home/` (dev `$Z42_HOME`) |
| `src/tests/`               | — (no build output)                         | |

### `build/libs` — the one non-mirror (and why it stays for now)

The stdlib needs a single flat directory the VM points `Z42_LIBS` at,
populated by `build-stdlib.sh` from each `build/libraries/<lib>/.../dist`.
It's the only `build/` subdir with no `src/` counterpart, so a tidier name
would be `build/libraries/_flat`. **It is deliberately *not* renamed**: the
flat-view path is a **baked-in runtime contract** — `z42vm` hardcodes
`artifacts/build/libs/{release,debug}` as its dev-mode `Z42_LIBS` fallback
([`src/runtime/src/main.rs`](../../../src/runtime/src/main.rs), `config.rs`,
`host_tests.rs`). Renaming `build/libs` would require changing that VM default
+ recompiling + updating the host tests — a runtime-behavior change, not a
cosmetic move. That's tracked as a separate follow-up
(`reorg-artifacts-future-libs-flat`) rather than folded into this layout
refactor.

### Why the launcher lives under `build/toolchain/launcher`

- **`launcher.zpkg`** is the launcher core's build product. It used to emit
  to `src/toolchain/launcher/core/dist/` — a build output *inside the source
  tree*, the one place that violated "outputs go to `artifacts/`." Its toml
  `out_dir` now points up into `build/toolchain/launcher/`.
- **`home/`** is the dev `$Z42_HOME` that `scripts/_lib/launcher-env.sh`
  assembles (a throwaway "fake `~/.z42`": `launcher/{z42vm, launcher.zpkg,
  libs→…}` + linked dev runtime) so ported scripts run as Exe-zpkgs via the
  launcher without touching the user's real `~/.z42`. It's an ephemeral
  dev/test install, not a compile output — but it's launcher-derived, so it
  sits under the launcher's `build/` subdir rather than warranting a separate
  top-level bucket. (`rm -rf` it anytime; `setup_launcher_env` rebuilds it.)
  The `z42` trampoline binary itself stays in `build/runtime/<…>` because
  cargo emits all workspace binaries to the shared target dir.

## Notes

- `artifacts/` is wholly git-ignored, so `deps/` cannot carry a *tracked*
  README — the convention is documented here instead.
- A launcher-project `z42c clean` removes its `out_dir`
  (`build/toolchain/launcher`), including `home/`; both are regenerable.
