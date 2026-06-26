# Design: compile-once toolchain

> 操作流程全貌见 [`docs/workflow/bootstrap-and-testing.md`](../../../workflow/bootstrap-and-testing.md)。
> 本文记关键架构决策。

## Architecture

### 命名与角色

| 名 | 是什么 | 位置 | 进 SDK 发布? |
|----|--------|------|------|
| **SDK** | 下载的上一版 nightly（launcher + z42c + z42vm + stdlib，**无 xtask**）| `.z42/` | — |
| **本地 SDK / Current** | 当前源码编出 | `artifacts/.z42/` | ✅（成为下一个 nightly）|
| **xtask** | 项目构建工具（`scripts/xtask*.z42`）；既是构建驱动、也是交叉验证靶子 | `artifacts/xtask/` | ❌ 永不进 SDK / 不分发 |
| **z42vm** | Rust 原生 VM | 各 host `cargo build` | ❌ 不在 zpkg 链里 |

**代际**：`gen1` = SDK 编当前 z42c 源（**行为**新、**字节**由 SDK codegen 出）；`gen2` = gen1 再编自己（**字节也新** = 当前 codegen）。**发布的是 gen2**。

### 主构建（linux，一次；zpkg 平台无关）

```
S0  cargo z42vm(release+debug)                          (Rust 原生，不在 SDK 的 zpkg 链)
S1  install-z42 → 下载 SDK → .z42/                       (打破 chicken-egg；保留供交叉验证/差分)
S2  SDK z42c + SDK stdlib → 编 xtask 源 → artifacts/xtask/   (构建驱动；xtask 不进 SDK)
S3  (xtask 驱动) SDK z42c → 编当前 stdlib 源 → stdlib(解析用)  (供 gen1/gen2 解析；SDK codegen，不发布)
S4  SDK z42c → 编当前 z42c 源（解析=S3 stdlib）→ gen1        (SDK 最后用处)
S5  gen1 → 再编 z42c 源 → gen2                            (gen2 = 当前 codegen = 发布件)
    ┌ 条件不动点门（见 Decision 6）：比较 gen1 vs gen2
    │   == → 没改 codegen，gen2 自动是不动点 → 跳过 gen3
    └   ≠ → 改了 codegen → 编 gen3，断言 gen2==gen3（逐字节 mod BLID）
S6  gen2 → 编当前 stdlib(发布件,当前格式) + 当前 toolchain(src/toolchain) → artifacts/.z42/
    → 本地 SDK = z42c(gen2) + stdlib(gen2编) + toolchain(gen2编)
    （S3 解析 stdlib 是 SDK codegen/旧格式，仅自举用；发布的 stdlib 必须 gen2 编 = 当前格式才能跑——
      见 Decision 5。稳态 S3==S6 时 S6 stdlib 重编可跳过，micro-opt）
S7  xtask --toolchain artifacts/.z42 regen goldens + 编 test-units → *.zbc（供下游 --no-build）
→ 上传 artifact "current-sdk" = artifacts/.z42 + goldens.zbc + units.zbc
      [gate：S5 不动点通过才上传 → 不变量"不动点 gate 发布"]
```

### 下游（消费 artifact）

```
current-sdk ─┬─ test-interp (per OS)   下载 + cargo z42vm + --toolchain artifacts/.z42 test --no-build (interp)
             ├─ test-jit (linux,4-shard)  同上 + jit + --shard k/4
             ├─ host-package (per OS)  下载 + cargo z42vm + package
             ├─ package-{ios,android,wasm} / test-{platform}  下载 + cross/平台
             └─ cross-bootstrap（交叉验证，独立 job；见 Decision 7）
                  用"打包发布形态的本地 SDK"当种子，重跑 S2-S6：编 xtask + z42c + stdlib
                  验：z42c 逐字节==本地SDK自带(gen2==gen3 真不动点) / stdlib 逐字节==发布 / xtask 编成功
                  = 提前演一遍"下一周期 bootstrap"，证发布的 SDK 自洽可用
publish-nightly  needs ← package-* + cross-bootstrap  (本地 SDK → 下一个 nightly)
```

## Decisions

### Decision 1：`--toolchain <dir>` 而非环境变量

**问题**：怎么让 build/test 选"用 SDK 还是 Current"。
**选项**：A — `--toolchain <dir>` 显式参数；B — `Z42_TOOLCHAIN` 环境变量。
**决定**：A。显式、可在一条命令里对账（`--toolchain .z42` vs `--toolchain artifacts/.z42`），
与现有 `Z42_LIBS`/`Z42_PORTABLE_VM`（指 stdlib/vm 单项）正交。默认 `artifacts/.z42`（warm Current）。
`<dir>` 是 `.z42` 布局：`programs/z42c/*.zpkg`（z42c 7 包）+ `libs/*.zpkg`（stdlib）+ toolchain。
**不含 xtask**（xtask 是驱动、在 `artifacts/xtask/`，不在 `--toolchain` 指向的 SDK 里）。

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

### Decision 4：不动点进 compile job → gate 一切

**问题**：不动点现在只在 `bootstrap-no-csharp` 验，不 gate 发布。
**决定**：把不动点门移进 compile job S5（gen1 vs gen2 比较 + 条件 gen3），作为**上传 artifact 的前置 gate**。
不过 → 不上传 → 所有下游（含 publish-nightly 链）拿不到 artifact → 不发布。
（`bootstrap-no-csharp` job 不删而是**改造**成 cross-bootstrap 交叉验证 job，见 Decision 7。）

