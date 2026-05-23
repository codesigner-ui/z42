# Proposal: add z42.yaml

## Why

YAML is the third leg of the "z42 reads any common config" trio
alongside `z42.toml` (manifests) and `z42.json` (APIs, logs). Use
cases:

- **CI / GitHub Actions configs**: `.github/workflows/*.yml` —
  self-hosted z42 build-driver scripts will want to parse + maybe
  generate them rather than shell out to `yq`.
- **Helm / Kubernetes manifests**: any z42 tool that touches container
  orchestration metadata.
- **Ansible playbooks / OpenAPI / Swagger specs**: same pattern —
  parse the file to learn structure, transform, emit.
- **Internal config files**: many small CLIs use YAML for app
  settings, dev tools, log routing.

z42.toml/z42.json already shipped; YAML completes the canonical
"config format" set. Pure-script mirror of those two — no language
changes, no FFI, depends only on `z42.core`.

Roadmap places z42.yaml at P2 ([`docs/design/stdlib/roadmap.md`](../../../design/stdlib/roadmap.md#p2--中等优先))
adjacent to z42.toml / z42.linq. This proposal cashes it in.

## What Changes

1. **New stdlib package `z42.yaml`** (depends only on `z42.core`).
2. **Public API** (single namespace, mirrors `Std.Toml` / `Std.Json`):
   - `Std.Yaml.YamlValue` — discriminated value type with
     `Is*()` predicates + `As*()` accessors + `Get(key)` /
     `At(index)` for nested traversal
   - `Std.Yaml.Yaml.Parse(text)` — text → `YamlValue` tree
   - `Std.Yaml.Yaml.Stringify(value)` — `YamlValue` → text (block
     style; flow style for compact output via overload)
   - `Std.YamlException` (in `Std` namespace, mirroring
     `Std.TomlException` / `Std.JsonException`)
3. **YAML 1.2 subset** (sufficient for ~95% of real-world configs):
   - **Scalars**: plain / single-quoted / double-quoted strings;
     integers (decimal); floats (incl. `1.5e10` exponent); booleans
     `true` / `false`; null (`~` or empty)
   - **Block mapping**: `key: value\n` with indentation-based nesting
   - **Block sequence**: `- item\n` with same indentation rules
   - **Flow mapping**: `{ key: value, ... }`
   - **Flow sequence**: `[ value, ... ]`
   - **Comments**: `# ...` (end-of-line and standalone)
   - **Document marker `---`** (start) — supported but ignored (we
     parse only the first document)
   - **UTF-8 source** only
4. **Workspace + build-stdlib + index.json registration.**
5. **README + design doc** (`docs/design/stdlib/yaml.md`).
6. **No new VM builtins** — pure z42 atop `String` / `byte[]` /
   `List<T>` / `Dictionary<K,V>`. Same pattern as `z42.toml` / `z42.json`.

## Scope

**修改 / 新增**：

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.yaml/` | NEW dir | 包根目录 |
| `src/libraries/z42.yaml/z42.yaml.z42.toml` | NEW | 包 manifest |
| `src/libraries/z42.yaml/src/YamlValue.z42` | NEW | discriminated value type |
| `src/libraries/z42.yaml/src/YamlParser.z42` | NEW | parser |
| `src/libraries/z42.yaml/src/YamlWriter.z42` | NEW | stringify |
| `src/libraries/z42.yaml/src/YamlException.z42` | NEW | `Std.YamlException : Exception` |
| `src/libraries/z42.yaml/tests/parse_basic.z42` | NEW | scalar + block mapping cases |
| `src/libraries/z42.yaml/tests/parse_block.z42` | NEW | nested block mapping + block sequence |
| `src/libraries/z42.yaml/tests/parse_flow.z42` | NEW | flow mapping / flow sequence |
| `src/libraries/z42.yaml/tests/parse_strings.z42` | NEW | plain / single / double quotes |
| `src/libraries/z42.yaml/tests/parse_errors.z42` | NEW | malformed → YamlException |
| `src/libraries/z42.yaml/tests/stringify.z42` | NEW | block-style round-trip |
| `src/libraries/z42.workspace.toml` | MODIFY | default-members 加 `z42.yaml` |
| `scripts/build-stdlib.z42` | MODIFY | `_stdlibList()` + `_indexJson()` 加 z42.yaml + `Std.Yaml` |
| `src/toolchain/host/platforms/wasm/js/stdlib/index.json` | MODIFY | 加 `Std.Yaml` → `z42.yaml.zpkg` |
| `src/toolchain/host/platforms/ios/Resources/stdlib/index.json` | MODIFY | 同 |
| `src/toolchain/host/platforms/android/z42vm/src/main/assets/stdlib/index.json` | MODIFY | 同 |
| `docs/design/stdlib/yaml.md` | NEW | 设计文档 |
| `docs/design/stdlib/roadmap.md` | MODIFY | "已落地" + Deferred Backlog Index |
| `docs/design/stdlib/overview.md` | MODIFY | 包列表加 z42.yaml |
| `src/libraries/z42.yaml/README.md` | NEW | 简介 |

**只读引用**：

- `src/libraries/z42.toml/` — 镜像参考（kind discriminator / parallel arrays pattern）
- `src/libraries/z42.json/` — 同
- `docs/design/stdlib/toml.md` + `json.md` — 设计文档结构模板

## Out of Scope

- **Anchors / aliases** (`&anchor` / `*anchor`) —
  `yaml-future-anchors`
- **Tags** (`!str`, `!!binary`, custom tags) — `yaml-future-tags`
- **Multi-line scalars** (`|` literal, `>` folded) —
  `yaml-future-multiline-strings`
- **Multiple documents per file** — `yaml-future-multi-doc`
- **Complex keys** (non-string keys, `? key` syntax) —
  `yaml-future-complex-keys`
- **Date / timestamp scalars** — `yaml-future-timestamps` (z42.time
  can be wired in via custom callers; not in v0 surface)
- **Octal / hex int literals** (YAML 1.2 supports both) —
  `yaml-future-numeric-bases`
- **YAML 1.1 quirks** (`yes`/`no`/`on`/`off` as bool) — explicitly
  out: YAML 1.2 normalised these away and Norway Problem is a
  well-documented foot-gun

## Open Questions

- [ ] **Indentation rule strictness**: spec says indentation = number
  of leading spaces (no tabs); enforce strictly (error on tab) or
  silently normalise? Lean toward **strict** matching libyaml / PyYAML.
- [ ] **Empty mapping value**: `key:\n` produces null or empty string?
  YAML 1.2 says null. Adopt null.
- [ ] **Stringify ordering**: preserve insertion order (mirrors
  TomlWriter) — confirmed via parallel-array storage in YamlValue.
