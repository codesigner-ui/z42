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
| **SDK** | 下载的**上一版 nightly**（`install-z42`）| ① 打破 chicken-egg：编出 current xtask + current z42c ② **保留用于交叉验证** | `.z42/`（仓库根）|
| **Current** | 当前源码编出：z42vm(cargo) + xtask(SDK编) + z42c gen1(SDK编) + stdlib(gen1编) | **被测试、被打包、成为下一个 nightly** | `artifacts/.z42/`（目标）|

- **平台无关 vs 平台相关**：SDK / Current 里的 **zpkg（z42c/stdlib/xtask）是平台无关字节码**——编一次全平台共享。**z42vm 是原生二进制**——每个 host OS 各自 `cargo build`。
- **`--toolchain <dir>` 选择器**（目标）：xtask build/test 命令据此定位用哪套（`.z42` = SDK，`artifacts/.z42` = Current）。两套布局一致，切换只是换个目录值 → **交叉验证天然可做**。

### 交叉验证（SDK ↔ Current）

下载的 SDK 不在编完 Current 后丢弃，而是**留着对账**：

- **fixpoint（自举不动点）**：`SDK 编 current z42c = gen1`，`gen1 编 current z42c = gen2`，断言 **gen1 == gen2 逐字节**（mod BLID）。证明 Current z42c 能自洽地编自己 → 才配做下一轮 SDK。
- 真正的不动点是 `gen1 == gen2`，**不是** `gen1 == SDK`：当前源若引入了 SDK 还没有的能力（新语法），SDK 编出的 gen1 合法地与 SDK 不同。

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

```
compile (linux, 一次)
  1. cargo z42vm + 下载 SDK → .z42/
  2. .z42 的 z42c 编 xtask
  3. xtask --toolchain .z42 build sdk → artifacts/.z42   ← Current
  + fixpoint: --toolchain artifacts/.z42 重建 gen2，验 gen1==gen2（交叉验证，保留 .z42）
  + 编 goldens.zbc + test-units.zbc（供下游 --no-build）
  → 上传 artifacts/.z42（+goldens+units）
     │
     ├─ test-interp (per OS)   下载 + cargo z42vm + --toolchain artifacts/.z42 test --no-build (interp)
     ├─ test-jit (linux, 4-shard)  同上 + jit + --shard k/4
     ├─ host-package (per OS)  下载 + cargo z42vm + package
     ├─ package-{ios,android,wasm}  下载 + cross-target
     └─ test-{platform}        下载 + 平台测试
  publish-nightly  needs ← package-*（Current 成为下一个 nightly）
```

### 4.2 严密性不变量（CI 必须保证）

1. **SDK 只在 compile job 用**（步 1-2 + fixpoint 对账）；下游一律 Current。
2. **测试跑的就是将发布的 Current**（同一 artifact）——测的 == 发的。
3. **发布的 nightly 必须过 fixpoint**：compile job 的 fixpoint gate 通过，才上传 artifact、才允许下游 →
   才允许 publish-nightly。保证下一轮 SDK 自洽。
4. **feed publish-nightly 的链路** 自洽可重建（见 §5 format 边界）。

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

### 5.4 fixpoint 未 gate 发布（待修）
现状 fixpoint 只在 `bootstrap-no-csharp` job 验，而 publish-nightly 的 needs **不含它**。理论上能发出
没验过自洽性的 nightly。compile-once 把 fixpoint 移进 compile job 后，自然 gate 一切（不变量 §4.2.3）。

---

## 6. 当前冗余清单（系统改进目标）

| # | 冗余 | 现状 | 目标 |
|---|------|------|------|
| 1 | **16× 重新全量自举** | 测试 job 各自 `ci-bootstrap-nocs`（~12min）| 全消费单一 host-SDK artifact |
| 2 | **build-wave 二次重建** | test all 的 `_regenCore` 又编 z42c+stdlib+goldens（~17min）| compile-once + `--no-build` 消除 |
| 3 | test all 内 z42c 编 3 次 | ✅ 已修（89db08a0：cross-zpkg/compiler-z42 stage 加 noBuild）| — |
| 4 | 两个 bootstrap 脚本 | `ci-bootstrap-nocs.sh` + `bootstrap-no-csharp.sh` 并存 | 内联进 compile job，合一 |
| 5 | `bootstrap-no-csharp` job | 唯一做 fixpoint，但 needs 不 gate 发布 | fixpoint 折进 compile job，删此 job |
| 6 | `compiler-z42-stdlib` job | z42c 编全 stdlib + 功能验证 | 覆盖已在 compile job + test 阶段，评估删 |
| 7 | scripts/ 多个 shell CLI | 5 个 .sh | 逻辑内联 CI；仅留 `install-z42.sh` |

### 迭代（每步独立 commit + CI 验证）
- **P1** xtask `--toolchain <dir>` + `build sdk`（地基）
- **P2** compile job（内联步 1-3 + fixpoint + goldens/units）+ format 兜底（§5.3 拍板）
- **P3** 下游消费 artifact（test-interp / test-jit / package）
- **P4** 重命名（build-and-test→test-interp、vm-jit+stdlib-jit→test-jit）+ 删冗余 job（5、6）
- **P5** 删脚本（4、7）

**预期**：关键路径 ~52min → ~25-30min；z42c/stdlib/xtask 编 16 次 → 1 次；SDK/Current 边界显式 +
fixpoint gate 发布（严密）。

---

> 维护：本文件随 P1–P5 落地逐步更新；现状段（§2 现状、§6）改完一项即勾掉。