### Decision 5：SDK 只编 gen1，用 **gen2** 编其余一切（codegen 改动同轮生效）

**问题**：compile 序列里 SDK 编到哪步、用哪一代 z42c 编 stdlib/toolchain/测试？
**背景（自举的微妙点）**：`gen1 = SDK 编当前 z42c 源`。gen1 的**行为**=当前源（改动立即生效），
但 gen1 **自身字节**是 SDK（旧 z42c）emit 的。若改的是 z42c **codegen**，gen1 行为新、自身字节旧——
要让被测/被发布的工具链**完全**是当前 codegen，必须用 `gen2 = gen1 再编自己`。否则 codegen 改动要到
下一次 nightly 才在 z42c 字节里完全体现。
**决定**：
- SDK **编 gen1**（打破 chicken-egg）+ **编一份当前 stdlib 供解析**（见下）；此外仅留作交叉验证/差分。
- **gen1 编 gen2**（S5）；**发布的是 gen2**。
- **用 gen2** 编 stdlib(发布件) + toolchain(src/toolchain) + goldens + 跑测试 + 打包/发布。被测/被发布的是
  端到端、完全 current（含 codegen + format）的工具链。
- **xtask 例外**：xtask 不进 SDK，是构建驱动——S2 由 **SDK** 先编出来驱动全程；其"gen2 形态"在
  cross-bootstrap（Decision 7）里由本地 SDK 重编，落 `artifacts/xtask/`。
- **stdlib 出现两次（解析 vs 发布）**：z42c 源 import stdlib，故 gen1/gen2 编 z42c **需要 stdlib 解析**，
  而 gen2 编的 stdlib 要 gen2 之后才有 → chicken-egg。解法：
  - **S3：SDK z42c 编当前 stdlib → 解析用 stdlib**（供 S4/S5 的 gen1/gen2 解析）。为什么不直接用下载 SDK
    自带的 stdlib：那是上一版，若当前 z42c 用了本周期新加的 stdlib API 就解析不到 → 先编一份当前的最稳
    （= 现有 bootstrap 的 "stdlib 先编" 顺序）。
  - **S6：gen2 编当前 stdlib → 发布 stdlib**（当前 codegen + 当前格式）。
  - **为什么发布件必须 gen2 编（不能发 S3 那份 SDK 编的）**：S3 stdlib 是 SDK codegen/格式。**不 bump
    format 的轮它其实能跑**；但 **format bump 那轮**，SDK 编的旧格式 stdlib 被当前 z42vm 的 strict-pin
    （`zbc_reader.rs` major/minor 精确匹配）拒 → **运行不了**。gen2 编的 = 永远当前格式 = 永远能跑。
    附带：只有 gen2 编的 stdlib 才能被 cross-bootstrap 逐字节验证。
  - **代价 = 2 次 stdlib 编译**（S3 解析 + S6 发布）。稳态（gen1==gen2 且 format 未 bump）时 S3==S6、
    S6 可跳过——留作 micro-opt，v1 老实编两次。

### Decision 6：gen3 条件触发（不固定第三代）

**问题**：gen2==gen3 这个真不动点要不要每轮都编 gen3？
**洞察**：`gen1 == gen2 ⟹ gen2 == gen3`（可证：gen1==gen2 则二者同一程序，gen3:=gen2(源)=gen1(源)=gen2）。
即**只要 gen1==gen2 成立，gen3 就是冗余的，不用真编**。而 `gen1 == gen2` 仅在"本周期没改 z42c
codegen"时成立——改了 codegen 时 gen1≠gen2（正常，非 bug），此时**只有 gen2==gen3 能当门**。
**决定**：gen3 **条件触发**，不固定跑——
- S5 比较 gen1 vs gen2（gen1/gen2 本就都要编，比较免费）：
  - **==** → 没改 codegen → gen2 自动是不动点 → **跳过 gen3**（稳态：绝大多数 nightly 走这条）。
  - **≠** → 改了 codegen → **编 gen3，断言 gen2==gen3**（专抓"自编自不稳定"= 非确定性 / 非语义保持的
    codegen 改动）。
- **不能无条件删 gen3**：改 codegen 那轮它是唯一不动点验证。也**不能把门改成 gen1==gen2**：那在改
  codegen 时会"假失败"。
- 代价：稳态省一代（仅编 gen1+gen2）；只在真改 codegen 时多编 gen3。

### Decision 7：cross-bootstrap 交叉验证（本地 SDK 当种子重跑 S2+S3+S5）

**问题**：怎么证"将发布的 SDK 真能当下一周期种子用"？
**决定**：把现有 `bootstrap-no-csharp` job **改造**（非删除）成 cross-bootstrap——种子来源从"下载上一版
nightly"换成"**本地刚编出、打包成发布形态的 SDK**"，重跑 S2+S3+S5：
- 用本地 SDK 的 z42c+stdlib 编 **xtask**（验"SDK 能编真实项目"，编成功即可，不字节比）。
- 用本地 SDK 编 **z42c** → 逐字节 == 本地 SDK 自带的 z42c（即 gen2==gen3 真不动点）。
- 用本地 SDK 编 **stdlib** → 逐字节 == 发布的 stdlib（确定性；依赖 stdlib=gen2 编，Decision 5）。
- = 提前演一遍"下一周期 bootstrap"，证发布的 SDK 自洽可用。
- **独立 job**（成本 ≈ 二次完整构建），进 `publish-nightly` 的 needs → 不自洽就不发布。
- 用**打包/发布形态**（apphost z42c + `.z42` 布局），验的才是真要发出去的东西。

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
