# Tasks: z42.io ergonomics — bytes / glob / temp-dir alias

> 状态：🟢 已完成（部分 GREEN，见备注） | 创建：2026-05-27 | 归档：2026-05-27

**变更说明：** 3 个 z42.io 易用性 API：
1. `File.ReadAllBytes(path) → byte[]` + `File.WriteAllBytes(path, bytes)` —— 一次性二进制 IO（与 `ReadAllText/WriteAllText` 对称）
2. `Path.GlobRecursive(dir, pattern) → string[]` —— 递归 `**` 风格 glob（当前 `Path.Glob` 只直接子项）
3. `Directory.CreateTempDir(prefix) → string` —— `File.CreateTempDir` 的 alias，符合"目录操作在 Directory"直觉

**原因：** scripts/ 移植里反复用到。B2 的 `Tar.ExtractTo` 实施期就被迫写 `FileStream + try/finally + WriteAllBytes` 三件套（30 LOC 凑一次写文件）；B2 测试期发现 `Directory.CreateTempDir` 不存在（在 `File` 类下）—— 这次顺手统一。`Path.GlobRecursive` 是 regen-golden-tests.sh 移植的直接 blocker（它要扫 `src/tests/<cat>/<name>/source.z42`）。

**类型：** 最小化（stdlib 扩展 + 2 个新 Rust native binding，无新 IR/VM 语义）。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/corelib/fs.rs` | MODIFY | 新增 `builtin_file_read_bytes` / `builtin_file_write_bytes`（~30 LOC each） |
| `src/runtime/src/corelib/mod.rs` | MODIFY | BUILTINS 表追加 2 项 `__file_read_bytes` / `__file_write_bytes` |
| `src/libraries/z42.io/src/File.z42` | MODIFY | 加 `ReadAllBytes` + `WriteAllBytes` 公开方法 + `[Native]` extern |
| `src/libraries/z42.io/src/Directory.z42` | MODIFY | 加 `CreateTempDir(prefix)` static 方法（delegate 到 `File.CreateTempDir`）|
| `src/libraries/z42.io/src/Path.z42` | MODIFY | 加 `GlobRecursive(dir, pattern)` static 方法（pure z42，atop `Directory.EnumerateRecursive` + 既有 glob matcher）|
| `src/libraries/z42.io/tests/file_bytes.z42` | NEW | 6 [Test]：round-trip / 空 / 大文件 (10KB) / 二进制非UTF8 / 路径不存在 / overwrite |
| `src/libraries/z42.io/tests/path_glob_recursive.z42` | NEW | 5 [Test]：单层 / 嵌套 2 层 / 嵌套 3 层 / pattern miss / 空目录 |
| `src/libraries/z42.io/tests/directory_temp.z42` | NEW | 2 [Test]：`Directory.CreateTempDir` 等价 `File.CreateTempDir` + 路径存在 |
| `src/libraries/z42.io/README.md` | MODIFY | File / Directory / Path 三段补充新 API |

**只读引用：**
- `src/runtime/src/corelib/fs.rs::builtin_file_read_text` — 参考 IO 错误 → `Std.Exception` 包装风格
- `src/libraries/z42.compression/src/Tar.z42` — B2 的 `_WriteBytesToFile` helper 是本次的简化目标
- `scripts/regen-golden-tests.sh:88-109` — 验证 `Path.GlobRecursive` API 形态匹配 bash 路径

## 文档影响
- z42.io README：是（新 API 表）
- design doc：不需要新建（行为对标 .NET BCL `File.ReadAllBytes` 等）

## Tasks

- [x] 1.1 Rust `fs.rs::builtin_file_read_bytes`：调 `std::fs::read(path)` → `Value::Array<i64-byte>`；IO 错（not found / permission）走现有 `anyhow` 路径包成 z42 Exception
- [x] 1.2 Rust `fs.rs::builtin_file_write_bytes`：参数 `(path, byte[])`，解码 byte array → `Vec<u8>` → `std::fs::write(path, bytes)`
- [x] 1.3 Rust `mod.rs` BUILTINS 末尾加 2 行：`("__file_read_bytes", fs::builtin_file_read_bytes)` 和 `("__file_write_bytes", fs::builtin_file_write_bytes)` —— 套路 spec id 段注释 "add-z42-io-ergonomics-bytes-glob"
- [x] 1.4 z42 `File.z42` 加 `[Native("__file_read_bytes")] public static extern byte[] ReadAllBytes(string path);` + 对称 `WriteAllBytes(string path, byte[] data)`
- [x] 1.5 z42 `Directory.z42` 加 `public static string CreateTempDir(string prefix) { return File.CreateTempDir(prefix); }` —— 纯 alias，避免新 Native
- [x] 1.6 z42 `Path.z42` 加 `public static string[] GlobRecursive(string dir, string pattern)`：
  - 用 `Directory.EnumerateRecursive(dir)` 拿所有相对 path
  - 对每个 path 按 `pattern` 匹配（支持 `*` 和 `?`，跨 `/` 边界）
  - 返回**绝对路径**排序数组（与 `Path.Glob` 一致 sorted）
  - **不**让 `**` 是显式 wildcard；递归本身就是"全展开"
- [x] 1.7 写 3 个新测试文件（按 Scope 表）
- [x] 1.8 更新 `z42.io/README.md` File / Directory / Path 三段
- [~] 1.9 验证：**部分** —— Rust lib `cargo build --release --lib` 干净 ✓；stdlib workspace build 22/22 ✓；但 `./scripts/test-stdlib.sh z42.io` **被另一个并行 in-flight session 的 Arc<str> 重构副作用阻塞**（test-runner `s.clone()` 类型错），见备注。13 个 [Test] 已写就绪，下个 session pick up 时自动收
- [x] 1.10 归档 + commit + push（hunk-pick 避开工作树 in-flight 改动；本次 mod.rs 只新增 2 行接续 add-process-which 段后面）

## 备注

- **为什么加 Rust native 而非纯 z42**：`ReadAllBytes` 走 `FileStream + ReadAllBytes()` 路径已可工作，但每次新建 slot + close 有开销；30 MB 文件级别能差出 10×。`WriteAllBytes` 同理。这是高频脚本 API，值得专门 native。
- **`Path.GlobRecursive` 实现选择**：用 `Directory.EnumerateRecursive` + 字符串匹配（不走 Rust）。理由：递归代码已有；glob matcher 复用 `_GlobMatch` 内部 helper（如果 `Path.Glob` 有可复用）；纯 z42 调试容易；性能对开发脚本足够（不是 hot path）。
- **Glob 语义**：`*` 匹配除 `/` 外任意字符；`?` 匹配单字符。Pattern 不带 `/` 时只匹配 basename；带 `/` 时匹配相对路径。例如：
  - `GlobRecursive("src", "*.z42")` 找所有子目录里的 `.z42`
  - `GlobRecursive("src", "tests/*.z42")` 找名为 `tests` 的目录下的 `.z42`
- **不实现 `**`**：`Directory.EnumerateRecursive` 本身就递归，pattern 里再加 `**` 会引入双重递归语义复杂。如果用户需要"递归 + 跨深度模式"用 EnumerateRecursive + 自定义 filter。
- **不破坏 B2 `Tar.ExtractTo._WriteBytesToFile`**：那是 B2 的内部 helper，本次不删；后续可改成调 `File.WriteAllBytes`（独立 cleanup spec）。

## 实施期发现 / 验证状态

1. **`test-runner` 编译阻塞** — `src/toolchain/test-runner/src/runner.rs:175` 的 `Value::Str(s) => s.clone()` 在 in-flight Arc<str> 重构（44 文件）后类型错（`Arc<str>` clone 给 `String` 字段）。修复是 `s.to_string()` 单字符串改动，但属于 toolchain scope，跨 spec；本变更不动它。User 已确认 Option A：commit + push 现状，下个 session 把 Arc<str> 重构收尾后 13 个 [Test] 自动 GREEN。
2. **`artifacts/` 反复被另一 session 清理** — 实施期跑 manual smoke test 失败的原因。Rust lib + stdlib workspace 都成功构建，但 end-to-end z42c→z42vm smoke 没跑通。下次清单。
3. **hunk-pick `fs.rs` only**：working tree 里 44 个文件被并行 Arc<str> 重构污染（如 `Value::Str(text)` → `Value::Str(text.into())`）。commit 时只 stage 我的 `builtin_file_read_bytes / builtin_file_write_bytes` 两段，其余留给 Arc<str> spec 自己 commit。
4. **Path.GlobRecursive 复杂度**：实现 92 LOC 含 `_globMatch` 递归 backtracking + 简单 insertion sort（z42.collections 没公开 sort 入口）。性能对开发脚本足够；非 hot path。
