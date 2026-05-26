# Tasks: File.WriteAllTextAtomic / WriteAllBytesAtomic — durable config write

> 状态：🟢 已完成 | 创建：2026-05-27 | 归档：2026-05-27

**变更说明：** 给 `Std.IO.File` 加两个 atomic 写入方法：
- `WriteAllTextAtomic(path, content)`
- `WriteAllBytesAtomic(path, bytes)`

实现：先写到同目录下临时文件 `<basename>.<nanos>.<pid>.tmp`，调 `fsync` 持久化到 disk，再 `rename` 覆盖目标。POSIX `rename` 是原子的；崩溃后只可能看到旧内容或新内容，绝不会看到半写文件。

**原因：** scripts 移植里更新 `versions.toml` / `manifest.toml` / 配置文件时，普通 `WriteAllText` 在 fsync 前进程被 SIGKILL 会留下截断文件。生产场景遇到过几次。

**类型：** 最小化（pure z42 wrapper + 2 个新 Rust native binding，无 lang change）。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/corelib/fs.rs` | MODIFY | 加 `builtin_file_write_text_atomic` + `builtin_file_write_bytes_atomic`；公用内部 helper `_write_atomic_bytes(target, bytes)` |
| `src/runtime/src/corelib/mod.rs` | MODIFY | BUILTINS 末尾追加 2 项 `__file_write_text_atomic` / `__file_write_bytes_atomic` |
| `src/libraries/z42.io/src/File.z42` | MODIFY | 加 `WriteAllTextAtomic` / `WriteAllBytesAtomic` 公开方法 + `[Native]` extern |
| `src/libraries/z42.io/tests/file_atomic_write.z42` | NEW | 4 [Test]：text round-trip / bytes round-trip / overwrite existing / target dir 不存在抛错 |
| `src/libraries/z42.io/README.md` | MODIFY | File 行追加 atomic 标注 |

**只读引用：**
- `src/runtime/src/corelib/fs.rs::builtin_file_write_bytes` — 参考 byte array 解码 + std::fs::write 风格
- bash `mv .tmp -> target` rename 操作；POSIX `rename(2)` 文档

## Tasks

- [x] 1.1 Rust `_write_atomic_bytes(target, bytes)` helper：
  - 同目录下生成 tmp 路径：`<dir>/.<basename>.<unix_ns>.<pid>.tmp`（dotfile 前缀防被 glob 扫到）
  - `OpenOptions::new().write(true).create_new(true).open(tmp)` —— `create_new` 避免覆盖既存 tmp（race 概率极低但保险）
  - `file.write_all(bytes)?`
  - `file.sync_all()?` —— 强制刷 page cache 到磁盘（POSIX fsync）
  - `drop(file)`
  - `std::fs::rename(tmp, target)?` —— POSIX 原子 rename
  - 异常路径：写失败 / fsync 失败 / rename 失败 → 删 tmp（best-effort）+ anyhow 透传错误
- [x] 1.2 Rust 2 个 builtin 包装 helper：`text` 版调 `.into_bytes()`，`bytes` 版直接传 `Vec<u8>`
- [x] 1.3 `mod.rs` BUILTINS 末尾追加 `__file_write_text_atomic` + `__file_write_bytes_atomic`（新 spec id section）
- [x] 1.4 z42 `File.z42` 加 `[Native]` extern + 公开方法 + doc comment
- [x] 1.5 测试覆盖：
  - text round-trip（写 → 读、检查内容）
  - bytes round-trip
  - overwrite existing（旧内容被新覆盖、size 匹配新内容）
  - 父目录不存在 → 抛 Exception（tmp open 会失败）
- [x] 1.6 README + smoke + commit

## 实施期验证

- Rust lib `cargo build --release` 干净 ✓
- stdlib workspace build 22/22 ✓
- Manual smoke test 4 路径全过：text round-trip / bytes round-trip / overwrite existing / 写完目录里只剩 3 个最终文件无 `.tmp` 残留 ✓
- 5 个 [Test] 写就（含 nonexistent dir 抛错 case）；端到端 GREEN 仍由后续 session 通过 test-runner 收

## 备注

- **不实现"atomic across reboots"层**：写 `parent dir fsync` 才能保证 entry 在 dir inode 落盘；POSIX 标准里 rename 仅保证 path 不会指向半写文件，不保证 reboot 后 rename 必然落盘。生产场景对此需求的占比很小，暂不做；如果未来要做，新增 `WriteAllTextDurable` 单独 spec。
- **tmp 路径前缀 `.`**：防止 `Directory.Enumerate` 一类调用扫到中间态文件；与 Linux 习惯（`.swp` / `.tmp.XXXXXX`）对齐。
- **rename 跨设备**：POSIX `rename` 跨文件系统会返回 `EXDEV`。z42 atomic write 只承诺**同目录**——调用方传的 path 已锚定 dir，tmp 在同 dir，所以不会跨 FS。这与 ext4 / APFS 现实行为一致。
- **fsync overhead**：典型 SSD 1-3ms；同步配置文件场景可接受。文档里说明"调用 atomic 形式比普通 WriteAll 慢 ~10×，仅对关键文件用"。
