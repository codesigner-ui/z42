# Spec: bootstrap-toolchain（共享 host SDK + 显式 SDK/Current 边界）

> 本变更是流程/CI 重构，不改 z42c/stdlib 功能。场景以 **CI 拓扑 + xtask 行为 + 自举不变量**
> 为可验证对象。

## ADDED Requirements

### Requirement: `--toolchain <dir>` 选择器

xtask build/test/package 命令接受 `--toolchain <dir>`，据此定位 z42c + stdlib + xtask
（`.z42` 布局），用于在 SDK（`.z42`）与 Current（`artifacts/.z42`）间切换。

#### Scenario: 默认指向 Current
- **WHEN** 不传 `--toolchain`
- **THEN** 定位 `artifacts/.z42`（warm Current toolchain）

#### Scenario: 显式指向 SDK
- **WHEN** `xtask --toolchain .z42 <cmd>`
- **THEN** 用下载的 SDK 工具链执行，不触碰 `artifacts/.z42`

#### Scenario: 目录缺失明确报错
- **WHEN** `--toolchain <dir>` 指向不含 `programs/z42c/*.zpkg` 的目录
- **THEN** 报错引导（"无 toolchain，请先 install-z42 或 build sdk"），非裸 crash

### Requirement: `xtask build sdk` 产出 Current toolchain

新增 `xtask build sdk [--out <dir>]`，把当前源码编成 `.z42` 布局的开发态工具链。

#### Scenario: 编出完整 .z42 布局（matched set）
- **WHEN** `xtask --toolchain .z42 build sdk --out artifacts/.z42`
- **THEN** `artifacts/.z42` 含 `programs/z42c/*.zpkg`（7 包）+ `libs/*.zpkg`（stdlib）+ toolchain；**不含 xtask**（落 artifacts/xtask/）

#### Scenario: 成对分代 codegen 改动同轮生效
- **WHEN** 仅改动 z42c **codegen**（不改行为）后 `build sdk`
- **THEN** 发布的是 gen2 对（gen1 再编 {stdlib, z42c}），{z42c, stdlib} 都由 gen2 codegen emit → 被测/被发布的字节完全体现该 codegen 改动，无需等下一次 nightly

### Requirement: compile job 单点产平台无关 artifact

CI 有且仅有一个 `compile` job（linux）产出 Current toolchain artifact，所有下游消费它。

#### Scenario: 一次编译，全下游复用
- **WHEN** compile job 成功
- **THEN** 上传 artifact `current-sdk`（`artifacts/.z42` + goldens.zbc + units.zbc）；test-interp / test-jit / host-package / package-* / platform-test 全部下载该 artifact，不再各自 `ci-bootstrap-nocs`

#### Scenario: cross-arch 消费平台无关 zbc
- **WHEN** ubuntu-24.04-arm 的下游 job 下载 x64 compile job 产的 artifact
- **THEN** zpkg/zbc 平台无关，正常加载执行（z42vm 由各 host `cargo build`）

### Requirement: 版本成对匹配（matched set）

z42c(emit 格式) + z42vm(read 格式) + stdlib(身处格式) 必须同格式 = 一个 matched set；bootstrap = 从
SDK set 过渡到本地 SDK set。

#### Scenario: 跑哪份 z42c 用哪份 z42vm
- **WHEN** 运行 SDK z42c（旧格式 zpkg）/ gen2（当前格式 zpkg）
- **THEN** 旧格式 zpkg 用旧格式 z42vm、当前格式 zpkg 用当前 z42vm；不 bump format 轮一个 z42vm 通吃，bump 轮过渡期两个

### Requirement: 成对分代 {z42c, stdlib}，发布 gen2 对

每代是 `{z42c, stdlib}` 一对：gen1 = SDK 编一对（stdlib 先编→z42c 用它解析）；gen2 = gen1 编一对（发布件）。

#### Scenario: SDK 影响面收缩到 xtask + gen1 对
- **WHEN** compile job 运行
- **THEN** SDK 仅用于 S2（编 xtask）+ S3（编 gen1 的 {stdlib, z42c}）；S4 起不再用 SDK；gen2 对编其余一切

#### Scenario: 发布的 stdlib 必须当前格式才能跑
- **WHEN** 本周期 bump 了 zpkg 格式
- **THEN** gen1 的 stdlib 是旧格式、被当前 z42vm strict-pin 拒 → 不能发；发布件必须 gen2 编（当前格式）→ 永远能跑

#### Scenario: xtask 不进 SDK
- **WHEN** 组装本地 SDK set（artifacts/.z42）
- **THEN** set 只含 z42c+stdlib+toolchain（src/toolchain），**不含 xtask**；xtask 落 artifacts/xtask/，不分发

