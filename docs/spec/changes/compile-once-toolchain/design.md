# Design: compile-once toolchain

> 操作流程全貌见 [`docs/workflow/testing/bootstrap.md`](../../../workflow/testing/bootstrap.md)。
> 本文记关键架构决策。

## Architecture

### 命名与角色

| 名 | 是什么 | 位置 | 进 SDK 发布? |
|----|--------|------|------|
| **SDK set** | 下载的上一版 nightly：matched set `{z42c, z42vm, stdlib}`,同一旧格式（launcher 同捆；**无 xtask**）| `.z42/` | — |
| **本地 SDK set** | 当前源码编出的 matched set `{z42c, z42vm, stdlib}`,同一当前格式 | `artifacts/.z42/` | ✅（成为下一个 nightly）|
| **xtask** | 项目构建工具（`scripts/xtask*.z42`）；既是构建驱动、也是交叉验证靶子 | `artifacts/xtask/` | ❌ 永不进 SDK / 不分发 |

**版本成对匹配（底层硬约束）**：strict-pin（`zbc_reader.rs` major/minor 精确匹配）→ **z42c(emit 格式) + z42vm(read 格式) + stdlib(身处格式) 必须同格式 = 一个 matched set**。bootstrap 本质 = 从 `SDK set` 过渡到 `本地 SDK set`。
- **z42vm 角色**：cargo 建的原生件,不在 z42c 编译链,但格式必须配它要跑的 zpkg。跑 SDK z42c(旧格式)用旧格式 z42vm,跑 gen2(当前格式)用当前 z42vm。**不 bump format 那些轮:一个 z42vm 通吃;bump 轮:过渡期要两个**（旧 vm 跑 SDK 段、当前 vm 跑 gen2 段——format-bump 复杂度,Decision 2 已延后）。

**成对分代**：每一代是 `{z42c, stdlib}` 一**对**（二者耦合:z42c 编 stdlib、stdlib 供 z42c 解析）：
- `gen1` = SDK 编 `{stdlib, z42c}`（行为新、字节由 SDK codegen 出）。
- `gen2` = gen1 编 `{stdlib, z42c}`（字节也新 = 当前 codegen+格式）。**发布的是 gen2 这一对**。
- `gen3` = gen2 编 `{stdlib, z42c}`（仅不动点检查,条件触发）。

### 主构建（linux，一次；zpkg 平台无关）

```
S0  cargo z42vm(release+debug)                          (当前格式 VM;成为本地 set 的 vm)
S1  install-z42 → 下载 SDK set → .z42/                   (打破 chicken-egg;旧格式 z42c+vm+stdlib)
S2  SDK z42c → 编 xtask → artifacts/xtask/               (构建驱动;xtask 不进 SDK)
S3  gen1 = SDK 编 {stdlib_g1, z42c_g1}                   (stdlib 先编→z42c 用它解析;SDK codegen)
S4  gen2 = gen1 编 {stdlib_g2, z42c_g2} → artifacts/.z42/  ★发布对(当前 codegen+格式)
    ┌ 条件不动点门（Decision 6）：比较 gen1 vs gen2
    │   == → 没改 codegen → gen2 自动是不动点 → 跳过 gen3
    └   ≠ → 改了 codegen → gen3 = gen2 编 {stdlib,z42c}，断言 {gen2}=={gen3}（逐字节 mod BLID）
    + gen2 编 toolchain(src/toolchain) → 本地 SDK set = {z42c, stdlib, toolchain}(gen2) + z42vm(S0)
S5  xtask --toolchain artifacts/.z42 regen goldens + 编 test-units → *.zbc（供下游 --no-build）
→ 上传 artifact "current-sdk" = artifacts/.z42 + goldens.zbc + units.zbc
      [gate①完整: gen2 编出来无报错(self-hosting closure)]
      [gate②稳定: {gen2}=={gen3}(gen1==gen2 时自动成立,跳 gen3) → 才上传]
```

### 下游（消费 artifact）+ 三关发布门

发布前必须过**三个正交属性**（见 Decision 8）——**完整 / 稳定 / 正确**,缺一不可：

