# Tasks: 退役 z42-test-runner

> 状态：🟢 主体完成（test/bench host 已替 + Rust runner 已删）| 创建：2026-06-19 | 完成：2026-06-30
> **前置**：boxing（0.3.11 ✅）+ 非泛型 Method.Invoke（0.3.12 ✅）
> **子系统**：stdlib（z42.test）+ toolchain（builder）+ runtime（__invoke_static + __load_module 依赖闭包/静态初始化）
> **修订（2026-06-25）**：命令宿主 z42c → **z42b（builder）**；z42c 退出 scope；bench 拉入。
> **实施记（2026-06-30）**：z42b 定为 **test/bench host only**——stdlib `z42.project` 与编译器
> `z42c.project` 共用 `namespace Z42.Project`，在共享 Z42_LIBS 下冲突会炸 z42c compile，故
> build/publish/export/new 编排（import Z42.Build/Z42.Project）留 PARKED，归 wire-z42b-host-build
> （须先解命名空间冲突）。z42.project/z42.build 暂不入 build。
> **运行器实现**：runner = `Std.Test.Runner.RunModule`（z42.test 库）；free-function 测试经新
> builtin `__invoke_static` 按 FQN 调用；`__load_module` 注册测试模块 import_namespaces 候选 +
> force-load 依赖闭包 + 重跑 init_static_fields（否则跨包 VCall / dep 静态字段 Null）。
> **验证**：test stdlib 272/272（22 lib，2615 PASS/0 FAIL/2 SKIP）+ test compiler 17/17 units +
> 7/7 自举不动点，均经 z42b。TAP/JUnit/JSON/--filter 富格式列 Deferred（z42b 只 pretty+退出码）。
> **结构化输出格式（Deferred）**：原 Rust runner 的 TAP/JUnit/JSON formatter + `--filter`/`--list`/
> `--platform` 未在 z42b 复刻；xtask 按退出码聚合，CI 不需富格式。需要时按独立变更补 z42 侧 formatter。

## 进度概览

- [ ] 阶段 1: z42.test — TestResult + TestDiscovery（无需 Method.Invoke，纯反射查询）
- [ ] 阶段 2: z42.test — TestRunner v2（需 Method.Invoke，等 0.3.12）
- [ ] 阶段 3: z42b（builder）— `test` verb（需 TestRunner v2，等阶段 2）
- [ ] 阶段 3b: z42.test — BenchRunner + z42b `bench` verb（需 Method.Invoke）
- [ ] 阶段 4: xtask 集成切换（test + bench）
- [ ] 阶段 5: test-runner 退役 + Cargo 清理（同时替掉 [Test]+[Benchmark] 才能删）
- [ ] 阶段 6: GREEN 验证

---

## 阶段 1: z42.test — TestResult + TestDiscovery
> **可立即开始**（只用现有反射，无需 Method.Invoke）

- [ ] 1.1 新建 `src/libraries/z42.test/src/TestResult.z42`
      — `TestResult` class（Name、FullName、Status: string、ErrorMsg、ErrorType）
      — `TestStatus` string 常量（Pass="pass"、Fail="fail"、Skip="skip"）
- [ ] 1.2 新建 `src/libraries/z42.test/src/TestDiscovery.z42`
      — `TestDiscovery.FindTests(Type t): MethodInfo[]`（`GetMethods()` + `GetAttribute(TestAttribute)` 过滤）
      — `TestDiscovery.FindSetups(Type t): MethodInfo[]`
      — `TestDiscovery.FindTeardowns(Type t): MethodInfo[]`
- [ ] 1.3 为 `[Test]`/`[Setup]`/`[Teardown]` 属性确认反射可查（GoldenTest 验证）
- [ ] 1.4 新建 `src/libraries/z42.test/tests/discovery.z42`
      — `[Test]` 验证 FindTests 正确返回标注 `[Test]` 的方法

## 阶段 2: z42.test — TestRunner v2
> **等待**：非泛型 Method.Invoke（0.3.12）

- [ ] 2.1 修改 `src/libraries/z42.test/src/TestRunner.z42`
      — 新增 `static TestResult RunOne(MethodInfo method, Type testType)`:
        1. `new testType instance`（通过 `Activator` or 反射构造，待设计）
        2. 顺序执行 `[Setup]` 方法（invoke），失败 → return Skip
        3. 执行 test 方法（invoke），catch 所有异常
        4. 顺序执行 `[Teardown]` 方法（invoke），不影响结果
        5. 返回 `TestResult`
      — 新增 `static TestResult[] Run(Type testType)`:
        遍历 `TestDiscovery.FindTests(testType)`，逐个调 `RunOne`
      — 保留 v0 API（`Begin/Fail/Summary`）向后兼容