#### Scenario: 行为改动经 gen1 立即生效
- **WHEN** 改 z42c **行为**（非 codegen）后 compile
- **THEN** gen1 已是新行为 → gen2 继承 → stdlib/测试立即反映新行为

### Requirement: 发布门 = 三个正交属性（完整 / 稳定 / 正确）

发布前必须过完整、稳定、正确三关，缺一不可——`{gen2}=={gen3}` 只证稳定不证正确。

#### Scenario: 完整（self-hosting closure）
- **WHEN** gen2 编 {z42c, stdlib} + cross-bootstrap 重建
- **THEN** 都无报错 → 证当前工具链能从源码编出完整工具链

#### Scenario: 稳定（条件不动点）
- **WHEN** 比较 gen1 vs gen2 对：== 则跳 gen3；≠ 则编 gen3 断言 `{gen2}=={gen3}`
- **THEN** 通过则上传 artifact，失败则不上传 → 阻断发布

#### Scenario: 正确（行为测试进 needs）
- **WHEN** gen2 工具链跑 vm goldens + stdlib [Test] + cross-zpkg
- **THEN** 全绿才允许 publish-nightly；**测试腿（test-interp/test-jit 等）进 publish-nightly needs**——否则"稳定地错"（gen2/gen3 同 bug）能过不动点却发出行为错误的 nightly

### Requirement: cross-bootstrap 交叉验证（本地 SDK set 当种子）

`bootstrap-no-csharp` job 改造成 cross-bootstrap：种子换成本地 SDK set 的打包发布形态，重跑 S2-S4。

#### Scenario: 本地 SDK set 能当下一轮种子
- **WHEN** cross-bootstrap 用打包发布形态的本地 SDK set 重跑 S2-S4
- **THEN** 编 xtask 成功 + 编 {z42c, stdlib} 逐字节==本地 set 自带（{gen2}=={gen3}）→ 进 publish-nightly needs

#### Scenario: 发布前自洽不过则不发
- **WHEN** cross-bootstrap 任一字节比对失败
- **THEN** job 失败 → publish-nightly 不触发 → 不会发出"当不了下一轮种子"的 nightly

## MODIFIED Requirements

### Requirement: CI 自举不再 per-job 重复

**Before:** 测试 job（build-and-test×4 / vm-jit×4 / stdlib-jit×4 / compiler-stdlib）各自跑
`ci-bootstrap.sh`（~12min/job），z42c+stdlib+xtask 被独立全量编 ~16 次。

**After:** compile job 编一次 → 上传 artifact → 测试 job 下载 + `cargo z42vm` +
`--toolchain artifacts/.z42 test --no-build`，不再自举。`{z42c, stdlib}` 全 CI 编 1 对（+条件 gen3）。

### Requirement: 发布门三关进 needs（完整 / 稳定 / 正确）

**Before:** 不动点只在 `bootstrap-no-csharp` job 验，publish-nightly needs 不含它；且测试腿不在 needs →
理论上能发出"未验自洽性"或"行为错误"的 nightly。

**After:** ① 稳定——不动点门移进 compile job S4（条件 gen3）作上传 gate；② 完整——cross-bootstrap（种子换
本地 SDK set）进 needs；③ **正确——test-interp / test-jit（+ stdlib/cross-zpkg）进 publish-nightly needs**。
三关全过才发。修了"稳定地错也能发"的缺口。

## CI Topology Steps（受影响的拓扑，按消费顺序）

- [ ] compile (linux)：cargo z42vm + 下载 SDK + S2 编 xtask + S3 gen1={stdlib,z42c} + S4 gen2={stdlib,z42c} + 条件不动点(gen1==gen2 跳 gen3) + gen2 编 toolchain + S5 goldens/units + 上传 current-sdk
- [ ] test-interp (per OS) / test-jit (linux, 4-shard)：下载 artifact + cargo z42vm + `--no-build` → **进 publish needs（正确关）**
- [ ] host-package (per OS) / package-{ios,android,wasm}：下载 artifact + 打包
- [ ] cross-bootstrap：用打包发布形态本地 SDK set 当种子重跑 S2-S4 → 进 publish needs（完整关）
- [ ] publish-nightly：needs package-* + cross-bootstrap + test-interp + test-jit（三关）

## 边界（不变量，CI 必须保证）

- **format-bump 兜底**（Decision 2）：第一版不做，延后到未来 format bump 变更同步落地。
- **版本成对匹配**：z42c+z42vm+stdlib 同格式;跑 SDK z42c 用旧 vm、跑 gen2 用当前 vm。
- **SDK set 只在 compile job**：下游零 SDK 依赖。
- **测的 == 发的**：测试与发布消费同一 artifact；测试腿进 publish needs（正确关）。
