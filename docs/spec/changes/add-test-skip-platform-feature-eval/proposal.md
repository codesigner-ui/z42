# Proposal: `[Skip(platform:)] / [Skip(feature:)]` runtime evaluation

## Why

R1.C 在 2026-04-29 让 compiler 把 `[Skip(platform: "ios", feature: "jit", reason: "...")]` 三段字段都 emit 到 TIDX section
（`SkipPlatformStrIdx` / `SkipFeatureStrIdx` / `SkipReasonStrIdx`），
runner 也读取并把三者拼成 reason 字符串显示。**但 runner 从未真正实现条件语义**
（`src/toolchain/test-runner/src/runner.rs:30-32` 和 `exec.rs:58-61`：只要 `TestFlags::SKIPPED` 置位 + reason
非空，无论平台是否匹配一律跳过）。结果：

- 用户写 `[Skip(platform: "ios", reason: "iOS-only WebGL bug")]` 期望 "iOS 上跳过，其他平台正常跑"，
  实际 **所有平台都跳过** —— 该用例永远不会被覆盖
- 用户写 `[Skip(feature: "multithreading")]` 期望 "无 multithreading 时跳过"，
  实际行为同上 —— 在 multithreading 可用的 host 上也跳过

`docs/design/testing/testing.md` 和 `TestEntry.cs` 的 docstring 早已许诺"runner skips this
test **only when** running on the named platform" — 这是文档与代码失同步的现存 bug，
不是新特性。本 spec 把许诺兑现。

## What Changes

1. **新模块 `src/toolchain/test-runner/src/skip_eval.rs`** —
   纯函数 `decide_skip(test, env) -> Option<String>` 集中条件计算
2. **`DiscoveredTest` 新增独立字段** `skip_platform` / `skip_feature`
   （取代当前唯一的 `skip_reason` composite 字符串），把"条件"和"用户写的人话理由"分开
3. **`runner.rs` + `exec.rs` 跳过分支** 改为调 `decide_skip`，按规则求值
4. **平台值来源**：runner 启动时读 `std::env::consts::OS`（与 `Std.Platform.OS()` 同源），
   CLI `--platform <NAME>` 和 env `Z42_TEST_PLATFORM` 可覆盖（用于跨平台测试 runner 自身）
5. **Feature 注册表**（runtime 编译期静态）：v1 minimal — `jit` / `interp` / `multithreading`
   / `filesystem`；未知 feature 名按 "unavailable" 处理（默认 deny，保守语义）
6. **Skip reason 显示** 包含触发条件 + 用户 reason：
   `"skipped on ios: iOS-only WebGL bug"` 或 `"skipped (feature 'jit' unavailable)"`
7. **examples/test_demo.z42 注释更新**：当前的 3 个 [Skip] demo 注释（"always skipped 因为
   runner 不查 platform"）改写为真实条件描述
