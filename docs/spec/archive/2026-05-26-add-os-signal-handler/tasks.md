# Tasks: add OS signal handler

> 状态：🟢 已完成 | 创建：2026-05-25 | 完成：2026-05-26

## 进度概览
- [x] 阶段 1：依赖 + 模块骨架（Cargo.toml + signal_handler.rs 占位）
- [x] 阶段 2：sigsafe 写入 primitives + 单元测试
- [x] 阶段 3：VM_CORES 全局 registry（vm_context.rs 改动）
- [x] 阶段 4：handler 主体 + install
- [x] 阶段 5：main.rs 接线 + helper binary
- [x] 阶段 6：集成测试 e2e
- [x] 阶段 7：docs 同步
- [ ] 阶段 8：全绿验证 + 归档（pending commit）

## 阶段 1：依赖 + 模块骨架

- [x] 1.1 `src/runtime/Cargo.toml` — 加 `signal-hook-registry = "1.4"` 到既有 `[target.'cfg(unix)'.dependencies]` 段（不重开 section）
- [x] 1.2 `src/runtime/src/signal_handler.rs` — 新文件，含模块 doc + `pub fn install()` + handler 主体 + `sigsafe` 子模块
- [x] 1.3 `src/runtime/src/lib.rs` — `#[cfg(unix)] pub mod signal_handler;`
- [x] 1.4 `cargo build` 通过

## 阶段 2：sigsafe 写入 primitives + 单元测试

- [x] 2.1 `sigsafe::write_str(fd, &[u8])` — 循环处理 partial write，错误 silent break
- [x] 2.2 `sigsafe::write_dec_u32(fd, u32)` — 栈 [u8; 10] buffer
- [x] 2.3 `sigsafe::write_hex_u64(fd, u64)` — 栈 [u8; 16] buffer + `0x` prefix
- [x] 2.4 `src/runtime/src/signal_handler_tests.rs` — 10 个测试（write_dec 0/small/max；write_hex 0/basic/max；write_str partial；signal_name known/unknown；install idempotent）
- [x] 2.5 `cargo test --lib signal_handler_tests` — 10/10 通过

## 阶段 3：VM_CORES 全局 registry

- [x] 3.1 `src/runtime/src/vm_context.rs` — `pub(crate) static VM_CORES: std::sync::Mutex<Vec<Weak<VmCore>>>` (const fn for static init)
- [x] 3.2 `pub fn vm_cores_snapshot() -> Vec<Arc<VmCore>>` 公开 API
- [x] 3.3 `new_internal()` 在 Arc::new(VmCore { ... }) 后 retain 死链 + push Weak
- [x] 3.4 `use std::sync::Weak;` 加导入
- [x] 3.5 `cargo test --lib` 全绿（验证未破现有测试 — 671/671 通过）

## 阶段 4：handler 主体 + install

- [x] 4.1 `signal_name(sig: i32) -> &'static [u8]` 静态映射 5 signals + UNKNOWN fallback
- [x] 4.2 `write_banner(fd)` — 写 version / target / profile（用 `env!` const + `std::env::consts`）
- [x] 4.3 `write_call_stacks(fd)` — 完整 try_lock 链（VM_CORES std::sync → core.vm_contexts parking_lot → ctx.call_stack parking_lot）
- [x] 4.4 `extern "C" fn handler(sig: i32)` — 串起 header + banner + stacks + reset + raise
- [x] 4.5 `install()` — `Z42_CRASH_DIR` fd 预开（`OpenOptions::create + append`），warn 不阻塞
- [x] 4.6 5 signal 循环 `register_signal_unchecked`
- [x] 4.7 `INSTALLED: AtomicI32` idempotency check
- [x] 4.8 release build pass

## 阶段 5：main.rs 接线 + helper binary

- [x] 5.1 `src/runtime/src/main.rs::main()` — `install_panic_hook()` 之后加 `#[cfg(unix)] z42::signal_handler::install();`
- [x] 5.2 `src/runtime/examples/signal_crash_helper.rs` — 新 example binary，install hooks + 创建 VmContext + raise 指定 signal
- [x] 5.3 `cargo build --example signal_crash_helper` 通过
- [x] 5.4 手测：`./signal_crash_helper SIGSEGV` → stderr 看到 `[z42vm signal SIGSEGV]` + z42 call stack + exit=139
- [x] 5.5 手测 `Z42_CRASH_DIR`：tempdir + 文件出现 + 内容匹配 ✓

