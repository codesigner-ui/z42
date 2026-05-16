# Tasks: fix deep-namespace import discovery

> 状态：🟢 已完成 | 创建：2026-05-16 | 完成：2026-05-16 | 类型：fix
> Spec 类型：minimal mode

**变更说明：** VM 的 `extract_import_namespaces_from_module` 仅取每个 call target 的"前两段"作为 namespace 候选；3+ 段 namespace（如 `Std.IO.Binary.BinaryWriter.WriteByte`）拿到 `Std.IO`，错过真正声明该 namespace 的 zpkg → lazy loader 不加载 → VM 报 `undefined function`。

**原因：** `Std.IO.Binary` 是首个三段 stdlib namespace；旧 heuristic 假设 `<ns>.<class>.<method>` 总是 4 段或更少。`add-z42-io-binary` 因此被迫降级到 `Std.Binary`（两段）workaround，留下 deferred `io-binary-future-nested-ns`。

**根因修复：** `infer_namespace` 改为 `infer_namespace_candidates`，emit 所有 `.`-bounded prefixes（`Std`, `Std.IO`, `Std.IO.Binary`, `Std.IO.Binary.BinaryWriter`），让 `resolve_namespace` 对每个 prefix 试着匹配真实 zpkg。无效 prefix 自然 lookup miss，不污染状态。物理上不再可能漏掉深层 namespace。

**文档影响：**
- `docs/design/stdlib/io-binary.md` — 删除 `io-binary-future-nested-ns` Deferred 条目；namespace 改回 `Std.IO.Binary`
- `docs/design/stdlib/organization.md` — 删除 namespace caveat
- 顺带在同次 spec 把 z42.io.binary 的 `Std.Binary` workaround 还原为 `Std.IO.Binary`（解 deferred）

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/metadata/loader.rs` | MODIFY | `extract_import_namespaces_from_module` / `extract_import_namespaces` 调用新 `infer_namespace_candidates`；旧 `infer_namespace` 删除 |
| `src/runtime/src/metadata/loader_tests.rs` | MODIFY | 测试用例更新到 prefix 集合语义（含 `deeper_namespace_emits_three_segment_prefix` 回归） |
| `src/libraries/z42.io.binary/src/BinaryReader.z42` | MODIFY | `namespace Std.Binary;` → `namespace Std.IO.Binary;` |
| `src/libraries/z42.io.binary/src/BinaryWriter.z42` | MODIFY | 同上 |
| `src/libraries/z42.io.binary/tests/binary_basic.z42` | MODIFY | `using Std.Binary;` → `using Std.IO.Binary;` |
| `src/libraries/z42.io.binary/tests/binary_errors.z42` | MODIFY | 同上 |
| `src/libraries/z42.io.binary/tests/binary_strings.z42` | MODIFY | 同上 |
| `docs/design/stdlib/io-binary.md` | MODIFY | 删除 `io-binary-future-nested-ns` Deferred；namespace 改回；Decision 8 后置注 resolved |
| `docs/design/stdlib/organization.md` | MODIFY | z42.io.binary 行的 namespace caveat 去掉 |

只读引用：
- `docs/spec/archive/2026-05-15-add-z42-io-binary/tasks.md` — 历史上下文（不动 archive）

## Tasks

- [x] 1.1 `src/runtime/src/metadata/loader.rs` — `infer_namespace` → `infer_namespace_candidates`（emit 所有 prefix）；call sites 用 iterator
- [x] 1.2 `src/runtime/src/metadata/loader_tests.rs` — 单测全部对齐新语义 + 新增 `deeper_namespace_emits_three_segment_prefix`
- [x] 1.3 `cargo build --release` + 单测 GREEN
- [x] 1.4 `z42.io.binary` 源 + tests namespace 改回 `Std.IO.Binary`
- [x] 1.5 `./scripts/test-stdlib.sh z42.io.binary` GREEN（24 tests）
- [x] 1.6 `./scripts/test-stdlib.sh` 全量不回归（52 tests / 15 libs）
- [x] 1.7 `./scripts/test-vm.sh` GREEN（316 tests, interp + JIT）
- [x] 1.8 `dotnet test src/compiler/z42.Tests` GREEN（1288 tests）
- [x] 1.9 `docs/design/stdlib/io-binary.md` 删除 deferred + 改 namespace 行
- [x] 1.10 `docs/design/stdlib/organization.md` 去掉 namespace caveat
- [x] 1.11 commit + push
- [x] 1.12 mv → `docs/spec/archive/2026-05-16-fix-deep-namespace-import-discovery/`

## 备注

- `index.json` 已映射 `Std.IO.Binary` → `z42.io.binary.zpkg`（前一迭代误以为 workaround，实际本来就对）；无需改 build-stdlib.sh
- 旧测试 `single_import_extracts_two_component_namespace` 名字本身记的就是 bug，重命名 `single_import_emits_all_prefixes`
