# launcher 命令分发架构

> ⚠️ **前瞻设计草案（未实施）**。为把 `z42` 从"固定几条内建命令的 muxer"演进成可扩展命令分发器（new/build/publish/test/workload/平台打包…）铺路。落地开 spec。

## 问题

`z42` 要分发越来越多命令：创建项目、编译、导出平台工程、打包发布、下载 workload/runtime…… 全 baked 进 launcher 会让它臃肿、与编译器/平台发布节奏强耦合、且每加一个平台就要重编 launcher。需要一套"目录发现 + 命令行注册"并存的机制。

## 调研结论（主流工具怎么做）

| 范式 | 代表 | 外部命令发现 | 父 help 带描述 | 版本作用域 | workload |
|------|------|------|:--:|:--:|:--:|
| PATH 约定 | git / cargo / kubectl | `tool-<cmd>` 丢 PATH | ❌ 只列名 | ❌ | ❌ |
| **manifest 驱动** | **dotnet** tools/workloads | 清单声明 | ✅ | ✅（SDK band）| ✅ 一等公民 |
| shim + toolchain | rustup | 代理 + override | — | ✅ | 近似 |

**决定性观察**：选纯 PATH 约定的（git/cargo/kubectl）全栽在"父级 `-h` 给不出外部命令描述"——kubectl 不得不在外面套 krew 补元数据。z42 刚把 launcher 迁到 `Std.Cli` 就是为了每层 help 带描述，纯约定会退回这个问题。z42 又自定位"dotnet muxer + rustup"，且现成基础设施（版本作用域 `runtimes/<ver>`、zpkg 包、`runtimeconfig.json` sidecar、项目本地 `.z42`）与 dotnet 模型几乎 1:1 → **采 dotnet 的 manifest + 版本作用域 + workload，保留 cargo 裸约定作 fallback**。

## 三层命令（按"谁提供 / 怎么注册"分）

| 层 | 例子 | 提供者 | 注册方式 | 为何 |
|----|------|--------|---------|------|
| **Core** | run / install / link / list / which / uninstall / info / apphost | launcher 自带 | **代码注册**（Std.Cli router，已有）| 引导关键——得先有 install/run 才能装 SDK |
| **SDK** | new / build / export / publish / test / fmt | 随 runtime/SDK 装 | **目录发现**（版本作用域）| 与编译器同版本、可独立发布 |
| **Workload** | 平台打包/工程生成（ios/android/wasm）、模板 | 按需 `z42 workload install` | **目录发现**（drop-in 即注册）| 平台按需、host 无关平台不强塞 |

判据：**Core 必须 baked**（鸡生蛋）；**SDK + workload 必须目录发现**（否则每加平台重编 launcher、无法按版本/平台隔离）。

## 两种注册机制 → 合并进同一棵 Std.Cli 树

```
launcher 启动：
  1. 代码注册 core            → router.Add("run", desc, runHandler)
  2. 扫命令目录 + 读 manifest  → 对每个发现的命令 router.Add("build", desc, spawnZpkg(build.zpkg))
  3. root.Resolve(argv)：
       命中 core handler → 直接执行
       命中发现的命令   → spawn `z42vm <cmd.zpkg> -- <剩余 argv>`（裸透传，同 `run`）
```

**目录发现产出的 router 注册项与代码注册项同形** → `z42 -h` 一棵树里同时列 core+SDK+workload（各带描述）、每层 `-h`、未知命令一致报错，全是 Std.Cli 现成的。外部命令自己再用 Std.Cli 解析其子命令/flag。这就是"目录 + 命令行注册"的统一点。

## 目录发现的设计

### 发现机制：sidecar manifest（首选）+ 裸约定（fallback）

每个命令带一个 sidecar `<cmd>.cmd.toml`：

```toml
[command]
name        = "build"
summary     = "compile a z42 project to a zpkg"
zpkg        = "z42c.driver.zpkg"   # 相对本目录
min-runtime = "0.3.0"
```

- launcher 扫目录读 manifest（廉价、不执行）→ 据此建带描述的 router 项。
- 无 manifest 时 fallback：裸 `z42-<cmd>.zpkg`（name-only，无描述）——保住第三方 drop-in 低门槛。

> 为何不学 dotnet 的单 `commands.toml` 索引：per-command sidecar 让"装/卸一个命令 = drop/删一对文件"，无需并发改一个共享索引文件（避免写竞争）。

### 目录布局（版本作用域 + 全局 + 项目本地），按优先级合并

```
<repo>/.z42/commands/                      项目本地（pin；复用 install-z42 的 .z42 隔离模式）   ← 最高
$Z42_HOME/runtimes/<ver>/commands/         SDK 命令（版本作用域）
$Z42_HOME/runtimes/<ver>/workloads/<wl>/commands/   workload 装入
$Z42_HOME/tools/                           全局用户工具（类 ~/.cargo/bin，版本无关）        ← 最低
```

- **版本作用域要紧**：`z42 publish ios` 解析到的是匹配当前 runtime 的 ios 打包器，不跨版本串味（`--runtime <ver>` 可覆盖）。
- 优先级：core > 项目本地 > 版本 SDK/workload > 全局 tools；同名 first-wins-by-precedence。
- ⚠️ **目录扫描必须显式排序**（[common-pitfalls.md §1](../../../.claude/rules/common-pitfalls.md)）：`Directory.Enumerate` 跨 OS 顺序不定，first-wins 注册前按稳定键 sort，否则 CI 跨平台炸。

## 分发与参数透传

- 外部命令 = 一个 zpkg，经 `z42vm <cmd.zpkg> -- <argv[1:]>` 跑（裸透传，含 `--` 尾参、退出码透传，同 `run`）。
- 命令自身链 `Std.Cli` 解析它的子命令/flag/positional → 它内部也享受每层 help。

## workload 安装

`z42 workload install ios` → 下载 workload 包（命令 zpkg + `.cmd.toml` + 模板 + native 资产如 xcframework）→ 解到 `runtimes/<ver>/workloads/ios/` → 下次 `z42 <cmd>` 自动发现。对标 `dotnet workload install`。卸载 = 删目录。

## Decisions

| # | 决定 | 理由 |
|---|------|------|
| 1 | core 只留运行时自管理，baked-in | 引导关键 + launcher 与编译器版本解耦 |
| 2 | SDK/workload 走 sidecar manifest 目录发现 + 裸约定 fallback | help 完整（manifest）+ drop-in 低门槛（约定）；避开纯约定的"无描述"病 |
| 3 | 全部合并进同一棵 Std.Cli router | 统一 help/分发/未知处理，复用既有库 |
| 4 | 版本作用域目录 + 项目本地 + 全局三层，显式排序 | 命令随 runtime 版本/平台隔离；deterministic |
| 5 | 平台支持 = 可安装 workload | 基础包小、平台按需、host 无关平台不强塞 |

## Deferred / 待 spec 细化

- `.cmd.toml` 完整 schema（arg 摘要、别名、min/max-runtime 区间）。
- workload 包格式（manifest + packs 布局、依赖/版本解析、签名）。
- 命令版本冲突/多版本共存策略；`z42 commands list` 自省。
- 与 `z42up`（roadmap 1.0 版本管理工具）的边界。
