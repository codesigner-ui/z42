# Tasks: `[Skip(platform/feature)]` runtime evaluation

> 状态：🟢 已完成 | 完成：2026-05-30 | 创建：2026-05-30 | 类型：vm (test runner behavior)

## 进度概览

- [x] 阶段 1: 新模块 `skip_eval.rs` + `SkipEnv` + `decide_skip` 纯函数
- [x] 阶段 2: `discover.rs` 字段拆分 (skip_reason / skip_platform / skip_feature)
- [x] 阶段 3: `runner.rs` + `exec.rs` + `parallel.rs` 调用 decide_skip
- [x] 阶段 4: `main.rs` CLI `--platform` + env `Z42_TEST_PLATFORM` + SkipEnv 构造
- [x] 阶段 5: 单元测试 (`skip_eval_tests.rs` 14-case 矩阵)
- [x] 阶段 6: E2E 演示 (`src/libraries/z42.test/tests/skip_platform_demo.z42`)
- [x] 阶段 7: 文档（用法 + 设计思路双轨）
- [x] 阶段 8: GREEN + commit + archive

## 阶段 1: `skip_eval.rs`

- [x] 1.1 NEW `src/toolchain/test-runner/src/skip_eval.rs`
  - `pub struct SkipEnv { pub current_platform: String, pub available_features: HashSet<String> }`
  - `impl SkipEnv { pub fn detect() -> Self; pub fn with_platform(self, p: String) -> Self }`
    - `detect`：`std::env::consts::OS.to_string()` + cfg-driven features
      - `interp`, `jit` 始终插入
      - `multithreading`, `filesystem` 在 `cfg!(not(target_arch = "wasm32"))` 时插入
  - `pub fn decide_skip(test: &DiscoveredTest, env: &SkipEnv) -> Option<String>`
    - 按 design Decision 1-5 实现；unknown feature 走 deny + stderr `note:`（用
      内部 `OnceLock<Mutex<HashSet>>` dedupe 同名警告，避免一次跑数千 test 重复打印）
  - `pub(crate) fn format_reason(...)` 内部 helper
- [x] 1.2 `src/toolchain/test-runner/src/main.rs` 加 `mod skip_eval;` 注册

## 阶段 2: `discover.rs` 字段拆分

- [x] 2.1 `DiscoveredTest` 三段字段独立
  - `pub skip_reason: Option<String>` — 用户写的 reason: 字面值
  - `pub skip_platform: Option<String>` — 用户写的 platform: 字面值
  - `pub skip_feature: Option<String>` — 用户写的 feature: 字面值
- [x] 2.2 `TestReport::from_artifact` 直接拷贝三个 entry 字段，不再拼接
- [x] 2.3 删除 `pub fn format_skip_reason(entry: &TestEntry) -> String`
- [x] 2.4 grep 确认无其它调用方（`format_skip_reason` 仅在 discover.rs 内部使用）

## 阶段 3: `runner.rs` / `exec.rs` / `parallel.rs` 调用切换

- [x] 3.1 `runner.rs:30-32` 删 `if let Some(reason) = &test.skip_reason { return Outcome::Skipped }`
  替换为 `if let Some(reason) = skip_eval::decide_skip(test, env) { return Outcome::Skipped { reason }; }`
- [x] 3.2 `runner::run_one` 签名加 `env: &SkipEnv` 参数
- [x] 3.3 `exec.rs:38-40` 同 3.1 (subprocess path)
- [x] 3.4 `exec::run_one` 签名加 `env: &SkipEnv` 参数
- [x] 3.5 `parallel.rs::run_tests` 签名加 `env: &SkipEnv`，透传到 worker 闭包
  （worker 调 `exec::run_one(z42vm, zbc, &test, env)`）
- [x] 3.6 调用点全部更新：main.rs 三个分支（jobs>1 / serial subprocess / in-process）

## 阶段 4: `main.rs` CLI / env override

- [x] 4.1 `Cli` struct 加：
  ```
  /// Override host platform detection (default: std::env::consts::OS). Useful
  /// for verifying [Skip(platform:)] gating across hosts on a single machine.
  /// Z42_TEST_PLATFORM env var is read when --platform is absent.
  #[arg(long, value_name = "NAME")]
  platform: Option<String>,
  ```
- [x] 4.2 `run(cli)` 顶部构造：
  ```
  let mut env = skip_eval::SkipEnv::detect();
  let override_plat = cli.platform.clone()
      .or_else(|| std::env::var("Z42_TEST_PLATFORM").ok());
  if let Some(p) = override_plat { env = env.with_platform(p); }
  ```
- [x] 4.3 `DiscoveredTestOwned` 字段加 `skip_platform: Option<String>` /
  `skip_feature: Option<String>`；构造时从 `entry.skip_platform.clone()` 直接拷
- [x] 4.4 in-process 循环：构造 `DiscoveredTest` 时把三个 skip-字段都传齐；run_one 调用加 env
- [x] 4.5 subprocess paths：同上 + parallel 路径加 env 透传

## 阶段 5: 单元测试

