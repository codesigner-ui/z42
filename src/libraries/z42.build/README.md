# z42.build

## 职责
z42 项目「编译 → 发布」管线的**框架库**（全 z42 实现）。提供：固定的相位流程引擎、
传给各相位的 `IPipelineContext` 契约、以及 workload 与项目 `build/` 脚本继承扩展的
基类（`WorkloadBase` / `BuildHooks`）。

**不做**：平台相关实现 —— 那住在各 workload 的 `*.workload.zpkg`
（见 `src/toolchain/workload/`），通过 `: WorkloadBase` 子类提供。

> ⚠️ **Parked / 接口先行（2026-06-17）**：本目录**只定义接口与流程**（`IPipelineContext` /
> `IConfigTable` 契约 + `Pipeline.Run` 相位编排 + 继承扩展点），**不含具体实现逻辑**。
> 故意不建 `z42.build.z42.toml` 清单，**暂不接入编译**（未登记 workspace / xtask / CI）。
> 具体的 `IPipelineContext` 实现、原生 builtin（sign/archive/hash/probeVersion/download）、
> z42c 集成均留待深加工后按 spec-first 正式落地。

## 设计要点（三层继承链）
```
项目 build/        ──►  workload 平台实现       ──►  z42.build 基类
class iOSBuild         class iOSWorkload            class WorkloadBase
  : iOSWorkload          : WorkloadBase               (no-op 默认)
  override + base.X()     override 平台逻辑            扩展点契约
```
- 继承 / 重载 / hook 全归一到 `override` + `base.M(ctx)`（z42 原生 OOP，**不需反射**）。
- 绑定不靠运行时动态加载：publish 时生成一次性 driver 程序，把
  `z42.build` + workload + 项目 `build/` 静态链接编译后运行（类似 `build.rs`）。

## 核心文件
| 文件 | 职责 |
|------|------|
| `src/Pipeline.z42` | 管线驱动 —— **流程**：八相位顺序，head（z42.build 拥有）+ tail（workload 拥有） |
| `src/WorkloadBase.z42` | 平台尾相位扩展点（Preflight/Configure/GenerateProject/NativeBuild/Package） |
| `src/BuildHooks.z42` | 平台无关头相位 hook 扩展点（Before/After × Compile/Trim/Assets） |
| `src/IPipelineContext.z42` | **相位上下文契约**：只读数据 + 能力受限 fs + exec + 日志 + 产物登记 + 平台原语 + preflight 原语 |
| `src/IConfigTable.z42` | `[platform.*]` TOML 值只读访问契约（无泛型 map → typed getter） |
| `src/Models.z42` | 数据记录（Project/Target/Dirs/Inputs/Output/ExecResult） |
| `src/BuildKinds.z42` | 常量：TargetFamily / BuildMode / Phase（用 const，避开 enum） |

## 入口点
- `Pipeline.Run(ctx)` —— 由 publish 时生成的 driver 程序调用
- `WorkloadBase` / `BuildHooks` —— workload 与项目的继承扩展点
- `IPipelineContext` —— 相位与外界交互的唯一契约

## 依赖关系
- 依赖 `Std.IO`（数据/进程语义参考）、`Std.Collections`（List）
- **待补（实现期）**：`IPipelineContext` 中 Sign / Archive / Hash / ProbeVersion / Download
  对应的原生 builtin（toolchain 侧 Rust 实现）
