# Design: apphost —— 每-app 原生可执行文件

## Architecture

```
z42c build app  →  app.zpkg                                      (今天)
z42 apphost build app.zpkg  →  ./app  (+ 旁边的 app.zpkg)        (本变更新增)

运行 ./app arg1 arg2:
┌─────────────────────────────────────────────────────────────────┐
│ apphost stub (Rust, src/bin/apphost.rs)                          │
│  1. current_exe() → exe_dir                                       │
│  2. 读内嵌占位符 static → app 相对路径 "app.zpkg"                  │
│     → resolve <exe_dir>/app.zpkg                                  │
│  3. 本地优先解析 launcher 运行时（见 Decision 3）:                 │
│       $Z42_HOME  >  本地 .z42(exe 上行)  >  $HOME/.z42            │
│  4. exec z42vm launcher.zpkg -- <app.zpkg> -- arg1 arg2          │
│     （设 Z42_LIBS = 运行时 libs；回传退出码）                       │
└───────────────────────────────┬─────────────────────────────────┘
                                 ▼
            z42vm launcher.zpkg  --  app.zpkg  --  arg1 arg2
                                 ▼
   launcher 核心（裸 apphost 形式，已存在）：
     读 app.zpkg + app.runtimeconfig.json → 解析版本 → 起 app
```

**关键复用**：apphost stub 不自己实现版本解析 / runtimeconfig，**原样走 launcher 核心已有的裸 apphost 形式**（`launcher.md:59` `z42 <app.zpkg>` 等价 run）。stub 唯一多做的：① 从内嵌占位符拿到"我是哪个 app"；② 本地优先的运行时探测。其余一切（"找/给 VM 的最小核"之外的逻辑）仍在 z42。这是对"z42 优先"铁律的延续：apphost 是 trampoline 的"专属化"，不是绕过它。

**与 trampoline 的关系**：trampoline (`z42`) 与 apphost 共享 `lib.rs`（`Runtime`/`z42_home`/探测/exec）。差异仅两点——app 来源（argv vs 内嵌占位符）与解析顺序（installed-first vs local-first）。故 apphost 作为同 crate 第二个 bin，不新建 crate。

## Decisions

### Decision 1：占位符 patch 机制（.NET 同款）

**问题**：apphost 怎么知道自己要跑哪个 zpkg？

**选项**：
- A — **约定同名相邻**：`./myapp` 找同目录 `myapp.zpkg`。零 patch，但改 exe 名即失联。
- B — **内嵌占位符 patch**：模板里留固定大小占位字节区，build 时覆写成 app 路径。改名安全。
- C — single-file 内嵌整个 zpkg 字节。最重，依赖 C ABI。

**决定**：选 **B**（User 已定）。机制：

```
占位区 = MAGIC(32 字节，唯一 sentinel) || payload(992 字节)
stub:  #[used] pub static Z42_APPHOST_TARGET: [u8; 1024] = [MAGIC.., 0u8..];
未 patch 模板: payload 全 0
patcher:  在二进制文件里搜 MAGIC → 把 payload 覆写为 UTF-8(app 相对路径) + NUL
runtime:  读 static；payload[0]==0 ⇒ 未配置 → 报错退出；否则 CStr → 相对路径
          MAGIC 原样保留（便于重 patch / 校验）
```

- **build-time 定位靠 MAGIC**（在文件里 grep 这串唯一字节），**runtime 读靠 static 符号地址**（无需重扫）；`#[used]` 防优化裁剪。
- app 路径**相对 exe 目录**（`app.zpkg`，非绝对）→ dist 目录可整体搬移；改 exe 名不改内嵌路径，仍找 `app.zpkg`，故改名安全。
- payload 1024 字节足够（路径远短于此）；超长路径 → patcher 报错（不截断）。

### Decision 2：framework-dependent，不内置运行时（P1）

**问题**：apphost 从哪加载 VM + stdlib？

**决定**：P1 **只做 framework-dependent**（User 已定）——apphost **不**捆绑 `z42vm`/`libs`，而是去本地/系统已装的 launcher 运行时找。self-contained（捆绑）与 single-file（链 VM）延后（见 Deferred）。理由：先把"原生 exe + 占位符 + 本地优先解析"这条最小闭环跑通；捆绑只是把同一份运行时拷到 app 旁，是正交的打包增量。

