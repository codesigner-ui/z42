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

#### Scenario: 编出完整 .z42 布局
- **WHEN** `xtask --toolchain .z42 build sdk --out artifacts/.z42`
- **THEN** `artifacts/.z42` 含 `programs/z42c/*.zpkg`（7 包）+ `libs/*.zpkg`（stdlib）+ `xtask.zpkg`

#### Scenario: 用 gen2 编 stdlib/xtask（codegen 改动同轮生效）
- **WHEN** 仅改动 z42c **codegen**（不改行为）后 `build sdk`
- **THEN** 产出的 z42c 是 gen2（gen1 再编自己），stdlib/xtask 由 gen2 编 → 被测/被发布的字节完全体现该 codegen 改动，无需等下一次 nightly

### Requirement: compile job 单点产平台无关 artifact

CI 有且仅有一个 `compile` job（linux）产出 Current toolchain artifact，所有下游消费它。

#### Scenario: 一次编译，全下游复用
- **WHEN** compile job 成功
- **THEN** 上传 artifact `current-sdk`（`artifacts/.z42` + goldens.zbc + units.zbc）；test-interp / test-jit / host-package / package-* / platform-test 全部下载该 artifact，不再各自 `ci-bootstrap-nocs`

#### Scenario: cross-arch 消费平台无关 zbc
- **WHEN** ubuntu-24.04-arm 的下游 job 下载 x64 compile job 产的 artifact
- **THEN** zpkg/zbc 平台无关，正常加载执行（z42vm 由各 host `cargo build`）

### Requirement: fixpoint gate 发布

compile job 内验自举不动点 `gen2 == gen3`（逐字节，mod BLID），作为上传 artifact 的前置 gate。

#### Scenario: 稳态跳过 gen3（gen1==gen2）
- **WHEN** 本周期没改 z42c codegen → gen1 == gen2
- **THEN** gen2 自动是不动点（gen1==gen2 ⟹ gen2==gen3），**跳过 gen3**，直接上传 artifact

#### Scenario: 改 codegen 触发 gen3 并验证
- **WHEN** 改了 z42c codegen → gen1 != gen2
- **THEN** 编 gen3，断言 gen2 == gen3；通过则上传，失败则阻断

#### Scenario: 不动点失败阻断一切
- **WHEN** gen2 != gen3（自编自不稳定）
- **THEN** 不上传 artifact → 所有下游 job 拿不到输入而失败 → publish-nightly 发不出 → 不会发出未验自洽性的 nightly

### Requirement: SDK 仅产 gen1，gen2 编一切（xtask 例外）

compile job 用 SDK 只编 xtask（驱动）+ gen1；其后 gen1→gen2，用 gen2 编 stdlib/toolchain/goldens/测试/发布。

#### Scenario: SDK 影响面收缩到 xtask 驱动 + gen1
- **WHEN** compile job 运行
- **THEN** SDK 仅用于 S2（编 xtask 驱动）+ S3（编 gen1）；S4 起不再用 SDK；gen2 编其余一切

#### Scenario: xtask 不进 SDK
- **WHEN** 组装本地 SDK（artifacts/.z42）
- **THEN** SDK 只含 z42c+stdlib+toolchain（src/toolchain），**不含 xtask**；xtask 落 artifacts/xtask/，不分发

#### Scenario: 行为改动经 gen1 立即生效
- **WHEN** 改 z42c **行为**（非 codegen）后 compile
- **THEN** gen1 已是新行为 → gen2 继承 → stdlib/测试立即反映新行为

### Requirement: cross-bootstrap 交叉验证（本地 SDK 当种子）

`bootstrap-no-csharp` job 改造成 cross-bootstrap：种子换成本地 SDK 的打包发布形态，重跑 S2+S3+S5。

#### Scenario: 本地 SDK 能当下一轮种子
- **WHEN** cross-bootstrap 用打包发布形态的本地 SDK 重跑 S2+S3+S5
- **THEN** 编 xtask 成功 + 编 z42c 逐字节==本地 SDK 自带（gen2==gen3）+ 编 stdlib 逐字节==发布 → 进 publish-nightly needs

#### Scenario: 发布前自洽不过则不发
- **WHEN** cross-bootstrap 任一字节比对失败
- **THEN** job 失败 → publish-nightly 不触发 → 不会发出"当不了下一轮种子"的 nightly

## MODIFIED Requirements

### Requirement: CI 自举不再 per-job 重复

**Before:** 测试 job（build-and-test×4 / vm-jit×4 / stdlib-jit×4 / compiler-z42-stdlib）各自跑
`ci-bootstrap-nocs.sh`（~12min/job），z42c+stdlib+xtask 被独立全量编 ~16 次。

**After:** compile job 编一次 → 上传 artifact → 测试 job 下载 + `cargo z42vm` +
`--toolchain artifacts/.z42 test --no-build`，不再自举。z42c+stdlib+xtask 全 CI 编 1 次。

### Requirement: 不动点进入发布门禁

**Before:** 不动点只在 `bootstrap-no-csharp` job 验，publish-nightly 的 needs 不含它 →
理论上能发出未验自洽性的 nightly。

**After:** 不动点门移进 compile job S4（条件触发 gen3）作为 artifact 上传 gate；`bootstrap-no-csharp`
**改造**成 cross-bootstrap（种子换本地 SDK）进 publish-nightly needs → 双重 gate：上传 gate + 种子可重建 gate。

## CI Topology Steps（受影响的拓扑，按消费顺序）

- [ ] compile (linux)：cargo z42vm + 下载 SDK + S2 编 xtask + S3 编 gen1 + S4 gen1→gen2 + 条件不动点(gen1==gen2 跳 gen3) + S5 gen2 编 stdlib/toolchain + S6 goldens/units + 上传 current-sdk
- [ ] test-interp (per OS)：下载 artifact + cargo z42vm + `--no-build` interp
- [ ] test-jit (linux, 4-shard)：下载 artifact + cargo z42vm + `--no-build` jit + `--shard k/4`
- [ ] host-package (per OS) / package-{ios,android,wasm}：下载 artifact + 打包
- [ ] cross-bootstrap：用打包发布形态本地 SDK 当种子重跑 S2+S3+S5 → 进 publish needs
- [ ] publish-nightly：needs package-* → 间接 gate 于 compile 的 fixpoint

## 边界（不变量，CI 必须保证）

- **format-bump 兜底**（Decision 2，A/B/C 待裁决）：删 C# 后无逃生口，需在 P2 落地。
- **SDK 只在 compile job**：下游零 SDK 依赖。
- **测的 == 发的**：测试与发布消费同一 artifact。
