# 构建编排器 `z42b`

> ⚠️ **前瞻设计草案（未实施）**。`z42b` 驱动 [`z42.build`](../../../src/libraries/z42.build/) 管线，
> 编排「编译 → 发布」全流程。当前为 PARKED 骨架（`src/toolchain/builder/` + `z42.build`，
> 均无 toml、未接编译）。落地开 `docs/spec/changes/<name>/` spec，按 workflow 实施。
> **前置**：replace-csharp S5 完成（z42c 成生产编译器、`toolchain` 解锁）。

## 立柱与定位

`z42b` 是**构建编排器**：读 `z42.toml` / `--rid` → 构造并驱动 `z42.build` 八相位管线 → 产出
平台无关 `app.zpkg`（build）或平台交付件（publish）或 IDE 工程（export）。它**只编排 + 注入**，
不自己编译、不含平台实现。

与既有工具的分工（沿用 launcher「源 → zpkg → apphost」模式）：

```
launcher (z42)  ──分发──►  z42b  (src/toolchain/builder/core → z42.builder.zpkg → apphost z42b)
                             │  读 toml/--rid → 注入 Compiler/Workload/Hooks → Pipeline.Run
                             ▼
        z42.build 管线   head（z42.build 拥有）            tail（workload 拥有）
        Resolve → Compile → Trim → Assets        Configure → GenerateProject → NativeBuild → Package
                     │  经 ICompiler in-process 调编译器库          │  虚分发沿 项目→workload→基类
                     ▼                                              ▼
             z42c（: ICompiler，不 fork 子进程）        iOSWorkload / DesktopWorkload / ...
```

- **z42c = 编译器**（源 → app.zpkg），是 Compile 相位经 `ICompiler` **在进程内**调的库。
- **z42b = 编排器**（toml → 全流程，含 trim/assets/workload/打包）。
- 命令动词（build/run/publish/export/test、`--rid` 选平台）裁决见 [platform-export-lifecycle.md](platform-export-lifecycle.md)，本文不复述；本文聚焦 **z42b 内部如何编排**。

## In-process 编译：`ICompiler` 共享实现（核心设计）

Compile 头相位**不 fork `z42c` 子进程**，而是经 `z42.build` 的 `ICompiler` 接口**在进程内**调
编译器库——与独立 `z42c.driver` CLI **引用同一份实现**。

```
ICompiler (z42.build 定义)
   ├─ z42c.driver CLI     →  构造 CompileRequest → Compile()    （独立编译命令）
   └─ z42b Pipeline.Compile →  ctx 构造 CompileRequest → Compile()（编排内调用）
```

- **好处**：类型化请求/结果（非解析 stdout）、零进程开销、不依赖 z42vm 在 PATH。
- **依赖倒置（DIP）**：`z42.build` 定接口；编译器库 `: ICompiler` 实现 → z42c → z42.build（无环）。
- **换实现只动一处**：`z42b._hostCompiler()` 返回 `ICompiler` 实现；从骨架 `NoCompiler`
  切到真实 z42c 实现时，调用方（`Pipeline.Compile`）一行不改。

> **计划重构（Deferred，见下）**：`ICompiler` + CompileRequest/CompileResult 现暂置 `z42.build`，
> 后续抽到**中立微库**，使编译器核心（z42c）只依赖该微库、不依赖整个 build 框架。

## 两条 driver 路径

| 路径 | 触发 | 机制 | 代价 |
|------|------|------|------|
| **标准** | 项目无自定义 `build/` | **进程内组合**：`new Pipeline()` 注入 `_hostCompiler()` + 标准 workload → `Pipeline.Run(ctx)`。**零子进程、零代码生成** | 无 |
| **自定义** | 项目有 `build/`（hook / workload 子类）| **生成一次性 driver** 源码（链 `z42.build` + workload + 项目 `build/`）→ 用**同一 `ICompiler`** 编译 → 跑（其 Main 注入项目 Hooks/Workload 后 `Pipeline.Run`）| 首次构建多一次 driver 编译（按输入 hash 缓存）|

in-process 编译让**标准路径无需生成 driver**——这是「固定通用 driver」与「每项目生成 driver」
的天然混合：标准项目走进程内快路径，只有项目要插自定义逻辑时才落 driver 生成。

## 自定义扩展：`build/` 发现约定

项目用一个 `build/` 目录（与 `src/` 平级）放可选的构建扩展 z42 源，**约定优于配置**
（类比 `build.rs`）：

```
myapp/
  z42.toml
  src/                       # 应用代码
  build/                     # ← 可选；构建扩展（编译进自定义 driver）
    ProjectHooks.z42         #   class ProjectHooks : BuildHooks   —— 平台无关编译前后 hook
    iOSBuild.z42             #   class iOSBuild : iOSWorkload      —— 平台尾相位 override
```

- **固定类名约定**（静态绑定、不需反射）：`ProjectHooks`（→ `Pipeline.Hooks`）、
  `<Family>Build`（如 `iOSBuild`/`DesktopBuild`，→ 覆盖该平台标准 workload）。
  生成的 driver 按这些名字 `new` 并注入；缺则用默认（空 Hooks / 标准 workload）。
- **`build/` 不存在或为空** → 标准路径（进程内，无 driver 生成）。
- **编译前/编译后**自定义即 `ProjectHooks` override `BeforeCompile`/`AfterCompile`
  （及 Before/After × Trim/Assets）；平台专属定制走 `<Family>Build` override + `base.X(ctx)`。

