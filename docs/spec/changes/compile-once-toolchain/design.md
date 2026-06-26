# Design: compile-once toolchain

> 操作流程全貌见 [`docs/workflow/bootstrap-and-testing.md`](../../../workflow/bootstrap-and-testing.md)。
> 本文记关键架构决策。

## Architecture

```
[compile job — linux，一次；zpkg 平台无关]
  1. cargo z42vm(release+debug)              (Rust，自包含)
  2. 下载 SDK → .z42/                         (install-z42；保留供交叉验证)
  3. SDK z42c.driver → current z42c (gen1)   ← SDK 唯一用处（gen1 行为新；codegen 改时字节旧）
  4. gen1 → current z42c (gen2)               ← gen2 = 完全 current（新行为 + 新 codegen emit）
  5. fixpoint: gen2 → gen3; assert gen2==gen3 (逐字节, mod BLID)  ← gen2 自洽，配做下一轮 SDK
  6. gen2 → current stdlib + current xtask     ← 用 gen2（非 gen1、非 SDK）编一切
     → Current toolchain = z42c(gen2) + stdlib + xtask  在 artifacts/.z42
  7. xtask --toolchain artifacts/.z42 regen-goldens + compile test-units → *.zbc
  → upload artifact "current-sdk" = artifacts/.z42（z42c=gen2）+ goldens.zbc + units.zbc
        │  [gate: 5 通过才上传 → 不变量"fixpoint gate 发布"]
        ├─ test-interp (per OS)  下载 + cargo z42vm + xtask --toolchain artifacts/.z42 test --no-build (interp)
        ├─ test-jit (linux, 4-shard)  同上 + jit + --shard k/4
        ├─ host-package (per OS) 下载 + cargo z42vm + package
        ├─ package-{ios,android,wasm}  下载 + cross-target
        └─ test-{platform}       下载 + 平台测试
  publish-nightly  needs ← host-package + package-*  (Current → 下一个 nightly)
```

## Decisions

### Decision 1：`--toolchain <dir>` 而非环境变量

**问题**：怎么让 build/test 选"用 SDK 还是 Current"。
**选项**：A — `--toolchain <dir>` 显式参数；B — `Z42_TOOLCHAIN` 环境变量。
**决定**：A。显式、可在一条命令里对账（`--toolchain .z42` vs `--toolchain artifacts/.z42`），
与现有 `Z42_LIBS`/`Z42_PORTABLE_VM`（指 stdlib/vm 单项）正交。默认 `artifacts/.z42`（warm Current）。
`<dir>` 是 `.z42` 布局：`programs/z42c/*.zpkg`（z42c 7 包）+ `libs/*.zpkg`（stdlib）+ `xtask.zpkg`。

### Decision 2：format-bump 兜底（🔴 需 User 裁决）

**问题**：删 C# 后，zbc/zpkg 格式 bump 时新 z42vm 读不了旧 SDK → bootstrap 死锁，无逃生口。
compile-once 把 bootstrap 收成单点后此风险更集中（一炸全炸）。

**选项**：
- **A — committed seed**：仓库提交一份**当前格式**的最小 z42c 种子（`seed/` 或 `.z42-seed/`）。
  compile job 步 2 改为：优先下载 nightly；下载缺失/格式不兼容时**回退 committed seed**。种子在
  能力/格式 bump 的同一 commit 重生提交。
  - 优：最严密——bootstrap 零外部依赖、零格式不匹配、零死锁，且彻底脱离 nightly 可用性。
  - 缺：~3MB 二进制进 git（仅能力 bump 时刷新，非每 commit）。`--toolchain` 让回退透明。
- **B — staged dual-format reader**：format bump 分两 release——先让 `zbc_reader.rs` 临时读
  `[current-1, current]` 两格式（support），下个 release 再切 writer（use）。
  - 优：无 git 二进制。
  - 缺：每次 format bump 要写临时双格式读取代码（与"不做兼容"哲学张力；但 bootstrap-seed.md
    已为种子这个跨进程接口开例外）；reader 改动属 `runtime` 子系统。
- **C — 接受死锁 + 手工恢复**：format bump 当次手工 workflow_dispatch 救。最省事最脆。

**推荐**：**A（committed seed）**。最严密，friction 低（仅能力 bump 刷新），与 compile-once 最搭
（compile job 读 committed seed → Current，SDK 退化成"仓库内种子 + 下载交叉验证源"）。

### 🟢 User 裁决（2026-06-27）：第一版不做破坏性修改，兜底延后

**决定**：compile-once 第一版**不落地任何 format 兜底**（不选 B 的 reader 改动、不引入 committed
seed），保持现状（删 C# 后无逃生口的 §5.3 现实）。**等后续真正遇到 format bump 需求时再应用**
兜底（届时倾向 A committed seed）。