### Decision 3：本地优先的运行时解析规则 🔴（待 User gate 确认）

**问题**：User 要"优先本地目录"，且明确"本地目录查询规则要确定"。"本地"到底指什么、与系统目录怎么排序？

**推荐规则**（apphost stub 解析 launcher 运行时 = `{z42vm, launcher.zpkg, libs}` 的目录）：

```
1. $Z42_HOME 非空            → $Z42_HOME/launcher/{z42vm,launcher.zpkg,libs}   （显式配置，最高优先）
2. 本地：从 exe 目录起逐级上行到文件系统根，每级查
     <dir>/.z42/launcher/{z42vm,launcher.zpkg,libs}   （installed 风格）
   或 <dir>/.z42/{bin/z42vm,launcher.zpkg,libs}        （portable 风格）
   首个命中即用                                          （项目钉死的运行时，本地优先）
3. 系统：$HOME/.z42/launcher/...（Windows 加 $USERPROFILE）
4. 都没有 → 报错：未找到 z42 运行时；请装 z42 或设 Z42_HOME，列已查路径，非零退出
```

**为什么 exe 目录上行**：apphost 装到哪、它的 `.z42` 项目运行时就在那棵树里（对齐仓库引导的 `<repo>/.z42` 模型）。比 cwd 更确定（不受"从哪调用"影响）。

**为什么本地优先于系统**：这正是 User 要的"项目钉死运行时"语义——某项目带 `.z42` 时，app 用它而非系统 z42（类比 `node_modules/.bin`、venv）。代价：本地 `.z42` 存在时会**遮蔽**系统安装；可接受，且 `$Z42_HOME` 永远能覆写。

**配置支持**：P1 的"配置"= ① `$Z42_HOME` 环境变量覆写（主旋钮）；② `app.runtimeconfig.json` 的 `runtime.version` 选版本（launcher 核心已处理，stub 不碰）。富搜索路径配置 / cwd 上行 → Deferred。

> **gate 决策点**：是否在第 2 步额外加"cwd 上行"？推荐**不加**（只 exe 上行，确定性更强）。请 User 拍板。

### Decision 4：patcher 落在 launcher.zpkg（而非新 packager）

**问题**：`apphost build`（拷模板 + patch + chmod）这段 z42 逻辑放哪？

**选项**：A — `launcher.zpkg` 加 `apphost` 子命令；B — 新建 `src/toolchain/packager/` zpkg。

**决定**：选 **A**。launcher 已管 install/link/uninstall 这类分发命令，`apphost build` 与之同族；复用其 file/path/process helper + 命令分发，且 launcher.zpkg **每个包都已 ship**（patcher 自然随处可用）。逻辑落**新文件** `core/apphost.z42`（launcher.z42 已 498 行，逼近软上限，不再加塞）。若日后分发命令成规模，再整体迁 packager（独立 refactor）。

### Decision 5：参数透传形态

apphost `./app a b` → stub forward = `[<app.zpkg 绝对/相对路径>, "--", "a", "b"]`，即 `z42vm launcher.zpkg -- <app.zpkg> -- a b`。launcher 核心 `GetCommandLineArgs()` 得 `[app.zpkg, --, a, b]`：首 token 当裸 apphost → run，`-- a b` 透传给 app。与 trampoline 的 forward 差异：apphost 在用户 argv 前**注入 `<app.zpkg> --`**。

### Decision 6：z42.toml 配置 apphost 开关

**问题**：项目怎么声明"这个 exe 要不要产 apphost"？（.NET `<UseAppHost>` 的对应物）

**选项**：
- A — **flat 布尔 `apphost`**，落在 `[[exe]]`（多目标）/ `[project]`（单 exe 推断），与既有 `pack` / `entry` / `name` 同级。
- B — 独立 `[apphost]` 表（`enabled = true`，留 self_contained/rid 增长空间）。
- C — 绑 profile（`[profile.release] apphost = true`，类比 .NET publish）。