```
current-sdk ─┬─ test-interp (per OS)   下载 + cargo z42vm + --toolchain artifacts/.z42 test --no-build (interp)
             ├─ test-jit (linux,4-shard)  同上 + jit + --shard k/4   ┐
             ├─ (stdlib [Test] / cross-zpkg 行为覆盖)               ├─ gate③正确:gen2 工具链跑测试套件全绿
             ├─ host-package (per OS) / package-{ios,android,wasm} / test-{platform}
             └─ cross-bootstrap（交叉验证,独立 job;Decision 7）
                  用"打包发布形态的本地 SDK set"当种子,重跑 S2-S4:编 xtask + {stdlib,z42c}
                  验:{z42c,stdlib} 逐字节==本地 set 自带({gen2}=={gen3}) / xtask 编成功
                  = 提前演"下一周期 bootstrap",证发布的 set 能当下轮种子(完整性二次确认)
publish-nightly  needs ← package-* + cross-bootstrap + test-interp + test-jit (+ stdlib/cross-zpkg)
                  ← 三关全过才发:完整(gen2/cross-bootstrap编出来) + 稳定(不动点) + 正确(测试套件)
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

### Decision 4：不动点进 compile job → gate 上传（"稳定"关）

**问题**：不动点现在只在 `bootstrap-no-csharp` 验，不 gate 发布。
**决定**：把不动点门移进 compile job S4（gen1 vs gen2 比较 + 条件 gen3），作为**上传 artifact 的前置 gate**
（= 三关之"稳定",见 Decision 8）。不过 → 不上传 → 所有下游拿不到 artifact → 不发布。
（`bootstrap-no-csharp` job 不删而是**改造**成 cross-bootstrap，见 Decision 7。）

### Decision 5：成对分代 `{z42c, stdlib}`，发布 gen2 对（codegen + 格式同轮生效）

**问题**：用哪一代编 stdlib/z42c？stdlib 和 z42c 怎么排序？
**背景**：① z42c 和 stdlib **耦合**（z42c 编 stdlib、stdlib 供 z42c 解析），应作一**对**翻代;② `gen1` 行为
=当前源但**自身字节** SDK codegen 出,若改 codegen 则 gen1 字节旧,要 `gen2 = gen1 再编` 才完全当前;
③ 版本成对匹配:发布的 stdlib 必须**当前格式**才能被当前 z42vm 读（strict-pin）。
**决定**：每代是 `{z42c, stdlib}` 一对——
- **gen1 = SDK 编 `{stdlib_g1, z42c_g1}`**（S3）：stdlib 先编 → z42c 用它解析。为什么不用下载 SDK 自带的
  stdlib 解析:那是上一版,若当前 z42c 用了本周期新加的 stdlib API 就解析不到 → 先编一份当前的最稳（=
  现有 bootstrap 的 "stdlib 先编" 顺序）。
- **gen2 = gen1 编 `{stdlib_g2, z42c_g2}`**（S4）：**这一对就是发布件**,全当前 codegen + 当前格式。
- **为什么发布的 stdlib 必须 gen2 编（不能发 gen1 那份）**：gen1 的 stdlib 是 SDK codegen/格式。**不 bump
  format 的轮其实能跑**;但 **format bump 那轮**,旧格式 stdlib 被当前 z42vm strict-pin 拒 → **运行不了**。
  gen2 那份 = 当前格式 = 永远能跑;且只有 gen2 那份能被 cross-bootstrap 逐字节验。
- **xtask 例外**：不进 SDK,是构建驱动——S2 由 SDK 先编出来驱动全程;其 gen2 形态在 cross-bootstrap 由本地
  SDK set 重编,落 `artifacts/xtask/`。
- **toolchain（src/toolchain）**：由 gen2 编（随 S4）。
- **代价 = 2 对 build**（gen1 + gen2），即 stdlib 编 2 次。稳态（gen1==gen2 且 format 未 bump）gen1 那对
  == gen2 那对、可跳过 gen2 重编——micro-opt,v1 老实编两对。

### Decision 6：gen3 条件触发（不固定第三代）

**问题**：`{gen2}=={gen3}` 这个真不动点要不要每轮都编 gen3？
**洞察**：`gen1 == gen2 ⟹ gen2 == gen3`（可证：gen1==gen2 则二者同一程序对，gen3:=gen2(源)=gen1(源)=gen2）。
即**只要 gen1==gen2 成立，gen3 冗余**。而 `gen1 == gen2` 仅在"本周期没改 z42c codegen"时成立——改了
codegen 时 gen1≠gen2（正常,非 bug）,此时**只有 {gen2}=={gen3} 能当门**。
**决定**：gen3 **条件触发**，不固定跑——
- S4 比较 gen1 vs gen2 对（gen1/gen2 本就都要编，比较免费）：
  - **==** → 没改 codegen → gen2 自动是不动点 → **跳过 gen3**（稳态:绝大多数 nightly 走这条）。
  - **≠** → 改了 codegen → **编 gen3 对，断言 {gen2}=={gen3}**（专抓"自编自不稳定"= 非确定性 / 非语义
    保持的 codegen 改动）。
- **不能无条件删 gen3**：改 codegen 那轮它是唯一不动点验证。也**不能把门改成 gen1==gen2**：那在改
  codegen 时会"假失败"。

### Decision 7：cross-bootstrap 交叉验证（本地 SDK set 当种子重跑 S2-S4）

**问题**：怎么证"将发布的 SDK set 真能当下一周期种子用"（= "完整"关二次确认）？
**决定**：把现有 `bootstrap-no-csharp` job **改造**（非删除）成 cross-bootstrap——种子来源从"下载上一版
nightly"换成"**本地刚编出、打包成发布形态的 SDK set**"，重跑 S2-S4：
- 用本地 SDK set 编 **xtask**（验"set 能编真实项目"，编成功即可）。
- 用本地 SDK set 编 `{z42c, stdlib}` → 逐字节 == 本地 set 自带（即 {gen2}=={gen3} 真不动点 + stdlib 确定性）。
- = 提前演"下一周期 bootstrap",证发布的 set 自洽可用。
- **独立 job**（成本 ≈ 二次完整构建）,进 `publish-nightly` needs。
- 用**打包/发布形态**（apphost z42c + `.z42` 布局）,验的才是真要发出去的东西。

### Decision 8：发布门 = 三个正交属性（完整 / 稳定 / 正确）

**问题**：`{gen2}=={gen3}` 只证"编译稳定"（自编自得到一样字节），**不证编出来的行为正确**——"稳定地错"
也能通过（gen2、gen3 同一 bug → 字节一样 → 假绿）。
**决定**：发布前必须过**三个互相独立**的属性，缺一不可：

| 属性 | 验什么 | 由谁验 | gate 点 |
|------|--------|--------|---------|
| **完整** | 当前工具链能从源码编出完整 `{z42c, stdlib}` 不报错（self-hosting closure）| gen2 编出来（S4）+ cross-bootstrap 重建 | 上传 + publish needs |
| **稳定** | 编译器自编自得到**一样字节** | `{gen2}=={gen3}`（Decision 6 条件触发）| 上传 gate（S4）|
| **正确** | 编出来的程序**行为对** | gen2 工具链跑 **vm goldens + stdlib [Test] + cross-zpkg**（下游 test 腿）| **publish-nightly needs** |

**关键修正**：之前 `publish-nightly needs` 只含 package-* + cross-bootstrap（= 完整+稳定），**漏了"正确"**
——测试腿没进 needs → 测试挂了只要打包过仍可能发布。修：**test-interp / test-jit（+ stdlib/cross-zpkg
行为覆盖）进 publish-nightly needs**。三腿本就跑 gen2 工具链（= 发布件）,直接进 needs 最省。

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
- **回归**：现有 GREEN gate（vm/cross-zpkg/stdlib/compiler）覆盖不减——只是改为消费预编译 Current。
- **本地**：`xtask --toolchain .z42 build sdk` + `--toolchain artifacts/.z42 test` 可复现 CI 链路。
