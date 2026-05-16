# Tasks: extend z42.io — script helpers (chmod / link / size / TTY / cwd)

> 状态：🟢 已完成 | 创建：2026-05-16 | 完成：2026-05-16 | 类型：feat（新 native extern + stdlib wrapper + 编译器 fix）
> Spec 类型：minimal mode

## 背景

Phase 0b round 2：覆盖 scripts/*.sh 剩余 IO 缺口。结合 Phase 0a (z42.cli) +
Phase 0b round 1 (Glob/TempDir/TempFile) + Stdio.Inherit（已有），脚本迁移
所需的全部 stdlib API 闭合。

| 缺口 | shell 等价 | 新 API |
|------|-----------|--------|
| chmod +x | `chmod +x` | `File.MakeExecutable(path)` |
| 硬链接 | `cp -l` / `ln src dst` | `File.Link(src, dst)` |
| 符号链接 | `ln -s` | `File.SymLink(src, dst)` |
| 文件字节数 | `wc -c < file` | `File.GetSize(path) → long` |
| TTY 检测 | `[[ -t 1 ]]` / `[[ -t 2 ]]` | `Console.IsTerminal()` / `ConsoleError.IsTerminal()` |
| 当前目录 | `pwd` / `$PWD` | `Environment.GetCurrentDirectory()` |
| cd | `cd path` | `Environment.SetCurrentDirectory(path)` |

注：流式 stdout（Phase 0c）已被 `Stdio.Inherit()` 覆盖——子进程直接写到父
tty。新的 LineReader API 留到真有脚本需要按行处理 child stdout 时再加。

## 设计决策

| Decision | 选项 | 决定 | 理由 |
|----------|------|------|------|
| 1. MakeExecutable 实现 | Unix only / cross-platform | Unix 实质 + Windows no-op | Win 没有 chmod；NTFS ACL 不在 v0 scope |
| 2. Link 行为 | hard link / fail on cross-device | hard link，跨设备时 OS 错误透传 | 同 `ln` 行为 |
| 3. SymLink 行为 | 相对路径解析 / 当字符串 | dst 当字符串（OS 决定） | 同 `ln -s` |
| 4. GetSize 返回类型 | int / long | long | 文件 > 2 GB 时 int 不够 |
| 5. IsTerminal 检测 | atty / 自检 stdin fd | 自检 stdin/stdout/stderr fd via libc | Rust `IsTerminal` trait (std 1.70+) 也可 |
| 6. IsTerminal 范围 | Console only / Console + ConsoleError 都加 | 都加 | 颜色判断常分别检查 stdout/stderr 重定向状态 |
| 7. GetCurrentDirectory 失败 | 抛 / 返空 | 透传 OS 错误 | 同 BCL 模式 |
| 8. SetCurrentDirectory 失败 | 抛 / 静默 | 透传（路径不存在 / 无权限）| fail-fast |
| 9. 已有 Link 在 file vs 新增 hard/sym 区分 | `File.Link` + `File.SymLink` 分两个 / 用 `bool symbolic` 参数 | 分两个方法 | 同 BCL `File.CreateSymbolicLink` 与 `File.CreateHardLink` 分立；语义不同清晰 |

## 阶段 1: Rust runtime impl

- [x] 1.1 NEW `src/runtime/src/corelib/fs.rs::builtin_file_make_executable`
- [x] 1.2 NEW `builtin_file_link`（hard link via std::fs::hard_link）
- [x] 1.3 NEW `builtin_file_symlink`（symlink via std::os::unix::fs::symlink — Windows 留 follow-up）
- [x] 1.4 NEW `builtin_file_get_size`
- [x] 1.5 NEW `builtin_console_is_terminal`（stdout）
- [x] 1.6 NEW `builtin_console_error_is_terminal`（stderr）
- [x] 1.7 NEW `builtin_env_get_cwd`
- [x] 1.8 NEW `builtin_env_set_cwd`
- [x] 1.9 MODIFY `corelib/mod.rs` — 注册 8 个 builtin

## 阶段 2: z42 stdlib wrapper

- [x] 2.1 MODIFY `z42.io/src/File.z42` — 4 个新 extern + doc
- [x] 2.2 MODIFY `z42.io/src/Console.z42` — `Console.IsTerminal` + `ConsoleError.IsTerminal`
- [x] 2.3 MODIFY `z42.io/src/Environment.z42` — `GetCurrentDirectory` + `SetCurrentDirectory`

## 阶段 3: 测试

- [x] 3.1 NEW `tests/file_chmod_link_size.z42` — MakeExecutable / Link / SymLink / GetSize
- [x] 3.2 NEW `tests/console_tty.z42` — IsTerminal smoke（不验证 true/false，运行不抛即可）
- [x] 3.3 NEW `tests/env_cwd.z42` — GetCurrentDirectory / SetCurrentDirectory round-trip

## 阶段 4: GREEN + 归档

- [x] 4.1 `cargo build --manifest-path src/runtime/Cargo.toml --release` 通过
- [x] 4.2 `./scripts/build-stdlib.sh release` 全绿
- [x] 4.3 `./scripts/test-stdlib.sh z42.io` 全绿
- [x] 4.4 `./scripts/test-stdlib.sh` 整体不回归
- [x] 4.5 mv → `docs/spec/archive/2026-05-16-extend-z42-io-script-helpers/`
- [x] 4.6 commit + push

## 实施期发现

1. **编译器 bug: `Console.X()` 0-arg 调用 crash on EmitConcat**。`FunctionEmitterCalls.cs:57`
   有 Console-specific 路径："如果 ReceiverClass == 'Console' 且 argRegs.Count != 1，把
   args 折叠成 concat"。Console.WriteLine 多参版本需要 (e.g. `WriteLine(a, b, c)` → concat），
   但本意是 `>1` 不是 `!= 1` — 0 参的 IsTerminal() 走进 EmitConcat([]) 然后 `regs[0]` 越界。
   Fix: 条件改为 `argRegs.Count > 1`。1288 compiler tests 全绿，行为不回归。
2. macOS `/tmp` 是 `/private/tmp` 的符号链接，`GetCurrentDirectory()` 返回 resolved path
   `/private/...`。`env_cwd` 测试改为只比较 `Path.GetFileName` 而非完整路径。
