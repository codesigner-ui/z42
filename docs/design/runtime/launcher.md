# z42 launcher (`z42`)

> 长期规范。来源 spec：`docs/spec/changes/add-z42-launcher/`（add-z42-launcher, 2026-06-02）。

## 定位

`z42` 是用户**一次性安装的唯一入口**：给定一个 z42 应用（Exe-mode zpkg），解析它需要的运行时版本，用对应的 `z42vm` 跑起来，并把命令行参数透传给程序；同时管理本机已装的多个运行时版本。类比 `dotnet` muxer + `rustup`。

**设计铁律 —— z42 优先**：bootstrap 约束（没有 VM 就跑不了 z42）决定"找/给 VM"的**最小核**必须原生；除此之外**全部逻辑用 z42 写**。因此 launcher 拆成两层：

```
z42  (原生 trampoline, Rust, ~85 行)
      │  定位 $Z42_HOME/launcher/{z42vm, launcher.zpkg, libs}
      ▼
z42vm launcher.zpkg  --  <用户 argv 原样>      ← 之后全是 z42 代码
      │
      ▼
launcher 核心 (z42 → launcher.zpkg, Exe-mode)
      │  解析 argv / 子命令 / 读 ~/.z42 / 解析版本
      │  run: Std.IO.Process.Spawn
      ▼
$Z42_HOME/runtimes/<ver>/z42vm  <app.zpkg>  --  <app args>
```

- **launcher 运行时**（`$Z42_HOME/launcher/`）：随 launcher 一起装的固定 `z42vm + launcher.zpkg + libs`，**只用来跑 launcher 核心自己**，避免"跑 launcher 需要先选运行时"的鸡生蛋。
- **app 运行时**（`$Z42_HOME/runtimes/<ver>/`）：受 launcher 管理，用来跑用户 app。

trampoline 永远只用 launcher 运行时跑核心，**不随 release 变**；所有行为都在 `launcher.zpkg` 里，可单独升级。

## 依赖前置：z42vm 透传命令行参数（phase 0）

`z42vm` CLI 末尾接受 `-- <args>`（clap `last = true`），存入 `VmCore.program_args`，由 `__env_args` builtin → `Std.IO.Environment.GetCommandLineArgs()` 返回。**只有 `--` 之后的 token** 是程序参数；VM 自身的 `file/entry/--mode` 不在内；无 `--` 则为空。

> 这是参数透传的**永久归属**：放在 Rust 运行时（自举只重写编译器，不重写 VM），**不放 z42c**（编译器会被 z42 重写）。

## 磁盘布局（`$Z42_HOME`，默认 `~/.z42`）

```
~/.z42/
├── bin/z42                     # trampoline（在 PATH 上）
├── launcher/                   # launcher 自身运行时（固定）
│   ├── z42vm
│   ├── launcher.zpkg
│   └── libs/                   # stdlib zpkg
├── runtimes/<ver>/             # app 运行时（受管）
│   ├── z42vm   (或 bin/z42vm)
│   ├── libs/
│   └── link.txt                # 可选：重定向到一个含 z42vm+libs 的本地目录
└── config.toml                 # default = "<ver>"
```

`runtimes/<ver>/` 既可以是真实运行时目录，也可以只放一个 `link.txt`（内容为一个本地构建目录的绝对路径）——后者用于 dev：`z42 link <dir> --as <ver>`，无需拷贝。

## 命令（P1）

| 命令 | 行为 |
|------|------|
| `z42 run [--runtime V] <app.zpkg> [-- <args>]` | 解析版本 → 用 `runtimes/<ver>/z42vm` 跑 app，继承 stdio，`--` 后参数透传，设 `Z42_LIBS`，回传退出码 |
| `z42 <app.zpkg> [-- <args>]` | 裸 apphost 形式，等价 `run` |
| `z42 link <dir> --as <ver>` | 把含 `z42vm`(+`libs/`) 的本地目录注册为 `<ver>`（写 `link.txt`） |
| `z42 list` | 列已装运行时（标注 default） |
| `z42 default [<ver>]` | 显示 / 设置默认版本（写 `config.toml`） |
| `z42 which [--runtime V] [app]` | 打印解析到的 `z42vm` 路径 |
| `z42 info` | 打印 `Z42_HOME` / runtimes 目录 / default / 已装数量 |

