# Spec: z42-test-runner + Test Metadata

## ADDED Requirements

### Requirement: 测试发现 (`[Test]` attribute)

#### Scenario: 发现单个 Test 方法

- **WHEN** 一个 .zbc 文件含一个标 `[Test]` attribute 的函数 `test_foo`
- **WHEN** 运行 `z42-test-runner <file>.zbc`
- **THEN** runner 检出该函数为测试用例

#### Scenario: 忽略未标注函数

- **WHEN** .zbc 含函数 `helper`（无 `[Test]` attribute）
- **THEN** runner **不**把 `helper` 当作测试

#### Scenario: 拒绝错误签名

- **WHEN** 标 `[Test]` 的函数有参数或非 void 返回值
- **THEN** runner 报错 `"test function '<name>' has invalid signature: must be fn() -> void"` 并 exit 2

#### Scenario: Skip attribute 跳过

- **WHEN** 函数同时标 `[Test]` 和 `[Skip(reason: "...")]`
- **THEN** runner 检出但不执行；状态标 `skipped`，原因写入结果

#### Scenario: Ignore attribute 完全忽略

- **WHEN** 函数同时标 `[Test]` 和 `[Ignore]`
- **THEN** runner 不计入 total，输出中不出现该测试

---

### Requirement: 测试执行

#### Scenario: 通过的测试

- **WHEN** 测试函数正常返回
- **THEN** TestResult.status = "passed"，记录 duration_ms

#### Scenario: assertion 失败

- **WHEN** 测试中调用 `Assert.eq(1, 2)`
- **THEN** z42.test.TestFailure 抛出，runner catch
- **AND** TestResult.status = "failed"，含 actual / expected / location

#### Scenario: 测试中抛任意异常

- **WHEN** 测试中代码抛出非 TestFailure 异常
- **THEN** runner catch 并标记 failed，failure.message 含异常类型与原始消息

#### Scenario: 测试超时

- **WHEN** 测试运行超过 `--timeout` 秒（默认 60）
- **THEN** runner 中断该测试，status = "failed"，failure.message = "timeout after Ns"

---

### Requirement: CLI 接口

#### Scenario: 单文件路径

- **WHEN** 执行 `z42-test-runner foo.zbc`
- **THEN** runner 处理该单一 .zbc

#### Scenario: 目录递归

- **WHEN** 执行 `z42-test-runner src/libraries/z42.io/tests/`
- **THEN** runner 递归收集所有 .zbc 文件

#### Scenario: --filter 正则过滤

- **WHEN** 执行 `z42-test-runner . --filter '^test_string'`
- **THEN** runner 只执行名字匹配 `^test_string` 的测试

#### Scenario: --format 切换

- **WHEN** 执行 `z42-test-runner . --format tap`
- **THEN** 输出符合 TAP 13 规范
- **AND** `--format json` 输出符合 design.md Decision 6 的 JSON 格式
- **AND** `--format pretty` 输出 TTY 友好（含颜色）

#### Scenario: 默认 format 取决于 TTY

- **WHEN** stdout 是 TTY
- **THEN** 默认 `--format pretty`
- **AND** stdout 非 TTY 时默认 `--format tap`

#### Scenario: exit code

- **WHEN** 全部测试通过
- **THEN** exit 0
- **AND** 任一测试失败 → exit 1
- **AND** 编译错误或 IO 错误 → exit 2
- **AND** 没有测试可运行 → exit 3

---

### Requirement: z42.test 库 API

#### Scenario: Assert.eq 通过

- **WHEN** `Assert.eq(2 + 2, 4)`
- **THEN** 不抛异常

#### Scenario: Assert.eq 失败

- **WHEN** `Assert.eq(1, 2)`
- **THEN** 抛 TestFailure，actual="1"，expected="2"

#### Scenario: Assert.throws 捕获

- **WHEN** `let e = Assert.throws<DivByZero>(|| { 1 / 0 })`
- **THEN** 返回捕获的 DivByZero 实例
- **AND** 函数不抛异常时 throws 自己抛 TestFailure

#### Scenario: Assert.near 浮点近似

- **WHEN** `Assert.near(1.0, 1.0 + 1e-12)` (默认 epsilon = 1e-9)
- **THEN** 通过
- **AND** `Assert.near(1.0, 1.1)` 失败

#### Scenario: Assert.fail 主动失败

- **WHEN** `Assert.fail("not implemented")`
- **THEN** 抛 TestFailure，message="not implemented"

---

### Requirement: 元数据 front-matter

#### Scenario: 合法 front-matter

- **WHEN** .z42 文件首行为 `// @test-tier: stdlib:z42.io`
- **THEN** 测试发现工具 / test-changed.sh 识别该文件归属为 z42.io

#### Scenario: 必填字段缺失

- **WHEN** .z42 测试文件无 `@test-tier`
- **THEN** test-changed.sh 输出警告，但不阻塞执行

#### Scenario: 非法 tier 值

- **WHEN** front-matter 含 `// @test-tier: unknown`
- **THEN** test-changed.sh 报错并 exit 非零

---

### Requirement: 增量测试 scripts/test-changed.sh

#### Scenario: 仅修改某 stdlib 库

- **WHEN** git diff 仅触及 `src/libraries/z42.io/foo.z42`
- **THEN** 输出 `{compiler: false, vm_core: false, stdlib: ["z42.io"], integration: false}`

#### Scenario: 修改 VM 核心

- **WHEN** git diff 触及 `src/runtime/src/gc/heap.rs`
- **THEN** 输出 `{compiler: false, vm_core: true, stdlib: ["z42.core", ...], integration: true}`（保守扩散）

#### Scenario: 修改编译器

- **WHEN** git diff 触及 `src/compiler/z42.Compiler/Parser.cs`
- **THEN** 输出 `{compiler: true, vm_core: true, stdlib: [...all...], integration: true}`

#### Scenario: 仅修改文档

- **WHEN** git diff 仅触及 `docs/design/foo.md`
- **THEN** 输出全 false / 空数组

#### Scenario: --dry-run 不执行

- **WHEN** 执行 `./scripts/test-changed.sh --dry-run`
- **THEN** 输出受影响集合 JSON 但不触发任何测试

---

### Requirement: just 入口接入

#### Scenario: just test-changed

- **WHEN** 执行 `just test-changed`
- **THEN** 内部调用 `scripts/test-changed.sh`，根据输出触发对应子集
- **AND** 替换 P0 中的 "P2 待实施" 占位

#### Scenario: just test-stdlib <lib>

- **WHEN** 执行 `just test-stdlib z42.core`
- **THEN** 调用 `z42-test-runner src/libraries/z42.core/tests/`

#### Scenario: just test-stdlib (无参数)

- **WHEN** 执行 `just test-stdlib`
- **THEN** 对每个 stdlib 库依次跑 `z42-test-runner`

---

### Requirement: 文档同步

#### Scenario: testing.md 元数据规范

- **WHEN** 阅读 [docs/design/testing.md](docs/design/testing.md)
- **THEN** 含章节：归属规则 / front-matter 规范 / `[Test]` attribute / 测试发现 / TAP 输出 / JSON 输出 / 增量测试规则

#### Scenario: test-runner.md 实现原理

- **WHEN** 阅读 [docs/design/test-runner.md](docs/design/test-runner.md)
- **THEN** 含章节：架构 / discover 算法 / runner 调用流程 / formatter 接口 / 错误处理

#### Scenario: dev.md 含 runner 命令

- **WHEN** 阅读 [docs/dev.md](docs/dev.md)
- **THEN** 存在 "z42-test-runner" 段，列出 CLI 用法
