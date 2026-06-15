# scripts

## 职责

仓库的开发 CLI 与启动引导。绝大多数开发命令（build / test / package / deps /
regen / audit / bench）都已收敛到一个自举的 z42 程序 **xtask**：源码是本目录的
`xtask*.z42`，编译成单个 `artifacts/xtask/xtask.zpkg`，经 launcher 调用：

```
z42c build scripts/xtask.z42.toml --release    # 构建 → artifacts/xtask/xtask.zpkg
z42 xtask.zpkg <command> [args]                # 经 launcher 运行
```

xtask 本身是一个 z42 程序，由 launcher **运行**——它不是通用 `z42` launcher 的一
部分（launcher 保持通用运行时）。冷启动如何先产出 `xtask.zpkg` 见下文「冷启动
bootstrap」。

本目录的 `.z42` 全部是 xtask 模块（含 stdlib 构建逻辑 `xtask_stdlib.z42`）。唯一
的非 xtask 文件是安装引导脚本：

- **`install-z42.{sh,bat,command}`** —— 下载预编译发行版并安装。运行在「还没有 z42 工具链」的最前端，故保持 shell。
  - 无参数：portable 安装到 `<repo>/.z42`（bootstrap，最常用）。
  - `--system`：managed 安装到 `$Z42_HOME`（默认 `~/.z42`），展开 `bin/launcher/runtimes` 布局，打印 PATH 接入提示。
  - `--dest <dir>`：安装到指定目录（与 `--system` 组合时用 managed 布局，否则用 portable）。

> **冷启动 bootstrap（鸡生蛋的真正破解点）**：`xtask.zpkg` 依赖 stdlib 才能编译，
> 所以冷树上先由 **C# 编译器**直接 `dotnet -- build --workspace`（不涉及任何 z42
> 程序）产出 primer stdlib（`.zpkg`/`.zsym`）→ 编译 `xtask.zpkg` → 再
> `z42 xtask.zpkg build stdlib`（即 `xtask_stdlib.z42`）做扁平视图。z42c 只读 `.zsym`、
> VM 扫目录（读各 zpkg 的 NSPC section 认领 namespace），都不需要任何 namespace 索引即可
> 编译/运行 xtask，所以这个次序无死锁。

> 所有版本号的唯一真相源是仓库根 `versions.toml`（xtask 经 `Std.Toml` 原生解析，
> 见共享模块 `xtask_versions.z42`）。

## 命令一览

| 命令 | 触发时机 | 关键依赖 | 主要产物 |
|------|---------|---------|---------|
| `z42 xtask.zpkg deps install` | **首次 clone / 平台版本变动** | `versions.toml` | rust targets + cargo-ndk + wasm-pack；按平台装 NDK / 构建 SDK |
| `z42 xtask.zpkg deps check` | 改 `versions.toml` 后对账 | `versions.toml` + 投影文件 | versions.toml ↔ Cargo.toml / build.gradle.kts / ios/Package.swift 一致性 |
| `z42 xtask.zpkg build stdlib` | 改了 stdlib `.z42` 源 | `dotnet` + `z42.Driver` | `artifacts/build/libraries/dist/release/<lib>.zpkg`（扁平视图，无 namespace 索引） |
| `z42 xtask.zpkg build package [release\|debug] [--rid R]` | 准备发行 / 测发行包 | `dotnet` + `cargo` | `artifacts/packages/z42-<version>-<rid>-<config>/{bin,libs,native}`（末尾自动跑 SHA-256 invariant） |
| `z42 xtask.zpkg regen` | 编译器变更使 `.zbc` 基线漂移 | `dotnet` + `z42.Driver` | run-golden → `artifacts/build/golden/<源相对路径>/source.zbc`（gitignored，不污染 src）；committed 字节基线 `src/tests/zbc-format/*/source.zbc` 就地重写 |
| `z42 xtask.zpkg audit` | 新增 test `source.z42` | `z42.regex` | 自动补缺失的 `using` 声明 |
| `z42 xtask.zpkg test` | **每次 commit / 归档前必跑** | 下面各 stage | 串联全部 GREEN 验证 |
| `z42 xtask.zpkg test vm [interp\|jit]` | 跑 VM 端到端 golden（最常用） | `cargo build` + regen 产物 | interp / jit 通过率 |
| `z42 xtask.zpkg test cross-zpkg` | 跨包路径 / 元数据相关变更 | `dotnet` + `cargo build` | target/ext/main 三方协作通过率 |
| `z42 xtask.zpkg test lib` | stdlib 源 / 编译器变动 | `build stdlib` + `z42-test-runner` | 各 stdlib lib 的 `[Test]` 通过率 |
| `z42 xtask.zpkg test dist` | 验证打包后发行版能独立工作 | `build package` 产物 | packaged z42c+z42vm 跑 golden 通过率 |
| `z42 xtask.zpkg test changed [base]` | 增量自测（按改动文件挑 stage） | 上述各命令（in-process 调度） | 仅跑受影响的 stage |