### 版本解析顺序

```
--runtime <ver>  >  app 自带版本声明(P1 暂空)  >  config.toml default  >  唯一已装  >  报错并列候选
```

## 发布打包：便携模式（portable, model A — bundle-launcher-in-release, 2026-06-03）

`scripts/package.sh` 的 desktop 包内置 launcher,**解压即用、无需安装**:

```
z42-<ver>-<rid>-<profile>/
├── z42            # trampoline（包入口，根；target-specific，cargo --target）
├── launcher.zpkg  # launcher 核心（RID-independent bytecode，host driver 编）
├── bin/           # 应用/可执行集合
│   ├── z42c
│   └── z42vm
├── libs/          # stdlib zpkg
├── native/  examples/  manifest.toml
```

**便携解析**(launcher-at-package-root, 2026-06-04):trampoline 先找 `$Z42_HOME/launcher`(installed 模式,优先);找不到则**按自身位置**回退——trampoline 在包根 `<pkg>/z42`,故 `exe.parent()` 即包根 → `<pkg>/bin/z42vm` + `<pkg>/launcher.zpkg` + `<pkg>/libs`,用它跑 launcher 核心,并设 `Z42_PORTABLE_VM`/`Z42_PORTABLE_LIBS` = 包内 z42vm+libs。核心的 `run`/`which` 在未 pin `--runtime` 时直接用这个 portable runtime(不查 `runtimes/<ver>`)。于是 `<pkg>/z42 run app.zpkg` 开箱即用,**不重复 z42vm/libs、不用 symlink**(Windows 友好)。`z42` 在根而非 `bin/`:它是包的统一入口,`bin/` 留给 app(z42c/z42vm/未来工具)。

打包步骤见 `scripts/_lib/package_desktop.sh` [2c];`z42 xtask.zpkg test dist` 有 portable `z42 which` smoke。

## 安装模式（installed, model B — install-z42-to-home, 2026-06-03）

包根的 `install.sh`(由 package_desktop.sh 内置)把包铺进 `$Z42_HOME`(默认 `~/.z42`):
- `bin/z42`(trampoline,放 PATH)+ `bin/z42c`(编译器)
- `launcher/{z42vm, launcher.zpkg, libs}`(跑 launcher 核心)
- 本版本经 **`z42 link $Z42_HOME/launcher --as <ver>` + `z42 default <ver>`** 注册为 app 运行时——`runtimes/<ver>/link.txt` 重定向到 `launcher/`,**不二次拷贝 z42vm/libs**。
- 版本号从包内 `manifest.toml` 读。

装完 `z42` 在 PATH 上、`z42 run app.zpkg` 任意目录可用(trampoline **installed 优先于 portable**),多版本可共存于 `runtimes/<ver>`。`install.sh` 只**打印** PATH 接入指引,不自动改 profile。

> Windows `install.ps1` + 自动 PATH/profile = 后续;联网 `install <ver>`/`self update`(P2)见下。

## 项目本地引导（z42-bootstrap — install-z42, 2026-06-04）

为了用 z42 自己实现的仓库构建工具(`xtask.zpkg` + 迁移后的脚本),仓库需要先有一个可用的 z42 launcher —— 鸡生蛋。`scripts/install-z42.{sh,bat,command}` 是**唯一保留的原生引导脚本**:从 GitHub Releases 下载预编译 launcher 包,装到**项目本地** `<repo>/.z42`(隔离、gitignore、不碰系统 `~/.z42`)。

