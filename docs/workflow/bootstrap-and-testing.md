# 自举与测试流程（本地 + CI）

> 权威的**操作层**流程文档：z42c 已自举、dotnet 已彻底移除（2026-06-26）后，工具链如何
> 从一个下载的 SDK 起步、编出当前版本、并被测试/打包/发布。设计原理（src/compiler 架构、
> 受限写法、对账策略）见 [`docs/design/compiler/self-hosting.md`](../design/compiler/self-hosting.md)；
> CI 总览见 [`ci.md`](ci.md)；测试层级见 [`testing/README.md`](testing/README.md)。

---

## 1. 核心模型：永远只有两套 toolchain

z42 工具链（z42c 编译器 + stdlib + xtask 构建 CLI）用 z42 写、编成 zpkg。要编它，先得有一个
能跑的 z42c —— 鸡生蛋。打破它的唯一办法是一个**已存在的种子**。于是任何时刻有且只有两套：

| | 来源 | 职责 | 位置（目标布局）|
|---|------|------|------|
| **SDK** | 下载的**上一版 nightly**（`install-z42`；launcher + z42c + z42vm + stdlib，**无 xtask**）| ① 打破 chicken-egg：编出 xtask(驱动) + current z42c(gen1) 这一中间体；之后 gen1→gen2、stdlib/toolchain 都用 **gen2** ② **保留用于交叉验证（不动点 + 差分）** | `.z42/`（仓库根）|
| **Current / 本地 SDK** | 当前源码编出：z42vm(cargo) + z42c gen2（SDK→gen1→gen2）+ stdlib/toolchain（gen2 编）| **被测试、被打包、成为下一个 nightly** | `artifacts/.z42/`（目标）|
| **xtask** | 项目构建工具（`scripts/`）：S2 由 SDK 编出驱动构建；发布形态由 cross-bootstrap 用本地 SDK 重编 | 构建驱动 + 交叉验证靶子，**不进 SDK / 不分发** | `artifacts/xtask/` |

> **为什么是 gen2 而非 gen1**：`gen1 = SDK 编当前 z42c 源`——gen1 的**行为**是当前源（改动立即生效），
> 但 gen1 **自身字节**由 SDK（旧 codegen）emit。若改的是 z42c **codegen**，gen1 行为新、字节旧。
> 用 `gen2 = gen1 再编自己`（gen2 由 gen1 的新 codegen emit）编一切，才能让 codegen 改动**同轮完全
> 生效**，否则要等下一次 nightly 才在字节里体现。SDK 影响面因此收缩到"仅产出 gen1 这个中间体"。

- **平台无关 vs 平台相关**：SDK / Current 里的 **zpkg（z42c/stdlib/xtask）是平台无关字节码**——编一次全平台共享。**z42vm 是原生二进制**——每个 host OS 各自 `cargo build`。
- **`--toolchain <dir>` 选择器**（目标）：xtask build/test 命令据此定位用哪套（`.z42` = SDK，`artifacts/.z42` = Current）。两套布局一致，切换只是换个目录值 → **交叉验证天然可做**。

### 交叉验证（SDK ↔ Current）

下载的 SDK 不在编完 Current 后丢弃，而是**留着对账**：

- **fixpoint（自举不动点）**：`SDK 编 current z42c = gen1`，`gen1 编自己 = gen2`，`gen2 编自己 = gen3`，断言 **gen2 == gen3 逐字节**（mod BLID）。Current 工具链用 **gen2** 编一切（codegen 改动同轮生效），gen2 自洽 → 才配做下一轮 SDK。
- 不动点验在 **gen2 == gen3**，**不是** `gen1 == SDK`、也不是 `gen1 == gen2`：gen1 行为新但字节由旧 SDK codegen emit；gen2 才是首个"行为+字节都 current"的代，gen2==gen3 证明它编自己稳定。当前源若引入了 SDK 没有的新语法，gen1 合法地与 SDK 不同。

---

## 2. 共享 host SDK：编一次，全 job 复用

**所有 job 都先需要 host 平台的 SDK**——连移动/wasm 打包也要 host 上的 z42c 来驱动交叉编译。
所以 host SDK（Current toolchain 的 zpkg 部分）**编一次、上传 artifact、全下游复用**，不重复编。

```
            ┌─ host-test (per OS)      下载 host-SDK + cargo z42vm + 跑测试
host SDK ───┼─ host-package (per OS)   下载 host-SDK + cargo z42vm + 打包
（编一次）  ├─ jit-test (linux, 分片)   下载 host-SDK + cargo z42vm + jit
            ├─ package-{ios,android,wasm}  下载 host-SDK + cross-target 编译
            └─ test-{ios,android,wasm,desktop}  下载 host-SDK + 平台测试
```

