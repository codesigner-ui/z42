# Tasks: add-file-last-write-time

> 状态：🟢 完成 | 创建：2026-06-09 | 类型：feat (runtime native + stdlib)
> 模式：minimal —— 新增 fs native builtin + stdlib 绑定，不涉及 IR/lang 规范。
> 子系统锁：`runtime` + `stdlib`（与 add-reflection-mvp 例外共存，文件/区段分离，见 ACTIVE.md）。

## 背景

`migrate-scripts-to-z42` 把 `File.GetLastWriteTime`/mtime 标为 **P0 硬阻塞**：所有
freshness / incremental build / cache 检查都需要它；build-stdlib.z42 现用字母序绕过
mtime 是 hack。z42.io 此前无任何文件时间 API。

## 设计（BCL 对齐）

- **native extern**（`corelib/fs.rs`）：`__file_last_write_time_ms(path) → i64`，
  unix epoch 毫秒，单位对齐既有 `__time_now_ms`。`metadata()?.modified()?` →
  `duration_since(UNIX_EPOCH).as_millis() as i64`；pre-epoch → 0。文件不存在 →
  `metadata` 失败 → anyhow 错误上抛 Std.Exception。
- **stdlib**（`z42.io/File.z42`）：`GetLastWriteTime(path) → Std.Time.DateTime`，
  `DateTime.FromUnixMs(LastWriteTimeMs(path))` 包装；私有 `extern long LastWriteTimeMs`
  镜像 DateTime 自身的 `NowMs` 模式。z42.io 已依赖 z42.time → 无新依赖。

## 任务

- [x] 1. `corelib/fs.rs::builtin_file_last_write_time_ms`（+ `corelib/mod.rs` 注册，落 fs 区）
- [x] 2. `z42.io/File.z42`：`using Std.Time;` + `GetLastWriteTime` + 私有 `LastWriteTimeMs` extern
- [x] 3. 测试 `z42.io/tests/file_last_write_time.z42`：3 个（真实当前时间 >2020 / 重写后
      mtime 单调不减 / 缺文件抛异常）
- [x] 4. 文档：`docs/design/stdlib/overview.md` File 行 + 语义/原理注（单位、依赖、用途）
- [x] 5. GREEN：runtime crate + test-runner cargo build 干净（exit 0）· z42.io 45/45
      （含新 3）· 隔离验证（last-good z42.core.zpkg + 重建 test-runner，因并发
      add-reflection-mvp 的 z42.core 源瞬态破损，canonical workspace gate 暂不可跑）
- [x] 6. commit（surgical：mod.rs `git add -p` 只本变更行）+ push + 释锁归档

## 验证教训（已入 memory）

`test lib <lib> --no-build` 用 test-runner **内嵌 VM** 执行，不 fork z42vm；加新
native builtin 后**必须重建 test-runner**（`cargo build --manifest-path
src/toolchain/test-runner/Cargo.toml`），否则假报 `unknown builtin`。canonical 全
gate 本会重建 test-runner，只有 `--no-build` 隔离时踩此坑。见
`reference_test_runner_embedded_vm.md`。