**决定**：选 **A**。理由：
- `[project] pack = true`（packed-vs-indexed zpkg）已是同类"输出形态布尔"先例；`apphost` 与之并列最 idiomatic。
- 放 `[[exe]]` 级 → 一个项目多个 exe 目标可各自决定（与 `name`/`entry` 同 cascade：`[[exe]].apphost` > `[project].apphost`）。
- 不是 profile 关注点（debug/release 都可能要 apphost）→ 不绑 profile。
- self-contained / rid 等增长项到来时，再把布尔升级为 `[[exe.apphost]]` 子表（届时 self-contained 解锁，独立 change）；P1 不预留空表。

**写法**：

```toml
# 单 exe（kind 由 [[exe]] 或 entry 推断）
[project]
kind    = "exe"
entry   = "App.Main"
apphost = true        # 产 ./app（原生 stub）+ app.zpkg

# 多 exe 目标，逐个决定
[[exe]]
name    = "myapp"
entry   = "App.Main"
apphost = true        # 仅此目标产 apphost
[[exe]]
name    = "helper"
entry   = "Helper.Main"
# 不写 → 不产 apphost，只 helper.zpkg
```

**默认值与消费方（关键 phasing）**：
- **默认 `false`（显式 opt-in）**——直到 `apphost-future-package-template`（发布包内置 per-RID native 模板）落地：模板尚不随包 ship 时，默认 true 会让没模板的环境 build 失败/告警，故先 opt-in。**模板随包 ship 后，可翻成 `kind="exe"` 默认 true**（对齐 .NET，单独决策）。
- **谁读这个字段**：`z42c build`（编译器 driver）。即"build 时按 `apphost=true` 自动调 patcher"= Deferred **`apphost-future-build-flag`**（碰 `compiler`/`z42c` 锁）。**本变更（P1）不读 z42.toml**——P1 只提供手动 `z42 apphost build app.zpkg`。字段 schema 现在定死（避免日后改名），消费延后。
- 故 **P1 不动 `docs/design/compiler/project.md`**（不文档化尚未被读取的字段）；schema 落地 + parser 支持随 `apphost-future-build-flag` 一起进 `project.md`。

## Implementation Notes

- **lib.rs 抽取**：`Runtime { vm, core, libs, portable }`、`z42_home()`、`probe_runtime(dir) -> Option<Runtime>`（判一个目录是否含完整运行时，installed/portable 两式）、`exec_core(rt, core_args) -> !`。trampoline = `installed → portable`；apphost = `Z42_HOME → 本地上行 → $HOME/.z42`，两者拼 `probe_runtime` 即可。
- **占位符模块**：`Z42_APPHOST_TARGET` static + `parse_target() -> Option<PathBuf>`（校验 payload[0]!=0、CStr 解析）。MAGIC 取一段固定 32 字节（建议含可读子串如 `z42-apphost-target-v1` + 随机尾，便于 grep 且不易撞）。
- **patcher（apphost.z42）**：`apphost build <app.zpkg> [--out <name>]`：
  1. 解析 app.zpkg 路径 → 默认 out 名 = app 文件名去 `.zpkg`（exe 名）。
  2. 定位模板：runtime bin 目录（z42vm 同级）找 `apphost`(`/apphost.exe`)。
  3. 读模板字节 → 搜 MAGIC → 越界/未找到则报错 → 覆写 payload = UTF-8(app 相对 out 目录的 zpkg 名) + NUL。
  4. 写 `./<out>` + chmod 0755（Unix）。
  5. 不动 `app.zpkg`（仍需相邻；P1 framework-dependent）。
- **chmod**：经 `Std.IO`（确认有 set-permission API；缺则停下按 dogfill 规则在 z42 stdlib 补，记 `project_scripts_z42_port` 缺口）。
- **Z42_LIBS**：stub 解析到 runtime 后，`exec` 前设 `Z42_LIBS = <runtime>/libs`（与 trampoline 一致），衔接 VM 的 libs 搜索。
- **退出码**：`exec` 失败 → 打印 + 非零退出；子进程退出码原样回传。

## Testing Strategy

