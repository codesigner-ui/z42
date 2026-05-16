# Tasks: extend z42.io — Glob + TempDir + TempFile

> 状态：🟢 已完成 | 创建：2026-05-16 | 完成：2026-05-16 | 类型：feat（新 native extern + stdlib wrapper）
> Spec 类型：minimal mode

## 背景

Phase 0b of "shell scripts → z42 self-hosting"：试点脚本 `check-versions-drift` /
`build-stdlib` / `test-stdlib` 需要的最小文件系统补丁集。

| 缺口 | shell 等价 | 新 API |
|------|-----------|--------|
| Glob 匹配（单层 `*`/`?`） | `for f in $dir/*.zpkg` | `Path.Glob(dir, pattern) → string[]` |
| 临时目录 | `mktemp -d` | `File.CreateTempDir(prefix) → string` |
| 临时文件 | `mktemp` | `File.CreateTempFile(prefix, suffix) → string` |

## 设计决策

| Decision | 选项 | 决定 | 理由 |
|----------|------|------|------|
| 1. Glob 语法 | (a) `*`/`?` 单层 / (b) `**` 递归 / (c) full fnmatch | (a) | v0 简单；递归留 follow-up（Directory.EnumerateRecursive + filter 可代替）|
| 2. Glob 返回 | basename / 全路径 | 全路径 | 调用方常需要传给后续 file ops；不需要再 join |
| 3. Glob 实现 | rust `glob` crate / 手写 | 手写 | 30 行状态机；不引入新 dep（最小化 runtime size）|
| 4. Glob 顺序 | OS-order / sorted | sorted | 同 `Directory.Enumerate` 行为，可重现 |
| 5. TempDir 位置 | `std::env::temp_dir()` / 自定 | std::env::temp_dir() | 跨平台（mac /var/folders、linux /tmp、win %TEMP%） |
| 6. TempDir 防冲突 | timestamp + pid / `tempfile` crate / counter | timestamp + nanos + pid | 单进程内冲突可能性 ~0；不引入新 dep |
| 7. TempFile 后缀 | (a) 必填 / (b) 可选 | 必填（可空字符串） | 显式 |
| 8. TempXxx 命名 | `prefix.{rand}` / `{rand}.prefix` | `prefix.{rand}.{suffix}` | mktemp 风格 |
| 9. cleanup | 自动 / 手动 | 手动 | z42 无 RAII / finalizer 保证；调用方自己 `File.Delete` / `Directory.Delete(path, true)` |
| 10. Glob 大小写 | sensitive / insensitive | sensitive | 同 POSIX shell；调用方需要 insensitive 自己 lower-case |

## 阶段 1: Rust runtime impl

- [x] 1.1 NEW `src/runtime/src/corelib/fs.rs::builtin_path_glob` — 简单 `*`/`?` 状态机 + read_dir filter
- [x] 1.2 NEW `src/runtime/src/corelib/fs.rs::builtin_file_create_temp_dir`
- [x] 1.3 NEW `src/runtime/src/corelib/fs.rs::builtin_file_create_temp_file`
- [x] 1.4 MODIFY `src/runtime/src/corelib/mod.rs` — 注册 3 个 builtin

## 阶段 2: z42 stdlib wrapper

- [x] 2.1 MODIFY `src/libraries/z42.io/src/Path.z42` — `Glob` extern + doc comment
- [x] 2.2 MODIFY `src/libraries/z42.io/src/File.z42` — `CreateTempDir` / `CreateTempFile` extern + doc

## 阶段 3: 测试

- [x] 3.1 NEW `src/libraries/z42.io/tests/path_glob.z42` — 基本 `*` 匹配 / `?` 匹配 / 无匹配 / 跨子目录隔离
- [x] 3.2 NEW `src/libraries/z42.io/tests/file_temp.z42` — CreateTempDir 唯一性 + CreateTempFile 唯一性 + 实际可写 + cleanup

## 阶段 4: GREEN + 归档

- [x] 4.1 `cargo build --manifest-path src/runtime/Cargo.toml --release` 通过
- [x] 4.2 `./scripts/build-stdlib.sh release` 全绿
- [x] 4.3 `./scripts/test-stdlib.sh z42.io` 全绿
- [x] 4.4 `./scripts/test-stdlib.sh` 整体不回归
- [x] 4.5 mv → `docs/spec/archive/2026-05-16-extend-z42-io-glob-temp/`
- [x] 4.6 commit + push