> 相位**封闭**（八个，线性，不可增删改序）；所有自定义落在 Hooks / Workload override 上，
> 不开放注册新相位（确定性 + 缓存模型）。

### 示例：编译前/后 hook + 平台 override

```z42
// build/ProjectHooks.z42 —— 平台无关的编译前/后扩展
using Z42.Build;

public class ProjectHooks : BuildHooks {
    // 编译前：代码生成，把构建元数据写成 z42 源纳入本次编译
    public override void BeforeCompile(IPipelineContext ctx) {
        string gen = ctx.Dirs.Intermediate + "/gen/BuildInfo.z42";
        ctx.WriteText(gen,
            "namespace App; public static class BuildInfo {"
          + " public const string Rid = \"" + ctx.Target.Rid + "\"; }");
        ctx.Log("generated " + gen);
    }
    // 编译后（资产收集后）：例如校验/盖戳产物
    public override void AfterAssets(IPipelineContext ctx) {
        ctx.Log("post-assets check");
    }
}
```

```z42
// build/iOSBuild.z42 —— iOS 平台尾相位定制（override + base.X）
using Z42.Build;
using Z42.Workload;

public class iOSBuild : iOSWorkload {
    public override void Package(IPipelineContext ctx) {
        base.Package(ctx);                      // 先跑标准 .ipa 打包
        ctx.Log("custom post-package step");    // 再叠加自定义（额外签名/校验/上传准备）
    }
}
```

z42b 发现 `build/` 后生成一次性 driver，其 Main 约等于：
`new Pipeline{ Compiler=hostCompiler, Hooks=new ProjectHooks(), Workload=new iOSBuild() }.Run(ctx)`。

## `IPipelineContext` 实现归属

`PipelineContext`（`IPipelineContext` 的 SDK 实现：受限 fs / exec / 平台原语 / 产物登记）
**暂置 `z42.build` 库**（2026-06-23 决策），使编排器 / 生成的 driver 都能 `import` 它构造 ctx。
随 `ICompiler` 微库抽取一并重新审视分层。

**待补的 native 原语**（`IPipelineContext` 中，经 `extern` 接 toolchain 侧 Rust builtin）：
`Sign` / `Archive` / `Hash` / `ProbeVersion` / `Download`。

## 命名

- 框架库：**`z42.build`**（公共扩展 API，workload / 用户 `build/` 继承；属 `z42.<domain>` 族，
  不改 `z42b.core`）。
- 编排器包：**`z42.builder`**（`src/toolchain/builder/core/` → `z42.builder.zpkg`，二进制 `z42b`；
  与 `z42.launcher` 同构）。

## Decisions

| # | 决定 | 理由 |
|---|------|------|
| 1 | Compile 经 `ICompiler` **in-process** 调编译器库，不 fork z42c | 类型化、零进程开销、不依赖 PATH；与 z42c.driver 共享实现 |
| 2 | z42b 与 z42c.driver 引用**同一 `ICompiler` 实现** | 单一编译入口；换实现不动调用方 |
| 3 | 两条 driver 路径（标准进程内 / 自定义生成 driver）| in-process 让标准项目零生成；仅自定义才付 driver 编译成本 |
| 4 | `build/` 固定类名约定（`ProjectHooks` / `<Family>Build`）| 静态绑定不需反射；约定优于配置 |
| 5 | 相位封闭，自定义只走 Hooks / Workload override | 确定性 + 缓存模型；不开放新相位 |
| 6 | `PipelineContext` 暂置 `z42.build`；`ICompiler` 后抽中立微库 | 减 churn；最终让编译器核心不依赖 build 框架 |
| 7 | 框架库 `z42.build` 不改名；编排器包 `z42.builder` | 框架是公共扩展 API（`z42.<domain>` 族）；包名同构 `z42.launcher` |

## Deferred / 待 spec 细化

### z42b-future-icompiler-microlib: `ICompiler` 抽中立微库

- **来源**：本设计 / 2026-06-23 用户决策。
- **触发原因**：`ICompiler` 暂置 `z42.build`，致编译器库（z42c）实现它时传递依赖整个 build 框架。
- **前置依赖**：z42b 标准路径落地（确认 `ICompiler` 调用面稳定）。
- **触发条件**：正式落地 z42b（spec）时，或 z42c 侧适配 `ICompiler` 前。
- **当前 workaround**：接口暂留 `z42.build`，DIP 保证无环；interim 可接受。

### 其他待 spec 细化

- `build` 动词的停点（仅 head 跑、产 app.zpkg、不跑 workload tail）：用「不注入 workload
  （`WorkloadBase` no-op 兜底）」约定，还是给 `BuildMode` 加 `Build`（当前仅 Export/Publish，
  `Pipeline.Run` 仅在 Export 停于 GenerateProject）—— 落地 spec 时定。
- driver 生成的源码模板形态 + 输入 hash 缓存键设计。
- `PipelineContext` 各 native 原语（Sign/Archive/Hash/ProbeVersion/Download）的 Rust builtin 契约。
- 各 workload 现有 `export.z42` / `apphost.z42` 真实逻辑接进 `WorkloadBase` 相位的迁移。
- `[build]` 段是否需声明 hook（当前纯 `build/` 约定）；`[platform.*]` 完整 schema 见
  [platform-export-lifecycle.md](platform-export-lifecycle.md) Deferred。