- **Rust 单元测试**（inline `#[cfg(test)]`，launcher crate）：
  - 占位符：未 patch（payload 全 0）→ `parse_target()` = None；patch 后 → 得正确路径；MAGIC 完整。
  - 解析顺序：临时目录搭 `$Z42_HOME` / 本地 `.z42` / `$HOME/.z42` 三态，断言 `Z42_HOME > 本地 > 系统`；本地命中遮蔽系统；都无 → None。
- **z42 [Test]**（`core/tests/apphost_patch_test.z42`）：`apphost build` patch 一个样例 zpkg → 读回字节断言 payload 被覆写 + NUL 终止；未配置模板（payload 全 0）跑 → 报"apphost not configured"。
- **e2e 烟测**：patch 样例 app → `./app foo` 跑 → 断言 app 见到 `[foo]` + 退出码透传。挂到 launcher 的 dist smoke（与现有 portable `z42 which` smoke 同处；具体 stage 文件实施时确认，沿用 `z42 xtask.zpkg test dist`）。
- GREEN：`z42 xtask.zpkg test`（含 cross-zpkg / lib）；发行相关追加 `test dist`。

## Deferred / Future Work

> 同步索引到 `docs/roadmap.md` Deferred Backlog Index。

### apphost-future-self-contained: self-contained 模式（VM+libs 随 app 本地化）

- **来源**：本 proposal Out of Scope（叉路 2，User 选"先只 framework-dependent"）
- **触发原因**：先跑通 framework-dependent 最小闭环；捆绑是正交打包增量
- **前置依赖**：apphost build 增 `--self-contained`：把解析到的 `z42vm`+`libs` 拷到 out 目录；stub 本地解析把"exe 同级 `.z42` 或 bin/libs"纳入
- **当前 workaround**：本地 `.z42` 放项目里（本地优先解析即用它）

### apphost-future-single-file: 单文件自包含（链 libz42_vm）

- **来源**：本 proposal Out of Scope（叉路 1 选项 C）
- **触发原因**：apphost 链接 `libz42_vm` + 内嵌 zpkg/libs，经 embedding C ABI（`z42_host_load_zbc` 内存加载）跑，免子进程/外部文件
- **前置依赖**：embedding C ABI 落地（`docs/design/runtime/embedding.md` 当前 draft）；碰 `runtime` 锁
- **当前 workaround**：framework-dependent（exe + 相邻 zpkg + 已装运行时）

### apphost-future-build-flag: `z42c build` 读 z42.toml `apphost` / `--apphost`

- **来源**：本 proposal Out of Scope（User 心智模型"z42c build 时覆写"）+ Decision 6
- **触发原因**：build 时按 z42.toml `[[exe]].apphost` / `[project].apphost`（Decision 6 已定 schema：flat 布尔，默认 false）自动调 patcher，是便捷糖；碰 `compiler`/`z42c` 锁，单列
- **前置依赖**：本变更的 `z42 apphost build` 命令落地；随此项把 `apphost` 字段进 `docs/design/compiler/project.md` schema + z42c project 解析 + driver 调 patcher（外加 CLI `--apphost` 覆写）
- **当前 workaround**：`z42c build app` 后手动 `z42 apphost build app.zpkg`（z42.toml 的 `apphost` 字段 P1 暂不被读取）

### apphost-future-package-template: 发布包内置 per-RID apphost 模板

- **来源**：本 proposal Out of Scope
- **触发原因**：`z42 apphost build` 需能在已装环境取到 native apphost 模板（按 RID）；P1 只在 dev（cargo 产物）可用
- **前置依赖**：`z42 xtask.zpkg build package` 把 apphost 模板铺进包 + `manifest.toml`；碰 xtask 打包
- **当前 workaround**：dev 期从 cargo 产物路径取模板

### apphost-future-cwd-search: cwd 上行 / 富搜索路径配置

- **来源**：Decision 3
- **触发原因**：P1 本地搜索只 exe 目录上行（确定性优先）
- **触发条件**：出现"从任意 cwd 跑、想用 cwd 树里的 `.z42`"需求
- **当前 workaround**：设 `$Z42_HOME` 或把 app 放进带 `.z42` 的项目树