### `deps install` —— 两层依赖模型

| 命令 | 行为 |
|------|------|
| `z42 xtask.zpkg deps install` | 跨平台 toolchain 存在性检查（rust / dotnet / node） |
| `z42 xtask.zpkg deps install --os <android\|ios\|wasm>` | **TIER 1**：该平台的「必要构建依赖」（android = rust targets + cargo-ndk + JDK + 构建 SDK；ios = rust targets + Xcode；wasm = rust targets + wasm-pack） |
| `z42 xtask.zpkg deps install node` | **TIER 2**（步骤级，惰性）：Node LTS（wasm js / playground 测试用） |
| `z42 xtask.zpkg deps install android-emulator` | **TIER 2**：emulator + system-image + AVD + Gradle（android 仪器测试用） |
| `z42 xtask.zpkg deps install --check` | 只 verify，不下载 / 不安装；missing 报 `✗` |
| `z42 xtask.zpkg deps install --drift` | 拿 `[platform.*]` 跟 Package.swift / build.gradle.kts 对账 |
| `z42 xtask.zpkg deps install --os android --print-env` | 输出可 `eval` 的 `export ANDROID_NDK_HOME=...` |

## 典型流程

**首次 clone / 新 dev 环境**：
```bash
z42 xtask.zpkg deps install --check          # 看缺什么
z42 xtask.zpkg deps install                  # 装跨平台 toolchain
z42 xtask.zpkg deps install --os android     # 需要打 android 时再装该平台必备
z42 xtask.zpkg deps install --drift          # 校验投影文件跟 versions.toml 一致
```

**commit 前 / 归档前（必跑，workflow 阶段 8 全绿入口）**：
```bash
z42 xtask.zpkg test               # 串联 dotnet build/test + test vm + test cross-zpkg + test lib
```

> 不要单独只跑其中一个 stage 就当作通过 —— 历史上 cross-zpkg subclass catch
> bug 就是因为 `test lib` 不在默认 GREEN 路径里，每次 spec 验证都被漏掉。

**日常开发循环（最高频）**：
```bash
z42 xtask.zpkg regen              # 改了编译器后重生 .zbc 基线
z42 xtask.zpkg test vm            # 跑 VM 端到端（interp + jit）
# 或只测受改动影响的 stage：
z42 xtask.zpkg test changed
```

**改了 stdlib `.z42` 源**：
```bash
z42 xtask.zpkg build stdlib       # 重编 stdlib zpkg（扁平视图，无 namespace 索引）
z42 xtask.zpkg test vm
```

**完整发行验证**：
```bash
z42 xtask.zpkg build package release   # 打 host-RID 发行包
z42 xtask.zpkg test dist               # 端到端验证发行包（packaged z42c/z42vm 跑 golden + launcher smoke）
```

## 依赖关系图

```
                          ┌─→ dotnet build / test
                          ├─→ test vm        (xtask_test_vm + xtask_golden)
z42 xtask.zpkg test ──────┼─→ test cross-zpkg (xtask_test_cross + xtask_golden)
                          └─→ test lib

z42 xtask.zpkg regen ─→ z42 xtask.zpkg test vm     (开发循环单独用)

cold bootstrap: dotnet -- build --workspace (primer)  →  build scripts/xtask.z42.toml  →  z42vm xtask.zpkg -- build stdlib
```

## xtask 源码结构

| 文件 | 职责 |
|------|------|
| `xtask.z42` | 入口 + 命令路由（build / test / deps / regen / audit / bench）|
| `xtask_versions.z42` | 共享 `versions.toml` 读取器（`_vget` / `_vRead` / `_vReadOr` / `_scalarStr`）|
| `xtask_stdlib.z42` | `build stdlib`：z42c `build --workspace` + 扁平视图（无 namespace 索引） |
| `xtask_test.z42` | `test` 路由 + `_testAll` / `_testLib` + 发行包发现 |
| `xtask_test_vm.z42` / `xtask_test_cross.z42` / `xtask_test_dist.z42` / `xtask_test_changed.z42` | 各测试 stage 实现 |
| `xtask_golden.z42` | 共享 golden 枚举 / 入口推导 helpers（被多个 test stage 复用）|
| `xtask_regen.z42` | `regen` 重生 `.zbc` 基线 |
| `xtask_deps.z42` | `deps check` 版本漂移检查 |
| `xtask_audit.z42` | `audit` 补缺失 `using` |
| `xtask_install*.z42` | `deps install` 各平台 / SDK 安装 |
| `xtask_package*.z42` | `build package` 各 RID 类别（desktop / ios / android / wasm）|
| `xtask_bench.z42` | `bench` 基准测试 |

每个命令的详细 Usage 见 `z42 xtask.zpkg --help` 与各源文件顶部注释。