> **现状（2026-06-27）**：9 个 package/platform job 已消费 host-SDK artifact（`xtask-bootstrap-artifact`
> action）✓。但**测试 job（build-and-test / vm-jit / stdlib-jit / compiler-z42-stdlib）仍各自全量
> 重新自举**（`ci-bootstrap-nocs.sh`，~12min/job）——这是当前最大的冗余（见 §5）。

---

## 3. 本地流程

```
# fresh checkout（无 warm 产物）：
bash scripts/install-z42.sh          # 下载 SDK → .z42/
# （后续：SDK 编 xtask → xtask 编 current → 测试；目标由单一入口驱动）

# warm（已有 artifacts/.z42 的 Current）：
z42 xtask.zpkg test                  # GREEN gate（用 Current）
z42 xtask.zpkg test vm jit           # 单独 jit
z42 xtask.zpkg build sdk             # 重建 Current toolchain
```

- **无 C# 兜底**：删 src/compiler 后，fresh checkout **必须**先下载 SDK 才能起步（需 gh auth + 网络）。
  无 warm 种子时 `xtask build` 明确报错引导跑 `install-z42` / 下载流程。
- **GREEN gate**：`z42 xtask.zpkg test` = cargo z42vm + 用 Current 跑 vm(interp)/cross-zpkg/stdlib/
  compiler-z42。jit 由 `test vm jit` / CI 的 jit 专腿覆盖。详见 [`.claude/rules/workflow.md` 阶段8]。

---

## 4. CI 流程

### 4.1 目标拓扑（compile-once）

> SDK = launcher + z42c + z42vm + stdlib（**无 xtask**）；xtask 是项目构建工具（`scripts/`），
> 从源码编、不进 SDK。代际：gen1 = SDK 编 z42c 源（行为新/字节旧 codegen）；gen2 = gen1 再编自己
> （字节也新）。**发布 gen2**。

```
compile (linux, 一次)
  S0  cargo z42vm(release+debug)                          (Rust 原生)
  S1  下载 SDK → .z42/                                     (打破 chicken-egg)
  S2  SDK z42c + SDK stdlib → 编 xtask → artifacts/xtask/   (构建驱动；xtask 不进 SDK)
  S3  (xtask 驱动) SDK z42c → 编 z42c 源 → gen1            (SDK stdlib 仅作解析 libs；SDK 最后用处)
  S4  gen1 → 再编 z42c 源 → gen2  ★发布件
      条件不动点门：比较 gen1 vs gen2
        == → 没改 codegen，gen2 自动是不动点 → 跳过 gen3
        ≠ → 改了 codegen → 编 gen3，断言 gen2==gen3（逐字节 mod BLID）
  S5  gen2 → 编 stdlib + toolchain(src/toolchain) → artifacts/.z42   (用 gen2，非 SDK)
      → 本地 SDK = z42c(gen2) + stdlib(gen2编) + toolchain(gen2编)
  S6  xtask --toolchain artifacts/.z42 regen goldens + 编 test-units → *.zbc（供下游 --no-build）
  → 上传 artifact "current-sdk"（artifacts/.z42 + goldens + units）；S4 不动点不过 → 不上传（gate 发布）
     │
     ├─ test-interp (per OS)   下载 + cargo z42vm + --toolchain artifacts/.z42 test --no-build (interp)
     ├─ test-jit (linux, 4-shard)  同上 + jit + --shard k/4
     ├─ host-package (per OS)  下载 + cargo z42vm + package
     ├─ package-{ios,android,wasm} / test-{platform}  下载 + cross/平台
     └─ cross-bootstrap（交叉验证，独立 job = 改造 bootstrap-no-csharp）
          用"打包发布形态的本地 SDK"当种子重跑 S2+S3+S5：编 xtask + z42c + stdlib
          验：z42c 逐字节==本地SDK自带(gen2==gen3) / stdlib 逐字节==发布 / xtask 编成功
          = 提前演下一周期 bootstrap，证发布 SDK 自洽可用
  publish-nightly  needs ← package-* + cross-bootstrap（本地 SDK 成为下一个 nightly）
```

### 4.2 严密性不变量（CI 必须保证）

1. **SDK 只在 compile job 用**（S1-S3 产 gen1 + 保留供差分）；下游一律本地 SDK(gen2)。
2. **测试跑的就是将发布的本地 SDK**（同一 artifact）——测的 == 发的。
3. **发布的 nightly 必须过不动点 + cross-bootstrap**：S4 不动点 gate 上传；cross-bootstrap 证"本地 SDK
   能当下一轮种子"，进 publish-nightly needs。保证下一轮 SDK 自洽可重建。
4. **xtask 不进 SDK**：S2 由 SDK 编出驱动构建；其发布形态由 cross-bootstrap 用本地 SDK 重编落 `artifacts/xtask/`。

