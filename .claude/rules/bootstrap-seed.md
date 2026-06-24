# 自举种子依赖：编译器 / xtask 的鸡蛋问题

> 触发条件：**改动任何「构建工具自己也要被构建」的链路**——尤其是 z42c（自举编译器）、
> xtask（z42 写的 dev CLI）、stdlib（被前两者依赖）三者的 build / seed / bootstrap 路径。
> 这条规则补齐 [workflow.md](workflow.md) 缺失的**自举维度**：删一个种子兜底前，必须先确认每个入口仍有种子。

---

## 鸡蛋问题（一句话）

**z42c / xtask / stdlib 互为前置：xtask 用 z42 写、要 z42c + stdlib 才能编；z42c 自身的构建又经
xtask / build 基础设施驱动；stdlib 又被两者依赖。任何「从源码全新构建」的入口，都必须先有一个
*已存在的种子*（seed = 一套能跑的 z42vm + z42c.driver.zpkg + stdlib dist），否则无路可走。**

种子有两种来源：

| 来源 | 何时用 | 例子 |
|------|--------|------|
| **warm 种子** | 本地已建过 / CI 有缓存 / 上游 nightly 已下载 | `artifacts/build/z42c/.../z42c.driver.zpkg` 存在 → z42c 自建 z42c（C#-free） |
| **cold 种子** | fresh checkout / CI 全新 runner，**没有任何 z42c 产物** | C# bootstrap 编译器现编一份 z42c（`_csharpBuildCompilerZ42Seed`），或下载 nightly 解包（`bootstrap-no-csharp` job） |

---

## 核心约定（必须遵守）

**删除任何「构建期种子 / 兜底路径」，必须与「为所有入口提供替代种子来源」作为同一个原子变更一起做——
绝不可拆成两个 commit、两个 change，或「先删兜底，回头再补种子」。**

判定「所有入口」时，**cold-start 入口最容易漏**：

- [ ] 本地 fresh checkout（删了 `artifacts/` 后第一次构建）
- [ ] CI 每条全新 runner 上 build stdlib / build z42c / package / golden regen 的 job
- [ ] download-bootstrap 类 gate（`vm-jit-consistency` / `stdlib-jit-consistency` / `compiler-z42-stdlib`）
- [ ] 打包矩阵（`package-{android,ios,wasm}` 等也会冷建 stdlib）

只要其中任一入口在删除后**没有种子来源**，该入口就会 `error: no <X> seed` 全红。

---

## 删种子前自检清单

改任何 `_buildCompilerZ42*` / `_buildStdlib*` / `bootstrap-*.sh` / CI 的 setup-dotnet / download-nightly 步骤前：

1. **列出此路径当前的种子来源**（warm？cold？两者？）
2. **若要删 cold 兜底**：先确认每个 cold 入口（上面清单）已切到「下载 nightly 种子」或「committed 种子」——
   **种子供给的 PR 必须先合并 / 同一 commit 落地，再删兜底**。
3. **本地不可验的部分（CI / packaging）**：本地只能验 warm 路径；cold 路径只能靠 CI。
   因此删 cold 兜底的 commit **push 后必须盯 CI**，红了立即回滚或补种子（见案例）。
4. **格式漂移风险**：下载的 nightly 种子，其 zbc / zpkg 格式必须能被当前 z42vm 读。
   format bump 后旧 nightly 失效 → 须等 `publish-nightly` 重新发布（self-healing 窗口期 CI 会短暂红）。
   删 cold 兜底**不要踩在 format bump 同一周期**。

---

## 现场案例（2026-06-24 fix f8a16812）

`d4471a85` 把 `_buildCompilerZ42` 从「dotnet 现编 z42c」改成「C#-free 自种子（缺种子即 `return 1`）」，
**但只改了函数、没动调用它的 `_buildStdlibCore` 冷启动分支**——该分支仍把它当「C# z42c 兜底」调用。
结果：CI fresh checkout 没有 z42c 种子 → `error: no z42c seed` → **所有冷建 stdlib 的 job 全红**
（build-and-test ×4 OS + package-{android,ios,wasm} + 3 个 download-bootstrap gate）。

根因正是违反核心约定：**cold 兜底被删，但 CI seed-provisioning（下载 nightly）还没落地**——两者是耦合原子步，
被拆开了。修复：恢复 `_csharpBuildCompilerZ42Seed`（cold 用 C# 现编 z42c），warm 路径保持 C#-free 不变。
彻底删 C# cold 兜底，要等 CI 全面切到下载 nightly 种子那一刻**同时**做。

---

## 与其他规则的关系

- **[philosophy.md](philosophy.md) 不为旧版本提供兼容**：种子的「format 漂移」是该规则的例外——nightly 种子是
  *跨进程的二进制接口*，删兜底要尊重发布周期，不能假设旧种子永远可读。
- **[workflow.md](workflow.md) 阶段 8 GREEN**：cold 路径本地不可验 → 该路径的「全绿」判定**以 CI 为准**，
  不是本地 warm 跑通就算数。
- **设计原理**（为什么自举需要种子、warm/cold 两态如何切换）落在 [`docs/design/compiler/self-hosting.md`](../../docs/design/compiler/self-hosting.md)，
  本文件只管「改动时如何避免踩坑」的流程约束。