**理由**：① P1–P5 主体（compile-once 去冗余 + fixpoint gate）不依赖 format 兜底，可独立交付价值；
② 现阶段不主动 bump 格式 → 死锁窗口未打开，兜底是"未来才需要"的能力；③ 避免第一版引入破坏性
（reader 双格式 / git 二进制）。**风险登记**：在做任何 zbc/zpkg format bump 的那次变更里，必须**同
一原子步**先落地兜底 A（见 [`bootstrap-seed.md`](../../../../.claude/rules/bootstrap-seed.md) 删种子前
自检清单第 4 条 format 漂移），否则 bump 即死锁。

> ⚠️ 故 P2 第一版**不含** format 兜底任务；§5.3 死锁仍是已知开口，由"未来 format bump 变更"负责闭合。
> Phase 1（`--toolchain`）本就不依赖它。

### Decision 3：compile job 单 OS（linux）产平台无关 artifact

**问题**：compile job 跑几个 OS。
**决定**：**linux 一个**。z42c/stdlib/xtask/goldens/units 是平台无关 zbc/zpkg，编一次即可。
z42vm 是原生二进制——各下游 job 自己 `cargo build`（per host OS）。
**验证 cross-arch 消费可行**：4d 的 ubuntu-24.04-arm 消费 x64 toolchain artifact 已绿。

### Decision 4：fixpoint 进 compile job → gate 一切

**问题**：fixpoint 现在只在 `bootstrap-no-csharp` 验，不 gate 发布。
**决定**：把 fixpoint（gen1==gen2）移进 compile job 步 5，作为**上传 artifact 的前置 gate**。
fixpoint 不过 → 不上传 → 所有下游（含 publish-nightly 链）拿不到 artifact → 不发布。
于是 `bootstrap-no-csharp` job 可删（其唯一独特价值 fixpoint 已被吸收；dotnet PATH-mask 在
compile job 里保留一行即可）。

### Decision 5：SDK 只编 z42c gen1 → gen2，用 **gen2** 编其余一切（codegen 改动同轮生效）

**问题**：compile 序列里 SDK 编到哪步、用哪一代 z42c 编 stdlib/xtask/测试？
**背景（自举的微妙点）**：`gen1 = SDK 编当前 z42c 源`。gen1 的**行为**=当前源（改动立即生效），
但 gen1 **自身字节**是 SDK（旧 z42c）emit 的。若改的是 z42c **codegen**，gen1 行为新、自身字节旧——
要让被测/被发布的工具链**完全**是当前 codegen，必须用 `gen2 = gen1 再编自己`（gen2 由 gen1 用新
codegen emit）。否则 codegen 改动要到下一次 nightly 才在 z42c 字节里完全体现。
**决定**：
- SDK **只**编 gen1（唯一用处，打破 chicken-egg）。
- **gen1 编 gen2**；fixpoint 验 `gen2 == gen3`（gen2 自洽，配做下一轮 SDK）。
- **用 gen2** 编 stdlib + xtask + goldens + 跑测试 + 打包/发布。被测/被发布的是端到端、完全
  current（含 codegen）的工具链。SDK 影响面收缩到"仅产出 gen1 这个中间体"。
- 步 3-6 直接 `z42vm <driver>.zpkg -- build …` 调 z42c.driver（SDK→gen1→gen2），**xtask 到步 6 才由
  gen2 编出**。
- 代价：z42c 多编 1 代（gen2）+ fixpoint 再 1 代（gen3），~2-4min，换"codegen 改动同轮完全生效"+
  发布的是真 fixpoint。
- 保留 SDK 还可**差分验证**：同源用 SDK 与 gen2 各编比对（z42c 未改时字节一致 = 回归探测）。

## Implementation Notes

- **`build sdk` 输出布局**：复用 `xtask package` 的 `.z42`/SDK 组装逻辑（已有），但目标是
  "可被 `--toolchain` 消费的开发态工具链"，非发行 SDK（无需 apphost trampoline，zpkg 直跑即可）。
- **goldens/units 预编译**：`regen` 已把 source.z42 → .zbc；compile job 跑一次 regen，下游
  `test ... --no-build` 直接跑 .zbc（跳过 z42c 重编）。需确认 `--no-build` 全链（vm/stdlib/cross-zpkg）
  都尊重预编译产物。
- **z42vm debug vs release**：vm goldens 跑 debug z42vm（断言更全），stdlib jit fork release z42vm。
  下游 job 两个都 cargo build（compile job 不产 z42vm，平台相关）。
- **artifact 大小**：zpkg(~6MB) + goldens.zbc + units.zbc(~?)。可控。

## Testing Strategy

- **每 Phase 独立 CI 验证**：P1 本地 + CI 验 `--toolchain` 两套等价；P2 compile job 产 artifact +
  fixpoint gate；P3 下游消费全绿且时长下降；P4/P5 重命名/删 job/删脚本后全绿。
- **交叉验证**：compile job 的 fixpoint（gen1==gen2）是核心新增门。
- **回归**：现有 GREEN gate（vm/cross-zpkg/stdlib/compiler-z42）覆盖不减——只是改为消费预编译 Current。
- **本地**：`xtask --toolchain .z42 build sdk` + `--toolchain artifacts/.z42 test` 可复现 CI 链路。
