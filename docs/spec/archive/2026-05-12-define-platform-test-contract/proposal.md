# Proposal: 定义平台 facade 测试契约（define-platform-test-contract）

## Why

z42 当前有 4 个嵌入式 facade（iOS / Android / wasm / Tier 2 desktop Rust + C），每个都是 Tier 3 薄胶水，把 platform-native 语言（Swift / Kotlin / JS / C）翻成 Tier 1 C ABI。底层（VM 语义、C ABI、Tier 2）已经覆盖透了，但平台 facade 本身只有 hello-world demo 跑通，**没有任何自动化测试**。

在动手为每个平台单独搭 test 框架（XCTest / JUnit / playwright / C harness）前，先定义一份共享的"facade test contract"，列清楚每个平台要测的 7–8 个 scenario、各自 verify 什么 facade-side bug，以及 fixture 的产出与分发规则。这样：

- iOS / Android / wasm 三个 spec 写实现时直接照本宣科，scenario 标号一致
- review 时只需看一遍 contract，三平台行为对齐有据
- 不会在 mobile 测试里偷渡 compiler / 模块解析等 host-only 行为

## What Changes

只产规范文档（design + specs），**不产任何代码**。下游三个 spec（`add-ios-tests` / `add-android-tests` / `add-wasm-tests`）实现 contract 时是各自独立的变更，本 spec 不预先实施。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `docs/spec/changes/define-platform-test-contract/proposal.md`                   | NEW | 本文件 |
| `docs/spec/changes/define-platform-test-contract/design.md`                     | NEW | fixture 布局 / 共享 harness 假设 / 决策记录 |
| `docs/spec/changes/define-platform-test-contract/specs/platform-test-contract/spec.md` | NEW | 7 个 scenario 的 ADDED requirements，平台 agnostic |
| `docs/spec/changes/define-platform-test-contract/tasks.md`                      | NEW | 实施清单（仅 4 个 spec 文档 + 1 处 design doc 落地） |
| `docs/design/runtime/embedding.md`                                              | MODIFY | 顶部补一段 "host vs mobile 编译边界"，含 "移动端不带 compiler" 原则 |
| `src/toolchain/host/platforms/README.md`                                        | MODIFY | 顶部加一段 "fixture 在 host 端预编译"，与上一条互链 |

**只读引用：**

- `src/toolchain/host/embed/src/lib.rs` — Tier 2 API 形态，看错误类型
- `src/runtime/include/z42_host.h` — Tier 1 status 码定义
- `src/toolchain/host/platforms/{ios,android,wasm}/Sources|src/...` — 现有 facade API 形态（每个平台不一样，contract 需 platform-agnostic）
- `examples/hello.z42` — smoke fixture 的源头
- `src/runtime/src/host/host_tests.rs` — Tier 1 已覆盖的 22 个测试（避免重复）

## Out of Scope

- 任何具体测试代码（XCTest / JUnit / playwright）
- 任何 build.sh 改动（fixture 编译步骤 / CI 接入留给下游 spec）
- desktop hello_c link-test —— 那是 item #3 的事，与 mobile facade 测试目标不同（host 上有 compiler，可以 round-trip 测编译 + 链接 + 执行；本 contract 只管 mobile/wasm 这种"只跑 VM"的场景）
- JIT 模式测试（iOS / Android 不允许；wasm interp-only）
- 并发 / hot-reload / multi-VM 测试（v0.1 单实例约束）
- string / object / array marshal 测试（v0.1 marshal 限 null/i64/f64/bool）
- 性能 / benchmark（本 contract 是正确性 contract，非性能 contract）

## Open Questions（已裁决）

- [x] **threading scenario 是否纳入**：**不纳入**。v0.1 runtime / facade 是单实例 + 同步 invoke，没有正式 threading 语义；后台线程 invoke + sink dispatch 是调用方责任。该 scenario 移入 spec.md **Deferred / Future Work** 段，等 runtime threading 模型落地后回来补。
- [x] **resolver scenario 怎么测"真的找到 corelib"**：smoke 充当正路，另加 1 个负路（`MapResolver` 不含 `Std.IO` → `vmException` / `badZbc`）。
- [x] **mobile 端要不要测 hot-restart**：纳入 lifecycle scenario（R6），循环 init/shutdown 3 轮验证 deinit 触发 shutdown 干净。