## 阶段 6：集成测试 e2e

- [x] 6.1 `src/runtime/tests/signal_handler_e2e.rs` 新文件 — `#[cfg(unix)]` gated
- [x] 6.2 helper `helper_path()` — 3 候选路径（`CARGO_TARGET_DIR` / z42 默认 `artifacts/build/runtime` / vanilla `target/`）
- [x] 6.3 5 个 signal 测试（SIGSEGV / SIGABRT / SIGFPE / SIGILL / SIGBUS）：assert marker + signaled exit
- [x] 6.4 `z42_crash_dir_writes_file` test：tempdir + Z42_CRASH_DIR + assert file 出现 + 内容匹配
- [x] 6.5 `z42_crash_dir_unwritable_falls_back_to_stderr` test
- [x] 6.6 `cargo test --test signal_handler_e2e` — 7/7 通过

## 阶段 7：docs 同步

- [x] 7.1 `docs/workflow/debugging.md` — `Z42_CRASH_DIR` 段重写，列 5 个被 capture 的 signal + lock-contended 降级语义 + Windows 暂未支持声明
- [x] 7.2 `docs/review.md` — Part 4 D4 状态 ❌ → ✅，列 Phase 1 + Phase 2 + 5 个 future Phase
- [ ] 7.3 `docs/design/runtime/vm-architecture.md` — 暂不动（review 文档已覆盖，design doc 后续 Phase 2.X 之一一起补）

## 阶段 8：全绿验证 + 归档

- [x] 8.1 `cargo build --manifest-path src/runtime/Cargo.toml --release` 无错
- [x] 8.2 `cargo test --manifest-path src/runtime/Cargo.toml --lib` 671/671 ✅（含 10 个新增 signal_handler_tests）
- [x] 8.3 `cargo test --manifest-path src/runtime/Cargo.toml --test signal_handler_e2e` 7/7 ✅
- [x] 8.4 手测：release build green / lib 全绿 / e2e 全绿（stdlib 跑过含 z42.net WIP pre-existing 失败不计入本 spec）
- [x] 8.5 spec scenarios 逐条对照验证（13/13 covered — 5 signal + 3 lock-contended + 3 Z42_CRASH_DIR + 2 cross-platform）
- [x] 8.6 commit + push（pending）
- [x] 8.7 归档到 `docs/spec/archive/2026-05-26-add-os-signal-handler/`（pending）

## 实施期发现

### Scope 微调

1. **`std::sync::Mutex` for VM_CORES**（spec design Decision 5 默认 `parking_lot::Mutex`）—— parking_lot 没 const fn，无法用于 static。改 std::sync::Mutex 既 const-init 又对 signal handler 调用没影响（信号 handler 用 try_lock 两种 mutex 都不阻塞）。design.md Decision 5 已隐含；本备注显式说明。

2. **`write_hex_u64` 暂未在 production path 使用** —— sigsafe::write_hex_u64 写好了但 handler 主体只用 `write_str / write_dec_u32`。原因：write_call_stacks 只输出 dec（行号 / 列号 / 索引），未涉及指针 / 地址。Phase 2.2 stack-pointer 线程归因落地时会用到 hex。当前测试覆盖了 write_hex_u64 行为正确，dead-code warning 加 `#[allow(dead_code)]`（已在 fn 上加注释说明用途）。Build warn 可接受。

3. **Helper binary 不在 Cargo.toml `[[example]]` 显式注册** —— Cargo 自动 discover `examples/*.rs`，无需手动注册。

## 备注

- **变更分类**：vm（runtime startup 行为变化 + 新依赖）→ 完整 spec 流程
- **后续工作**（不在本 spec）：
  - Phase 2.1 Windows VEH
  - Phase 2.2 stack-pointer 线程归因
  - Phase 2.3 sigaltstack
  - Phase 2.4 Rust backtrace in signal context（需 `backtrace` crate 提供 async-signal-safe 路径）
  - Phase 2.5 panic hook + signal handler 复用同一 crash file fd