8. **文档双轨**：用法 + 设计思路同步落地（见 Scope 中 docs 行）

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/toolchain/test-runner/src/skip_eval.rs` | NEW | `SkipEnv { current_platform, available_features }`, `decide_skip(test, env) -> Option<String>` |
| `src/toolchain/test-runner/src/skip_eval_tests.rs` | NEW | 单元测试矩阵（platform-match / platform-mismatch / feature-available / feature-unavailable / compound OR / unconditional-skip / no-skip-flag） |
| `src/toolchain/test-runner/src/discover.rs` | MODIFY | `DiscoveredTest` 拆 `skip_reason` 为 `skip_reason` (人话) + `skip_platform: Option<String>` + `skip_feature: Option<String>`；删除 `format_skip_reason` (替换为 skip_eval 内部 formatter) |
| `src/toolchain/test-runner/src/runner.rs` | MODIFY | 替换 L30-32 unconditional skip 为 `if let Some(reason) = skip_eval::decide_skip(test, env) { return Outcome::Skipped { reason }; }` |
| `src/toolchain/test-runner/src/exec.rs` | MODIFY | 同上 L58-61；env 通过参数传入 |
| `src/toolchain/test-runner/src/parallel.rs` | MODIFY | 把 env 透传给 worker pool |
| `src/toolchain/test-runner/src/main.rs` | MODIFY | (a) 新增 CLI `--platform <NAME>` + 读 `Z42_TEST_PLATFORM` env (b) `DiscoveredTestOwned` 同步加字段 (c) 启动时构建 `SkipEnv` 并传给 `runner::run_one` / `exec::run_one` (d) 调用 `skip_eval::decide_skip` |
| `src/toolchain/test-runner/src/bootstrap.rs` | MODIFY (read-only check) | 如果有任何 skip 逻辑残留，迁移到 skip_eval |
| `src/runtime/src/metadata/test_index.rs` | 只读引用 | 字段已在 R1.C 落地；不修改 |
| `src/compiler/z42.IR/TestEntry.cs` | MODIFY | 仅更新 `SkipPlatformStrIdx` / `SkipFeatureStrIdx` 字段的 XML doc，删除"runner 当前不做条件评估"的过时注释（如有） |
| `examples/test_demo.z42` | MODIFY | 3 个 [Skip] 示例的内联注释从"always skipped" 改写为真实条件描述；不改 attribute 本身 |
| `src/libraries/z42.test/tests/skip_platform_demo.z42` | NEW | in-tree e2e demo：至少 1 个 platform-match + 1 个 platform-mismatch + 1 个 feature-gated case，验证 runner 路径实际生效 |
| `docs/design/testing/testing.md` | MODIFY | (用法) 新增 §"Conditional skip semantics"：[Skip(platform)] / [Skip(feature)] 语义表 + 平台/特性枚举 + CLI override 用法；(设计思路) §"Skip evaluation design rationale"：为什么 OR 语义 / 为什么 deny-by-default unknown feature / 为什么 platform 来自 Rust consts 而非 z42 bootstrap |
| `src/libraries/z42.test/README.md` | MODIFY | 能力表"Attribute 注解"行从"`[Skip(reason:)]`"扩展为"`[Skip(reason:, platform?:, feature?:)] — 平台/特性条件已生效`" |

**只读引用：**

- `src/runtime/src/corelib/platform.rs:15-40` — 复用平台枚举集 (`"linux"|"macos"|"windows"|"android"|"ios"|"wasm"|"freebsd"`)
- `docs/design/testing/testing.md:69-75 format_skip_reason` 历史段（参考现有 reason 拼接习惯）
- `src/toolchain/test-runner/src/result.rs` `Outcome::Skipped { reason }`（字段不变，仅 reason 内容语义升级）

## Out of Scope

- **动态扩展 feature 注册表**（让用户在 `.z42proj` 或 CLI 里声明额外 features）— v2 考虑；
  本 spec 用编译期 const set
- **平台层级匹配**（如 `platform: "unix"` 自动覆盖 linux+macos+freebsd）—
  v2；v1 只做精确字符串相等
- **arch 维度**（`[Skip(arch: "wasm32")]`）— 没有现存 IR 字段；本 spec 不引入
- **Setup/Teardown 也按 skip 条件跳过** — 当前 Setup/Teardown 不带 attribute，
  无需特殊处理
- **TIDX format 变更** — 字段已在 R1.C 落地；不改 zbc/zpkg version
- **`[SkipIf(...)]` 反义 attribute 形式** — 当前 `[Skip(...)]` 已经表达 "条件成立则跳过"，
  反义只是糖；v2 再议

## Open Questions

- [x] **已裁决（基于现状）**：platform 字符串精确相等匹配，case-sensitive，
      值取自 `std::env::consts::OS` 已枚举的 7 个值 + `unknown` fallback
- [x] **已裁决（基于现状）**：feature 注册表 deny-by-default — 未知 feature 名 →
      认为 unavailable → 触发跳过。Why: 保守路径，避免"打错字 → 测试本该跳但实际跑挂"
- [x] **已裁决（基于现状）**：compound (`[Skip(platform: X, feature: Y)]`)
      用 OR 语义 — 任一条件成立就跳过；matches "test inapplicable in either
      condition" 直觉
- [x] **已裁决（基于现状）**：`[Skip]` 完全无 named-arg 或仅有 `reason:` →
      无条件跳过（preserve 现行 R1.A 语义；纯粹"挂起这个 case"用法）
- [x] **已裁决（基于现状）**：CLI override = `--platform <NAME>`；env override =
      `Z42_TEST_PLATFORM`；两者并存时 CLI 优先；都缺省则 `std::env::consts::OS`
