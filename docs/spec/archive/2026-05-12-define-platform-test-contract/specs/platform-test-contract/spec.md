# Spec: 平台 facade 测试契约（platform-test-contract）

> 适用范围：iOS / Android / wasm facade。Desktop hello_c link-test 走 item #3 的独立 spec，不在本契约内。
>
> 本 spec 描述每个平台**必须**满足的最小测试 surface；具体实现由 `add-ios-tests` / `add-android-tests` / `add-wasm-tests` 三个下游 spec 落地。

## Conformance

下游平台 spec **必须**实现 R1–R7 全部 scenario。任何 scenario 跳过都必须在下游 spec 的 Out of Scope 段写明原因。

## Fixture 假设（所有 scenario 公用）

- **`hello.zbc`**：由 host `z42c` 把 `examples/hello.z42`（一行 `Console.WriteLine("hello, world")`）编出来的产物。每个平台 build.sh 在产 Xcframework / AAR / npm package 时把 `hello.zbc` 一起 ship 进 test bundle。
- **`garbage.zbc`**：随机字节数组（如 `vec![0xDE, 0xAD, 0xBE, 0xEF]`），不需要 compiler 参与。在 test 文件里 inline。
- **stdlib zpkg**：所有平台 build.sh 已经把 `z42.core.zpkg` / `z42.io.zpkg` 等 ship 进 bundle / asset / pkg 目录；resolver 默认配置能找到。
- **永远不在测试端调用 `z42c` / `dotnet`**：fixture 全部 host 端预编，测试只 load 二进制产物。原则见 [memory: project_mobile_no_compiler](../../../../../.claude/projects/-Users-d-s-qiu-Documents-codesigner-ui-z42/memory/project_mobile_no_compiler.md)。

## ADDED Requirements

### Requirement 1: Smoke — `hello, world` round-trip

平台 facade 必须能装载预编译 `.zbc`、解析 entry、invoke、并通过 stdout handler 收回正确输出。

#### Scenario: smoke
- **WHEN** test 用默认 resolver 构造 VM、装自定义 stdout sink、`loadZbc(hello.zbc)`、`resolveEntry("App.Main")`、`invoke(entry)`
- **THEN** sink 收到字节序列等价于 UTF-8 编码的 `"hello, world\n"`，且 `invoke` 不抛异常

### Requirement 2: Error — bad zbc

错误 zbc 必须映射到平台对应的 `badZbc` 异常（status = 10），消息可读。

#### Scenario: load_garbage_returns_bad_zbc
- **WHEN** test 调 `loadZbc(garbageBytes)`，其中 `garbageBytes` 是任意非 zbc 字节
- **THEN** 抛出平台异常（iOS `Z42VMError.badZbc`、Android `Z42VMException(status=10)`、wasm `Z42VMError` with status 10），且 `last_error_message` 包含人类可读的解释

### Requirement 3: Error — entry not found

`resolveEntry` 命中不存在的 FQN 时必须抛 `entryNotFound`（status = 20）。

#### Scenario: resolve_unknown_fqn_returns_entry_not_found
- **WHEN** module 已成功 load，但 `resolveEntry("App.Ghost")` 这个 fqn 在 module 中不存在
- **THEN** 抛出 `entryNotFound`（status = 20），消息含 fqn 字符串

### Requirement 4: Error — arg mismatch

`invoke` 传错参数个数必须抛 `argMismatch`（status = 21）。

#### Scenario: invoke_wrong_arg_count_returns_arg_mismatch
- **WHEN** entry 期望 0 参数，test 传 1 个参数调 invoke
- **THEN** 抛出 `argMismatch`（status = 21）

### Requirement 5: Resolver — corelib reachable; unknown ns returns null

默认 resolver（iOS `BundleZpkgResolver`、Android `AssetZpkgResolver`、wasm fetch-from-pkg）必须能从平台标准存储找到 stdlib zpkg；同一个 resolver 被问到不存在的 namespace 时返回 null（C ABI 层）/ 让 invoke 抛 `entryNotFound`。

#### Scenario: default_resolver_finds_corelib
- **WHEN** test 跑 §1 smoke（隐含依赖 `z42.core.zpkg` 可解析）
- **THEN** smoke 通过 = resolver 正常工作

#### Scenario: resolver_miss_for_unknown_ns_surfaces_at_invoke
- **WHEN** test 装一个只认识 `Std.Phantom` 的 `MapZpkgResolver`，load 一份 zbc 依赖 `Std.IO`
- **THEN** `invoke` 抛 `vmException`（status = 30）或 `loadZbc` 抛 `badZbc`（取决于 zbc 是否预 link），消息含 `Std.IO` namespace

### Requirement 6: Lifecycle — init / shutdown 循环无 leak

VM 多次构造 + 释放后 native handle 必须干净；不允许出现 already-init（runtime 单实例语义）或 leak（每次释放后 process state 必须支持重新 init）。

#### Scenario: init_shutdown_repeatedly
- **WHEN** test 循环 3 次：构造 VM → smoke → 显式 close / `deinit` 触发的隐式 shutdown
- **THEN** 第 2、3 次构造与第 1 次行为完全一致，无异常

### Requirement 7: Stdout handler — multi-line preserves order

stdout handler 必须按 `Console.WriteLine` 的顺序逐行回调；每次回调收到完整一行的字节（含换行）。

#### Scenario: multiline_stdout_in_order
- **WHEN** test load `multi_line.zbc`（fixture 内含 `Console.WriteLine("a"); Console.WriteLine("b"); Console.WriteLine("c");`），装 sink 收集到 `[ByteArray]`，invoke
- **THEN** 收集到的字节数组按顺序拼接后等价于 `"a\nb\nc\n"`

## MODIFIED Requirements

无。所有 scenario 都是新增对平台 facade 的契约。

## Deferred / Future Work

### threading-tests: 后台线程 invoke + sink 线程亲和性

- **来源**：本 contract 草稿期 Open Question
- **触发原因**：v0.1 runtime / facade 是单实例 + 同步 invoke，没有正式的 threading 语义；UI 主线程 vs 后台 dispatch 现阶段由调用方自负
- **前置依赖**：runtime 引入 threading 模型（multi-VM / async invoke / per-thread context 其中之一）
- **触发条件**：threading 设计落地（roadmap 后续 phase）后，回到本 contract 补 R8 scenario
- **当前 workaround**：facade 文档建议调用方自己用 `DispatchQueue` / `Dispatchers.Default`；不在本 contract 范围

## 平台 → fixture 路径表

| 平台 | fixture 在哪 | 测试 harness |
|------|------------|------------|
| iOS | `Z42VM.xcframework/Resources/test-fixtures/{hello,multi_line}.zbc` | XCTest，`Bundle.module` 加载 |
| Android | `z42vm/src/androidTest/assets/test-fixtures/{hello,multi_line}.zbc` | JUnit (Instrumented)，`InstrumentationRegistry.getInstrumentation().context.assets` |
| wasm | `tests/fixtures/{hello,multi_line}.zbc` + 静态服 | playwright headless Chrome，通过 fetch 加载 |

下游 spec 实现时必须遵循这张表，避免 fixture 散落。