- [ ] 2.2 新建 `src/libraries/z42.test/tests/runner_v2.z42`
      — 用 v2 API 自己跑自己（bootstrap 风格测试）
      — 覆盖：pass case / fail case（预期失败捕获）/ skip via [Setup] 失败

## 阶段 3: z42b（builder）— `test` verb
> **等待**：阶段 2 完成

- [ ] 3.1 修改 `src/toolchain/builder/core/builder_cli.z42`
      — `_cmdTest`：解析参数（--filter、--format、--list）
      — （toml 入参时）先经 Compile 相位 build；加载 .zbc：`Std.Runtime.LoadModule(path)` → 枚举 TIDX
      — 调 `Type.GetType(fqn)` 获取 Type（待 API 确认）→ `TestRunner.Run(type)` 执行
      — 输出 pretty / JSON（由 --format 控制）；返回 exit code（0/1/2/3）
- [ ] 3.2 修改 `src/toolchain/builder/core/builder.z42`
      — test 编排：build → 调 z42.test runner 库 → 报告（薄）
- [ ] 3.3 确认 z42.test 在 `z42.builder.z42.toml` 依赖中（MODIFY）

## 阶段 3b: z42.test BenchRunner + z42b `bench` verb
> **等待**：阶段 2（Method.Invoke）

- [ ] 3b.1 新建 `src/libraries/z42.test/src/BenchRunner.z42`
      — 发现 `[Benchmark]` 方法（复用 TestDiscovery 反射基建）+ `Bencher`（warmup/samples/median）执行
- [ ] 3b.2 `builder_cli.z42` 加 `_cmdBench`（--diff、--baseline、--format）→ 调 BenchRunner
- [ ] 3b.3 输出对齐 `xtask bench --diff` + `bench-baselines` 分支门禁格式

## 阶段 4: xtask 集成切换（test + bench）

- [ ] 4.1 确认 xtask `test lib` / bench 当前调用 test-runner 的位置
- [ ] 4.2 修改 xtask 调用：`z42-test-runner <zbc>` → `z42b test <zbc> --format json`（bench 同理）
- [ ] 4.3 修改 xtask JSON 解析（若 z42b test/bench JSON 格式有差异则对齐）
- [ ] 4.4 本地跑 `z42 xtask.zpkg test lib` 验证 22/22 lib 全通

## 阶段 5: test-runner 退役
> **删除前先 oracle 对账**：Rust runner 与 z42b test/bench **双跑结果 parity**（pass/fail/计数一致）
> 后再删——仿 replace-csharp 的 C# oracle 门。GREEN gate 是关键基建，不容回归。

- [ ] 5.0 oracle 对账：同一批 lib 用 Rust runner 与 z42b test 双跑，diff 结果一致（[Test]+[Benchmark]）
- [ ] 5.1 删除 `src/toolchain/test-runner/`（整个目录）
- [ ] 5.2 修改 `src/runtime/Cargo.toml`：移除 test-runner workspace member
- [ ] 5.3 修改 CI 配置（`.github/workflows/`）：移除 cargo build -p z42-test-runner
- [ ] 5.4 搜索并清理其余所有对 `z42-test-runner` / `test-runner` 二进制的引用
- [ ] 5.5 更新 `docs/design/testing/testing.md`：说明 z42b test/bench 已替代

## 阶段 6: GREEN 验证

- [ ] 6.1 `dotnet build src/compiler/z42.slnx` — 无错
- [ ] 6.2 `cargo build --manifest-path src/runtime/Cargo.toml --release` — 无 test-runner 产物
- [ ] 6.3 `dotnet test` — 全绿
- [ ] 6.4 `z42 xtask.zpkg test vm` — 全绿
- [ ] 6.5 `z42 xtask.zpkg test lib` — 22/22（z42b test 驱动）
- [ ] 6.6 `z42 xtask.zpkg test cross-zpkg` — 全绿
- [ ] 6.7 docs/roadmap.md 0.3.13 退出标准打 ✅

## 备注

- 阶段 1 可立即实施（不需要等 Method.Invoke）
- 阶段 3 中 `Type.GetType(fqn)` 可能需要新增到反射 API（0.3.12 阶段评估）
- z42b（builder）骨架已有 `test` verb 占位（builder_cli.z42）；`bench` verb 本变更补齐