- [x] 5.1 NEW `src/toolchain/test-runner/src/skip_eval_tests.rs`
  - 测试矩阵 case 1-14（design.md Testing Strategy 表）
  - case 11-14 reason 字符串断言用 `.contains(...)` 不做完全相等以容忍未来微调
- [x] 5.2 `skip_eval.rs` 末尾 `#[cfg(test)] mod skip_eval_tests;`
  （遵循 runtime-rust.md "测试拆分" 规则 — 独立 .rs 文件，不内联在实现末尾）
- [x] 5.3 `cargo test --manifest-path src/toolchain/test-runner/Cargo.toml`
  局部 GREEN 通过

## 阶段 6: E2E 演示

- [x] 6.1 NEW `src/libraries/z42.test/tests/skip_platform_demo.z42` — 三个 case：
  - `test_skip_on_macos` — `[Skip(platform: "macos", reason: "macOS-only quirk")]`
    + `void test_skip_on_macos() { Assert.Fail("should not run on linux"); }`
    （linux CI 必须跑，macos local 必须跳）
  - `test_skip_on_atari` — `[Skip(platform: "atari", reason: "retro")]` + 实际跑
    通的 trivial body（任何 host 都不匹配 → 跑通）
  - `test_skip_unknown_feature` — `[Skip(feature: "quantum_entanglement")]` + body
    `Assert.Fail("unknown feature should deny")`（deny-by-default → 跳 → 不会触发 Fail）
- [x] 6.2 `./scripts/test-stdlib.sh` GREEN（验证 e2e wiring）

## 阶段 7: 文档

### 7.A 用法（user-facing）

- [x] 7.A.1 `docs/design/testing/testing.md` 新增 § "Conditional skip semantics"：
  - [Skip(platform:)] 写法 + 示例 + 表格枚举支持的 platform 值（`"linux" |
    "macos" | "windows" | "android" | "ios" | "wasm" | "freebsd"`）
  - [Skip(feature:)] 写法 + 当前注册的 4 个 feature 名 + 未知 feature 的
    deny-by-default 行为说明
  - compound `[Skip(platform:, feature:)]` 的 OR 语义
  - CLI override `--platform <NAME>` + env `Z42_TEST_PLATFORM`
- [x] 7.A.2 `src/libraries/z42.test/README.md` 能力表 Attribute 行扩展为
  `[Skip(reason:, platform?:, feature?:)] — 平台/特性条件实际生效（add-test-skip-platform-feature-eval）`
- [x] 7.A.3 `examples/test_demo.z42` 把 3 个 [Skip] 示例的内联注释改写为
  "在 <plat> 上才跳" / "无 <feature> 时跳"，去除"always skipped"过时表述
- [x] 7.A.4 `src/compiler/z42.IR/TestEntry.cs` XML doc：
  - `SkipPlatformStrIdx` 删除"runner skips this test **only when** running on
    the named platform" 中的 "(currently runner unconditionally skips)" 字样
    （如果之前 R1.C 加过这种 TODO 注释）
  - 同上 SkipFeatureStrIdx

### 7.B 设计思路（design rationale, internal-facing）

- [x] 7.B.1 `docs/design/testing/testing.md` 同节后续段 § "Skip evaluation
  design rationale"：
  - 解释 Decision 1（platform 来源选 B）、Decision 2 (OR)、Decision 3
    (deny-by-default unknown feature)、Decision 4 (4-feature 初始集，未来扩
    展原则)、Decision 5 (reason 拼接格式)
  - 引用 `docs/spec/archive/2026-05-30-add-test-skip-platform-feature-eval/design.md`
    指向完整决策记录
- [x] 7.B.2 `docs/design/testing/testing.md` 顶部 R 系列实施进度表加一行：
  `| Skip eval | add-test-skip-platform-feature-eval | ✅ 完成 (2026-05-30) | <commit hash> |`

## 阶段 8: GREEN + commit + archive

- [x] 8.1 `cargo build --manifest-path src/runtime/Cargo.toml --release` 通过
- [x] 8.2 `cargo test --manifest-path src/toolchain/test-runner/Cargo.toml`
  GREEN（包含新 14-case 矩阵）
- [x] 8.3 `./scripts/test-all.sh --parallel --jobs=4` 全绿（含 e2e demo + 原 stdlib）
- [x] 8.4 commit + push（spec + 实现 + 测试 + 文档 + demo）
- [x] 8.5 归档 `docs/spec/changes/add-test-skip-platform-feature-eval/` →
  `docs/spec/archive/2026-05-30-add-test-skip-platform-feature-eval/`
- [x] 8.6 push 归档 commit

## 备注

- C# 编译器侧 0 改动 — TIDX 字段已在 R1.C 落地，本 spec 纯 Rust 端 + 文档
- 同时存在 in-process (runner.rs) + subprocess (exec.rs) + parallel-subprocess
  (parallel.rs) 三条执行路径，签名变更需三处同步；漏一处 main.rs 会编译失败 = 自动 catch
- `OnceLock<Mutex<HashSet>>` 用于 unknown-feature warn dedupe；如果实施时发现
  parking_lot 或 std::sync::Mutex 在 worker pool 下竞争明显，停下来报告（罕见，
  实测可忽略）