- **版本**:`versions.toml [toolchain.z42].launcher`,默认 `nightly`(也可 pin `0.1.0`)。
- **按 RID 下载**:`z42-<ver>-<rid>.{tar.gz|zip}` ← `releases/download/<tag>/`;对 `SHA256SUMS` 校验。
- **版本检查(每次跑)**:nightly 比对 release `published_at`(存于 `.z42/.bootstrap-stamp`),变了才重下;pin 版装一次即跳过。
- **入口**:装完即 `.z42/z42`(launcher-at-package-root 后在根)。
- **`.bat`** 走 PowerShell(下载/解压/Get-FileHash);**`.command`** 是 macOS Finder 双击 → exec `.sh`。

这是 `bootstrap → xtask` 链路的第一环:`install-z42.sh` → `.z42/z42 xtask.zpkg build/test/...`。

## app `runtimeconfig.json`（版本声明 + 动态配置 — add-runtimeconfig-json, 2026-06-03）

app 可在 **`<app>.runtimeconfig.json`** sidecar(.NET 同款,独立于 zpkg,可编辑/动态)声明所需运行时版本 + 运行时旋钮:

```json
{
  "runtime": { "version": "0.3.4", "rollForward": "exact" },
  "configProperties": { "Z42_GC_MODE": "concurrent", "Z42_SAFEPOINT_THROTTLE": 1024 }
}
```

`z42 run <app.zpkg>` 时,launcher 核心(`Std.Json` 解析):
- `runtime.version` 进入版本解析,**优先级**:`--runtime` > runtimeconfig `runtime.version` > `config.toml` default > 唯一已装。
- `configProperties` 的**任意** key/value 作为 env 设到被 spawn 的 z42vm(`Std.IO.Process.Env`)——支持任何 `Z42_*` 旋钮(GC 模式 / safepoint throttle / …),动态生效。
- `rollForward`:P1 只认 `exact`。

> 独立 sidecar 的好处:版本无关的 launcher 读它**不需解析带版本的 zpkg 格式**;可手改、可被工具生成。这也是 P2(下载即用)的前置——"声明需要的版本 → 没装自动拉"(自动拉 = P2)。

## Deferred / Future Work

### launcher-future-install: 下载 / install / uninstall / self-update（P2）

- **来源**：add-z42-launcher proposal（P2）
- **触发原因**：P1 先把本地（`link` + `default`）跑通，不引入网络/下载基础设施
- **前置依赖**：每平台×版本的 `z42vm` 发布点（GitHub Releases；`scripts/package.sh` 产 host-RID 包可作产物）+ 完整性校验
- **当前 workaround**：`z42 link <本地构建目录> --as <ver>`

### launcher-future-version-declaration: app 自带运行时版本声明

- **来源**：add-z42-launcher design（决策 D4）
- **触发原因**：是否把"需要哪个运行时版本"写进 zpkg `META.toolchain_version` 还是独立 `runtimeconfig.json` sidecar 未定；strict-pin 下 P1 用 `link`+`default` 本地指定即可
- **触发条件**：进入分发场景（P2 下载）时必须定
- **当前 workaround**：版本解析第 2 步（读 app 自带声明）留空 hook

### launcher-future-single-file-exe-zpkg: z42c 从裸 `.z42` 脚本直接产 Exe-zpkg

- **来源**：add-z42-launcher（原 phase 0.5）
- **触发原因**：launcher 核心与 dev 脚本都可作为**普通 `kind="exe"` 项目**（带 `z42.toml`）经现有 `z42c build` 产 Exe-zpkg；单独实现"裸脚本 → Exe-zpkg"需在 SingleFileCompiler 重新装配 zpkg（sourceHash/namespace/deps），与已测项目路径重复，ROI 低
- **触发条件**：若大量一次性脚本需免 `z42.toml` 的极简体验再做
- **当前 workaround**：脚本写成 5 行 `z42.toml`（`kind="exe"`）的 mini-project，`z42c build` 即得 Exe-zpkg
