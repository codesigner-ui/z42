# toolchain/workload — 平台相关能力束（按需下载）

## 职责

承载**一切平台相关**的应用工程能力：把 runtime 产的平台无关 `app.zpkg` + 原始库，包装成各平台可发布/可导出的工程与产物。按 dotnet workload 模型，**按需 `z42 workload install <plat>`** 下载。

立柱（见 [platform-export-lifecycle.md](../../../docs/design/toolchain/platform-export-lifecycle.md)）：**`z42 build` 一次产平台无关 `app.zpkg`，零 workload；`export`/`publish`/on-platform `test` 才分叉并门控对应平台 workload。**

与 `runtime/` 的区别：runtime = 平台无关核心（VM + Tier1 C ABI + 头 + per-RID 原始库）；本模块 = 平台相关的"SDK 化"（Tier2 人因层 + facade + 模板 + native glue + 导出/编译）。
与 `launcher/`（SDK）的区别：launcher = `z42` CLI core（install/build/run...），引导关键、baked-in；本模块 = 平台命令（publish/export/工程生成），目录发现、按需装。

不做：VM 执行引擎（归 `runtime/`）；CLI core（归 `launcher/`）；SDK installer / 应用打包基础设施（归 `packager/`，另议）。

## 计划模块（consolidate-platform-into-workload 目标结构，迁移中）

| 模块 | 职责 | 来源 |
|------|------|------|
| `host-api/` | Tier2 人因 Rust（Host/Module/Entry 封装 Tier1 C ABI）| ← `host/embed` |
| `facades/` | 各平台惯用封装库（Swift / Kotlin / JS）| ← `host/platforms/*` |
| `templates/` | 各平台工程脚手架（`z42 export` 骨架）| ← `host/platforms/*` |
| `apphost/` | desktop publish 产物（`z42 publish` 桌面）| ← `launcher/core/apphost.z42` |
| `conformance/` | R1–R7 嵌入契约测试 = workload 自身测试用例（dogfood）| ← `host/platforms/*/tests` |

> 四 workload：`desktop`（仅 publish/export，不含 runtime）/ `ios` / `android` / `wasm`（含 target runtime pack）。分发模型见 [runtime-workload-distribution.md](../../../docs/design/toolchain/runtime-workload-distribution.md)。

## 依赖关系

- 依赖 `runtime/`（原始库 + C ABI 头）；被 `launcher` 的目录发现注册为平台命令。
- 迁移路线见 `docs/spec/changes/consolidate-platform-into-workload/`。
