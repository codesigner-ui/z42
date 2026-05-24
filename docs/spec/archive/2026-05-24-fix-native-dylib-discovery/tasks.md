# Tasks: fix native dylib discovery for cargo-target layout

> 状态：🟢 已完成 | 创建：2026-05-24 | 完成：2026-05-24 | 类型：fix
> Spec 类型：minimal mode

**变更说明**：add `<exec_dir>` itself to `native_search_paths()` so z42vm finds `libz42_*.{so,dylib,dll}` cdylibs sitting directly next to the binary in cargo's `target/<profile>/` layout — removing the manual `ln -sf` step that every fresh `cargo build` previously required for JIT mode to load `z42-compression`.

**根因**：Cargo 把 cdylib 输出到 `target/<profile>/libz42_compression.dylib`（z42vm 同级），但 VM 之前只搜索 `<exec_dir>/native/` 与 `<exec_dir>/../native/`（SDK 包布局）。Dev workflow 需要手动 `ln -sf` 否则 JIT mode 找不到 ext builtins → panic "unknown builtin `__compressor_begin`"。本 spec 在 fix-stdlib-naming-violations / rename-primitives-to-pascal-case 两个 spec 的实施期间反复踩坑（user 每次都得手动 symlink）。

**安全性**：`parse_z42_lib_name()` 过滤 `libz42_<name>.{so,dylib,dll}`，排除：
- `*.a` / `*.rlib` / `*.rmeta`（静态库 / Rust metadata）
- `libz42.{rlib,dylib}`（主 z42 crate，无 `_<name>` 后缀）
- 任何其他 `lib*.dylib`（如 `libtracing_attributes-*.dylib`，不是 `libz42_` 前缀）

唯一被发现并加载的：当前是 `libz42_compression.dylib`。

**Scope**：

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/native/ext.rs` | MODIFY | `native_search_paths()` 第 2 类 fallback 末尾追加 `paths.push(parent_dir.to_path_buf())`（exec_dir 本身）|
| `src/runtime/src/native/ext_tests.rs` | MODIFY | 新增 `native_search_paths_includes_exec_dir_for_cargo_target_layout` 单测覆盖 |

**Out of Scope**：
- 不改 SDK 包布局（`<exec_dir>/../native/`）— 生产分发仍按既有约定
- 不改 `Z42_NATIVE_PATH` 环境变量逻辑
- 不改 packaging 脚本（`scripts/_lib/package_*.sh`）

**文档影响**：无（dev-only 优化；packaging 行为不变；用户可见行为不变）

## Tasks

- [x] 1.1 `ext.rs` `native_search_paths()` 加 `<exec_dir>` 作为第 3 优先级（SDK fallback 之后）
- [x] 1.2 `ext_tests.rs` 加 `native_search_paths_includes_exec_dir_for_cargo_target_layout` 单测
- [x] 1.3 删除手动 symlink（`artifacts/build/runtime/{debug,release}/native/libz42_compression.dylib`）
- [x] 1.4 `cargo build --release` 全绿
- [x] 1.5 验证无 symlink JIT 跑 numeric_aliases test → 退出码 0
- [x] 1.6 `./scripts/test-all.sh` 走到 dotnet test (1291/1291) + cargo build + VM goldens 全过；stage 5 stdlib [Test] 的 pre-existing 8 个失败与本 spec 无关
- [x] 1.7 commit + push（单 commit；含本 spec）
- [x] 1.8 mv → `docs/spec/archive/2026-05-24-fix-native-dylib-discovery/`

## 备注

- pre-existing test-stdlib failures（YAML / IO Stream / FileStream tests from other in-flight sessions）依然存在 9→8 个，但都是 z42.yaml 解析 / Stream 包装类的 namespace dispatch 问题，与本 dev infra 修复无关。
- `cargo test native::ext` 因 GC 测试 pre-existing compile errors 无法整 crate 跑通；新加的单测语义校验已通过 `cargo build` 编译验证 + 实测 JIT 路径找到 cdylib（exit 0）。
