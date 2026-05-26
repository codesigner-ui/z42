# Tasks: Tar.ExtractTo — extract archive entries to filesystem directory

> 状态：🟢 已完成 | 创建：2026-05-26 | 归档：2026-05-26

**变更说明：** 给 `Std.Archive.Tar` 添加便利方法 `ExtractTo(byte[] tarBytes, string destDir) → int`：解压所有 entry 到 destDir，自动 mkdir-p 父目录，根据 entry mode 设置可执行位。

**原因：** 移植 `install-node-local.sh`（下载 Node.js LTS tarball 后 `tar -xzf` 到 `artifacts/tools/node/`）等脚本的关键 API。当前用户得手写 entry 循环 + `File.WriteAllBytes` + `Directory.Create` + `File.MakeExecutable`，30+ LOC 拼装。`ExtractTo` 收成 1 行调用。

**类型：** 最小化（pure z42 stdlib 扩展，复用现有 `Std.IO.File` / `Directory` / `Path` API；无新 native，无新 IR）。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.compression/src/Tar.z42` | MODIFY | 加 `ExtractTo(byte[], string) → int` 静态方法 |
| `src/libraries/z42.compression/tests/tar_extract.z42` | NEW | 6 [Test]：单文件 / 嵌套目录 / mode 0755 → MakeExecutable / 空 tar / 已存在目标 / overwrite |
| `src/libraries/z42.compression/README.md` | MODIFY | Archive 段加 ExtractTo 示例 |
| `src/libraries/z42.compression/z42.toml` | MODIFY (if needed) | 添加 `z42.io` 依赖（确认是否已有） |
| `docs/design/stdlib/compression.md` | MODIFY | 把 Deferred → "compression-future-streaming-decode" 段补充说明：v0 `ExtractTo` 是 `byte[]` 入，真流式（Stream-input `ExtractStream`）仍属 deferred |

**只读引用：**
- `src/libraries/z42.io/src/{File,Directory,Path}.z42` — 复用 `MakeExecutable / Create / Join / GetDirectoryName / WriteAllBytes`
- `scripts/install-node-local.sh:88-92` — 验证 API 形态匹配 `tar -xzf` 用法

## 文档影响
- compression README：是（新公开 API）
- compression.md Deferred：是（标注 ExtractTo 已落地、真 streaming pipeline 仍 Deferred）

## Tasks

- [x] 1.1 z42.compression 已依赖 z42.io（确认无需改动 `z42.compression.z42.toml`）。
- [x] 1.2 在 `Tar.z42` 添加 `public static int ExtractTo(byte[], string)`（含 `_WriteBytesToFile` 用 `FileStream + try/finally` cleanup、`_EnsureSafeEntryName` Zip-Slip 防护）。
- [x] 1.3 新增 `tests/tar_extract.z42` 7 个 [Test] 全部 GREEN：单文件、嵌套目录、exec 位、空 tar、overwrite、`..` 拒绝、绝对路径拒绝。
- [x] 1.4 更新 `compression/README.md` 加 ExtractTo 示例。
- [x] 1.5 更新 `docs/design/stdlib/compression.md` `compression-future-streaming-decode` 段加 Tar 配套缺口说明。
- [x] 1.6 `./scripts/test-stdlib.sh z42.compression` 全绿（含 7 个新 case + 18 既有 = 25/25）。
- [x] 1.7 归档 + commit + push。

## 实施期发现 & 修复

1. **Pre-existing latent bug：`new string(chars)` 对空 char[] 返 Null** —— `Tar._ReadStr` 使用 `new string(chars)` 在 ustar prefix 字段全 NUL 时返回 `null` 而非 `""`，导致下游 `.Length > 0` 比较 `I64(0) vs Null` 类型错误。
   - **根因**：`new string(...)` syntax 在 z42 不是 documented 的 String 构造路径（只有 `Std.String.FromChars(char[])` intrinsic 走 `__str_from_chars` native）；编译器未拒绝，但运行时对空 char[] 返 Null。
   - **范围内修复**：本 spec 改 `Tar._ReadStr` 用 `String.FromChars(chars)`。
   - **范围外残留**：`Zip.z42:157` 同样 pattern；未触发是因为 zip header 几乎从不出现"完全空 string field"。记入 backlog 由独立 cleanup spec 修。
   - **Lesson**：z42 stdlib 应只用 documented 的 `String.FromChars`；`new string(...)` 应在编译器禁用或正确实现。
2. **`File.CreateTempDir` 不在 `Directory`**：第一版测试写 `Directory.CreateTempDir(...)`，运行时报 undefined function。Native binding 在 `File` 类，sed 一把改完。Stdlib 命名归类不太一致（`Directory.Create` mkdir 但 `File.CreateTempDir` 创目录），保持现状不动；本次只跟随既有 API 而非重构。
3. **工作树同步churn**：实施期工作树状态变化（其他 in-flight session 改写 metadata files、丢掉 csprng Cargo.toml 改动）。我的 Tar.z42 edits 被 reset 一次，重新 apply 后继续。结论：每轮编辑前用 `git diff` 验证基线。

## 备注
- **v0 是 buffered**：`Tar.Read` 拿 `byte[]` 全在内存，所以 30 MB tarball 解压期间峰值内存 ~250 MB（30 MB 压缩 + 120 MB 解压 + entries 数组引用）。对 install-node-local.sh 单次 dev-machine 使用可接受。真流式（`Tar.ExtractStream(Stream src, string dir)` 配合 Gzip true-streaming decode）属于 deferred。
- **Zip Slip 防御**：CVE-2018-1002200 类攻击 — tar entry name 含 `../` 或绝对路径时可写出 destDir 外。本方法默认拒绝，无 opt-in（z42 现在没 raw-mode 需求；以后真要可加 `ExtractToUnsafe`）。
- **不返回 entry 列表**：调用方需要明细就先 `entries = Tar.Read(bytes); for ... { ExtractEntryTo }`；ExtractTo 设计成 "fire and check count"。
- **Permission bits**：只复用 `MakeExecutable`（任何 `0o111` 位置位即触发），不实现 setuid / sticky / per-owner-class mode。脚本场景需要更细粒度时另议。
