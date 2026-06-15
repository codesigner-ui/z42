# Proposal: desktop 作为第 4 个平台后端（统一测试管线）

## Why

`add-platform-test-pipeline` 把 wasm/iOS/Android 统一到 `test platform <p>` 框架，唯独
**桌面(host)C-ABI 嵌入**没进来：facade 级 R1–R7（真实外部 C 消费者 load zbc + 跑 7 场景）
在桌面是缺口——只有游离的 `src/toolchain/host/examples/hello_c/build.sh`(陈旧死副本)做过
smoke，从不进 test gate；Rust 级 `host_tests.rs` 测内部函数，不测"链接 libz42.a 的外部 C
程序"这条路径。加 `DesktopBackend` 补缺 + 统一心智(`test platform desktop|wasm|ios|android`)。

User 2026-06-15 决策：desktop v1 直接全 R1–R7。

## What Changes

- 新增 `src/toolchain/host/platforms/desktop/`（与 wasm/ios/android 平级）+ `tests/r1_r7.c`：Tier-1 C ABI 的 R1–R7 harness（z42_host.h；7 场景 + 状态码断言；每场景打 `[Rn] PASS/FAIL` + exit code）
- 新增 `scripts/xtask_test_desktop.z42` `DesktopBackend : IPlatformBackend`：① `cargo rustc --crate-type=staticlib`→libz42.a；② 编 fixtures；③ `cc r1_r7.c`+链+跑+解析 stdout→junit
- 注册进框架（`_platformDispatch` + `_platformAllPlatforms`）+ CLI（`test platform desktop`）
- ~~退役 `src/toolchain/host/examples/`~~ → **取消**（删前发现它是 host/README 文档化的示例：hello_rust Tier-2 H2b + hello_c 参考，非纯死物）。DesktopBackend 新增 R1–R7 测试，不取代示例。examples/embedding vs host/examples 去重留专门 change。

## Scope（允许改动的文件）

| 文件 | 类型 | 说明 |
|------|------|------|
| `src/toolchain/host/platforms/desktop/tests/r1_r7.c` | NEW | C R1–R7 harness |
| `src/toolchain/host/platforms/desktop/README.md` | NEW | 目录职责 |
| `scripts/xtask_test_desktop.z42` | NEW | DesktopBackend |
| `scripts/xtask_test_platform.z42` | MODIFY | 注册 desktop 后端 |
| `scripts/xtask.z42.toml` | MODIFY | [sources] 加 xtask_test_desktop.z42 |
| `scripts/xtask_cli.z42` | MODIFY | `test platform` 加 desktop 叶子 |
| `docs/design/testing/cross-platform-testing.md` | MODIFY | desktop 后端 + R1–R7 实现原理 |
| `docs/spec/changes/ACTIVE.md` | MODIFY | toolchain 登记/释放 |

**只读引用**：`examples/embedding/hello_c/main.c`(C ABI 流参考)、`src/runtime/include/z42_host.h`、`platforms/wasm/tests/r1-r7.spec.ts`(场景对照)、`scripts/xtask_test_{platform,wasm}.z42`。

## Out of Scope
- 桌面 Tier-2 Rust(z42-host)的独立 R1–R7（已有 host_tests.rs 覆盖内部）
- CI test-desktop job（可随后加；本 change 先本地跑通——桌面工具链 cc/cargo/dotnet 本机即全）

## 并行
占 `toolchain`（含 src/toolchain/host/* + scripts/）。与 redirect-golden / z42c 等并行，User 多次授权；主动新文件 + 删独立死副本，不碰他们的文件。
