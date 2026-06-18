# consolidate-platform-into-workload — design

> 本 change 是**代码结构整合**的设计 + 迁移路线。分发/生命周期模型不在此重设计，以
> [runtime-workload-distribution.md](../../../design/toolchain/runtime-workload-distribution.md) +
> [platform-export-lifecycle.md](../../../design/toolchain/platform-export-lifecycle.md) +
> [launcher-command-dispatch.md](../../../design/toolchain/launcher-command-dispatch.md) 为唯一真相源。

## Architecture：组件归属（目标）

```
┌──────────────────────────────────────────────────────────────┐
│ runtime/   平台无关核心（= 现有结构，简化优化）                  │
│   VM(interp/jit/aot) · src/host/(Tier1 C ABI) · include/(头)    │
│   → 产 per-RID 原始静态/动态库（被 workload 消费）               │
└───────────────┬──────────────────────────────────────────────┘
                │ 依赖（Rust path/version dep + C ABI 头）
┌───────────────▼──────────────────────────────────────────────┐
│ workload/  平台相关——全部独立到此，按需下载                     │
│   host-api/     Tier2 人因 Rust（← host/embed）                 │
│   facades/      Swift / Kotlin / JS 惯用封装库                  │
│   templates/    各平台工程脚手架（export 骨架）                  │
│   apphost/      desktop publish 产物（← launcher/core/apphost）  │
│   conformance/  R1–R7 = workload 自身测试用例（dogfood）         │
│   {desktop,ios,android,wasm}                                    │
└──────────────────────────────────────────────────────────────┘
┌──────────────────────────────────────────────────────────────┐
│ launcher/  z42 CLI core（SDK 一部分；apphost.z42 迁出）          │
│   install / build / run / list / default / use / which / ...   │
└──────────────────────────────────────────────────────────────┘
host/   →  解散、顶层移除          packager/  →  不动（后议）
```

## Decisions

### Decision 1：runtime 留最小核，Tier2 也进 workload
**问题**：Tier2 `embed`（z42-host 人因 crate）放 runtime 还是 workload？
**决定**：进 **workload**（用户裁决）。runtime 只暴露 VM + Tier1 C ABI + 头 + 原始库；一切"SDK 化 / 导出 / 编译平台依赖供下载"的事归 workload。
**理由**：runtime 不因人因层膨胀；workload 是"导出/编译各平台依赖"的中心，Tier2 是其底座，co-locate 更自然（facade 的 rust crate 本就 path-dep 它）。

### Decision 2：apphost = desktop workload 的 publish 产物
**问题**：apphost 属 launcher Core 还是 workload？
**决定**：**desktop workload**（用户裁决；已修 launcher-command-dispatch.md）。
**理由**：apphost 是平台相关发布件，与 .ipa/.aab/wasm bundle 同层；保持"默认 build/run 零 workload、publish/export 才下载平台 workload"的对称。

### Decision 3：launcher 留 SDK
**决定**：`z42` CLI core（install/build/run/...）属 SDK，留 `src/toolchain/launcher/`；仅 `apphost.z42` 迁出到 desktop workload。
**理由**：launcher 是引导关键（鸡生蛋），不是平台相关件。

### Decision 4：host/ 顶层解散
**决定**：`host/embed`→workload/host-api；`host/platforms/*`→workload/{facades,templates,conformance}；`host/` 删除。
**理由**：host 的存在本身就是"平台相关与无关混居"的根源；拆净后该顶层无剩余职责。

### Decision 5：分两步看待"平台无关 vs 相关"
- **平台无关**（runtime）：build 一次产 `app.zpkg`（生命周期文档立柱不变量）。
- **平台相关**（workload）：export/publish/on-platform-test 才分叉，且 workload 门控。

## workload 门控模型（与既有设计一致，落到代码后果）

| 用户动作 | 需要 workload? | 走哪个组件 |
|---------|:--:|------|
| `z42 build` / `z42 run` / `z42 test`（host 面）| ❌ | SDK（launcher + runtime host VM）|
| `z42 publish`（desktop apphost）| ✅ desktop | desktop workload（apphost 模板 + 桌面 glue）|
| `z42 export ios` / `z42 publish ios` | ✅ ios | ios workload（facade + 模板 + target runtime pack）|
| android / wasm 同理 | ✅ 对应 | 对应 workload |

## 迁移路线（S1–S5，各自独立 change、独立锁、独立 GREEN）

| 步 | 内容 | 锁 | 关键风险 |
|---|------|----|------|
| **S0** | 本 change：设计落地 + 冲突修复（docs-only）| docs | — |
| **S1** | Tier2 `host/embed` → `workload/host-api`；简化 runtime host 结构 | runtime + toolchain | 3 个 facade crate 的 path-dep 改向 |
| **S2** | `launcher/core/apphost.z42` → desktop workload；建 workload 脚手架骨架 | toolchain | apphost 现由 `z42 apphost build` 驱动，命令面迁移 |
| **S3** | `host/platforms/*` facade + 模板 → `workload/{facades,templates}`；wasm demo 一并归位（撤销旧 P1 微清理，直接落终态）| toolchain | 9 个 xtask 路径字面量、embedding.md §package 布局、export-lifecycle line 90 骨架来源 |
| **S4** | R1–R7 测试改由 workload 生成/驱动，成 workload 自身测试用例；删 `host/platforms/*/tests` | toolchain（+runtime 验证）| CI 依赖脚手架（dogfood，接受；留极简 smoke 兜底）|
| **S5** | `host/` 顶层移除；embedding.md / platforms README / 分发文档收口到新结构 | docs | 全量链接自检 |

> 每步落地时同步更新对应 `docs/design/`（embedding.md package 布局、export-lifecycle 骨架来源、host README 等），按 workflow 阶段 9 文档同步。

## Implementation Notes

- **不动 packager**（用户暂缓）。
- runtime "简化优化"= 现有 `src/runtime/src/host/` + `include/` 保留，仅在 S1 顺带清理（不新增职责）。
- 各 facade rust crate 现 `z42-host = { path = "../../../embed" }` → S1 后改指 `workload/host-api`，再 S3 facade 整体进 workload 后变同 workspace 内 path（无跨组件 path-dep，更干净）。

## Testing Strategy

- S0：docs-only，无代码；GREEN = 文档链接自检 + 两处冲突修复一致性。
- S1–S5：每步独立 GREEN（`z42 xtask.zpkg test` 全套 + 受影响平台 `test platform <p>`）；S4 起 R1–R7 经 workload 跑通即 dogfood 验证。