---

## 5. 边界与已知风险

### 5.1 chicken-egg（已解）
SDK（下载）打破。compile job 的步 1-2 是**不可消除的 shell 层**（cargo z42vm + 下载 + SDK 编 xtask）——
此刻 xtask 还不存在，无法"用 xtask 编 xtask"。步 3 起全用 xtask。

### 5.2 语法/能力 bump（已有纪律）
新语法分两阶段：**support 先行**（z42c 加能力但源码不用）→ 发一版 nightly → **晚一个 release 再 use**。
保证上一版 nightly 的 z42c 永远能编当前源。CI 的 bootstrap 本身强制（SDK 编不过当前源就红）。

### 5.3 🔴 zbc/zpkg format bump 死锁（删 C# 后的开口，**待修**）
`zbc_reader.rs` **精确匹配 major+minor**（拒读 older + newer minor）。一旦某 commit bump 格式：
新 z42vm 读不了旧 SDK 的 zpkg → bootstrap 全断 → publish-nightly 发不出新格式 nightly → **死锁**，
且无 C# 逃生口。compile-once 把 bootstrap 收成单点后，此风险**更集中**。

**修法（待拍板）**：
- **A. committed seed**：`.z42/` 下载不兼容时回退**仓库内提交的当前格式种子**。最严密、零外部依赖，
  代价 ~3MB 二进制进 git（仅能力 bump 时刷新）。
- **B. staged dual-format reader**：format bump 时 reader 临时读 `[旧,新]` 两格式（support 先行）。
  无 git 二进制，代价是每次 format bump 写临时双格式读取代码。
- **C. 接受死锁 + 手工恢复**：最省事最脆。

### 5.4 不动点未 gate 发布（待修）
现状不动点只在 `bootstrap-no-csharp` job 验，而 publish-nightly 的 needs **不含它**。理论上能发出没验过
自洽性的 nightly。compile-once 把不动点门移进 compile job（S4，条件触发 gen3 见 design.md Decision 6）+
把 bootstrap-no-csharp 改造成 cross-bootstrap（种子换本地 SDK）进 publish needs → 自然 gate 一切（§4.2.3）。

---

## 6. 当前冗余清单（系统改进目标）

| # | 冗余 | 现状 | 目标 |
|---|------|------|------|
| 1 | **16× 重新全量自举** | 测试 job 各自 `ci-bootstrap-nocs`（~12min）| 全消费单一 current-sdk artifact |
| 2 | **build-wave 二次重建** | test all 的 `_regenCore` 又编 z42c+stdlib+goldens（~17min）| compile-once + `--no-build` 消除 |
| 3 | test all 内 z42c 编 3 次 | ✅ 已修（89db08a0：cross-zpkg/compiler-z42 stage 加 noBuild）| — |
| 4 | 两个 bootstrap 脚本 | `ci-bootstrap-nocs.sh` + `bootstrap-no-csharp.sh` 并存 | ci-bootstrap-nocs 内联进 compile job；bootstrap-no-csharp **改造**为 cross-bootstrap |
| 5 | `bootstrap-no-csharp` job | 唯一做不动点，但 needs 不 gate 发布 | 不动点门折进 compile job S4；此 job **改造**成 cross-bootstrap（种子换本地 SDK），进 publish needs |
| 6 | `compiler-z42-stdlib` job | z42c 编全 stdlib + 功能验证 | 覆盖已在 compile job + test 阶段，评估删 |
| 7 | scripts/ 多个 shell CLI | 5 个 .sh | 逻辑内联 CI；仅留 `install-z42.sh` |

### 迭代（每步独立 commit + CI 验证；详见 spec tasks.md）
- **P1** xtask `--toolchain <dir>` + `build sdk`（地基，不依赖任何裁决）
- **P2** compile job（内联 S0-S6 + 条件不动点门 + goldens/units 上传 current-sdk）；format 兜底**第一版不做**（Decision 2）
- **P3** 下游消费 artifact（test-interp / test-jit / host-package / package-* / platform）
- **P4** cross-bootstrap：改造 bootstrap-no-csharp（种子换本地 SDK，重跑 S2+S3+S5）+ 进 publish needs
- **P5** 重命名（build-and-test→test-interp、vm-jit+stdlib-jit→test-jit）+ 评估删 compiler-z42-stdlib + 删脚本（仅留 install-z42.sh）

**预期**：关键路径 ~52min → ~25-30min；z42c/stdlib/xtask 编 16 次 → 1 次；SDK/Current 边界显式 +
fixpoint gate 发布（严密）。

---

> 维护：本文件随 P1–P5 落地逐步更新；现状段（§2 现状、§6）改完一项即勾掉。
