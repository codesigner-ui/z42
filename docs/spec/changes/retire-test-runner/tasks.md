# Tasks: 退役 z42-test-runner

> 状态：🟡 进行中（等待前置） | 创建：2026-06-19
> **前置**：boxing 机制（0.3.11）+ 非泛型 Method.Invoke（0.3.12）
> **子系统**：stdlib（z42.test）+ z42c + toolchain（实施时逐个锁，按阶段）

## 进度概览

- [ ] 阶段 1: z42.test — TestResult + TestDiscovery（无需 Method.Invoke，纯反射查询）
- [ ] 阶段 2: z42.test — TestRunner v2（需 Method.Invoke，等 0.3.12）
- [ ] 阶段 3: z42c.driver — `test` 子命令（需 TestRunner v2，等阶段 2）
- [ ] 阶段 4: xtask 集成切换
- [ ] 阶段 5: test-runner 退役 + Cargo 清理
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

## 阶段 3: z42c.driver — `test` 子命令
> **等待**：阶段 2 完成

- [ ] 3.1 新建 `src/z42c/z42c.driver/src/TestCommand.z42`
      — `TestCommand.Run(argv: string[])` 解析参数（--filter、--format、--list）
      — 加载 .zbc：`Std.Runtime.LoadModule(path)` → 枚举 TIDX 条目
      — 调 `Type.GetType(fqn)` 获取 Type 对象（待 API 确认）
      — 调 `TestRunner.Run(type)` 执行
      — 输出 pretty / JSON（由 --format 控制）
      — 返回 exit code（0=全通, 1=有失败, 2=参数错误）
- [ ] 3.2 修改 `src/z42c/z42c.driver/src/Main.z42`
      — 在命令分发中加入 `"test"` → `TestCommand.Run(argv[1:])`
      — 更新顶部注释中已实现命令列表
- [ ] 3.3 确认 z42.test 在 z42c.driver.z42.toml 依赖中（修改 MODIFY）

## 阶段 4: xtask 集成切换

- [ ] 4.1 确认 xtask `test lib` 命令当前调用 test-runner 的位置
- [ ] 4.2 修改 xtask 调用：`z42-test-runner <zbc>` → `z42c test <zbc> --format json`
- [ ] 4.3 修改 xtask JSON 解析（若 z42c test JSON 格式有差异则对齐）
- [ ] 4.4 本地跑 `z42 xtask.zpkg test lib` 验证 22/22 lib 全通

## 阶段 5: test-runner 退役

- [ ] 5.1 删除 `src/toolchain/test-runner/`（整个目录）
- [ ] 5.2 修改 `src/runtime/Cargo.toml`：移除 test-runner workspace member
- [ ] 5.3 修改 CI 配置（`.github/workflows/`）：移除 cargo build -p z42-test-runner
- [ ] 5.4 搜索并清理其余所有对 `z42-test-runner` / `test-runner` 二进制的引用
- [ ] 5.5 更新 `docs/design/testing/testing.md`：说明 z42c test 已替代

## 阶段 6: GREEN 验证

- [ ] 6.1 `dotnet build src/compiler/z42.slnx` — 无错
- [ ] 6.2 `cargo build --manifest-path src/runtime/Cargo.toml --release` — 无 test-runner 产物
- [ ] 6.3 `dotnet test` — 全绿
- [ ] 6.4 `z42 xtask.zpkg test vm` — 全绿
- [ ] 6.5 `z42 xtask.zpkg test lib` — 22/22（z42c test 驱动）
- [ ] 6.6 `z42 xtask.zpkg test cross-zpkg` — 全绿
- [ ] 6.7 docs/roadmap.md 0.3.13 退出标准打 ✅

## 备注

- 阶段 1 可立即实施（不需要等 Method.Invoke）
- 阶段 3 中 `Type.GetType(fqn)` 可能需要新增到反射 API（0.3.12 阶段评估）
- z42c.driver 目前已有 `build` 命令占位；`test` 命令结构同理
