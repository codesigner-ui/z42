# Design: build/publish 命令归位

## Architecture

```
z42 build <toml>   ──forward──►  z42c (compiler)            # 编译
z42 publish <toml> ──forward──►  z42b (builder)             # 部署编排
                                   │
                                   ├─ zpkg 不存在？ ──► spawn z42c build   (build-if-needed)
                                   └─ Apphost.Produce(bin/payload 布局)   (z42.workload.desktop)
z42 test/bench/clean ─forward──►  z42b                       # 已有
z42 export ios/...   ─────────►  launcher（不动）
```

命令面统一：launcher = muxer + runtime resolution，把动词透传给真正的 owner（z42c 编译 / z42b 编排）。

## Decisions

### Decision 1: build → z42c 子进程转发（非 z42b 编排 / 非 in-process API）
**问题**：`z42 build` 归谁？
**决定**：转发 z42c。build=编译，是 z42c 的本职；z42b 是部署/测试编排。照搬 `_forwardZ42b` 写 `_forwardZ42c`，起 `z42vm programs/z42c/z42c.driver.zpkg -- build …`。这取代了 wire-z42b-host-build 设想的「z42b 持 in-process ICompiler 做 build」——更轻、无编译器 API 依赖、无 z42.project 串味。

### Decision 2: publish 迁 z42b，launcher 转发（与 test/bench 一致）
**问题**：publish 实现该住哪？
**决定**：住 z42b。理由：① 命令面一致（test/bench/clean 已转发 z42b，publish 也该）；② publish=部署编排，属 z42b 职责；③ publish 不碰 z42.project/z42.build（只用 `Apphost.Produce` + z42.toml），**不触发**自举串味雷区。launcher `publish` 退化为 `_forwardZ42b`。z42b router 已注册 `publish`（builder_cli.z42:51），只需接 dispatch。export 留 launcher（workload export 逻辑仍在 launcher_export，单独迁移无收益）。

### Decision 3: publish 自带编译（build-if-needed），但 xtask 仍控编译环境
**问题**：publish 发现没编译就先编，会不会在 xtask 组装时用错 z42c/Z42_LIBS 破坏字节一致？
**决定**：build-if-needed 主要服务**终端用户** `z42 publish myapp`（一步建+部署）。**xtask 组装发行包时仍显式控制编译**（用它指定的 z42c seed + Z42_LIBS 预先 build，或给 publish 传对的环境），publish 看到 zpkg 已在就跳过自动编译。即：zpkg 存在 → 直接产 apphost（xtask 路径，字节可控）；不存在 → spawn z42c build（用户便捷路径）。两条路径不冲突。

### Decision 3.5: apphost stub 解析留 launcher，经 env 传 z42b（避免拖入 runtime 解析基础设施）
**问题**：publish 产 apphost 需要 stub 路径，而 stub 住「已装 desktop workload」——这是 launcher 域的
runtime/workload 解析（`_desktopApphostStub`/`_installedVersions`/`_runtimesDir`）。搬 publish 到 z42b
会把这套基础设施一起拖过去（且 `_cmdRunDeploy` 留 launcher 仍用它们）。
**决定**：**launcher 预解析 + gate stub，经已存在的 `Z42_APPHOST_TEMPLATE` env 覆盖传 z42b**
（`_desktopApphostStub` 本就先查该 env）。launcher publish 转发 = 解析 rid（默认 host）+ `_requireDesktopApphost`
（含"未装 workload"友好报错）→ 注入 `--rid <rid>` + `Z42_APPHOST_TEMPLATE=<stub>` env 转发。z42b 只从
env 读 stub，**不含任何 runtime/workload 解析**。直接 `z42b publish`（绕过 launcher）则要求 env 已设，否则
报错指引用 `z42 publish`。

### Decision 4: 共享 helper 的去留
**问题**：`_cmdPublishDesktop` 与 export 共用 `_platformStr` / `_requireDesktopApphost` / `_desktopApphostStub` 等。
**决定**：随用随走——publish 专属 helper（`_desktopResolveZpkg` / `_requireDesktopApphost` / `_desktopApphostStub`）搬去 z42b；export 仍用的通用 helper（`_platformStr` / `_expResolveZpkg`）留 launcher，z42b 侧按需复制最小集（z42b 已有 toml 解析能力）。实施时以「编译通过 + 行为不变」为准，不强求零重复。

### Decision 6: apphost patcher 内联 z42b（z42b 必须保持纯 stdlib 依赖）
**问题**：z42b 兼作**反射测试运行器**——`stdlib [Test]` 构建阶段用**只含 stdlib 的 Z42_LIBS** 编 z42b。
给 z42b 加 `z42.workload.desktop`（为 `Apphost.Produce`）后，该阶段编 z42b 报 `E0401: undefined Apphost`
（workload 不在 stdlib 上下文）。完整 GREEN 门实测炸在此（首轮 stdlib stage）。
**决定**：**内联 apphost patcher**（`builder_apphost.z42` 的 `_pubProduceApphost`，镜像 workload
`Apphost.Produce` + xtask `_produceApphost`），z42b 依赖收回 stdlib-only（core/io/cli/test/toml）。代价：
patcher 第三份副本（workload / xtask / z42b），MAGIC 须三处同步——这是 xtask 早已采用的同款先例与权衡。
**教训**：任何 z42b 改动不得引入 toolchain（非 stdlib）依赖，否则破坏其测试运行器构建上下文。

## Implementation Notes

- `_forwardZ42c`：镜像 `_forwardZ42b`——定位 bundled vm（Z42_PORTABLE_VM）+ `programs/z42c/z42c.driver.zpkg`，`Process(vm).Arg(driver).Arg("--").Arg("build")...` 继承 stdio + exit code。
- z42b publish build-if-needed：同样用 `_forwardZ42c` 思路 spawn z42c；z42b 已是被 launcher 用 vm 拉起的进程，能定位 vm + z42c。
- z42b deps 增量：`z42.workload.desktop`（Apphost.Produce）、`z42.toml`（TomlValue.Parse）、`z42.encoding`（Apphost 用 Utf8）。不加 z42.project/z42.build。

## Testing Strategy

- 单元：publish 解析 bin/payload + build-if-needed 分支（zpkg 存在跳过 / 不存在触发）。
- e2e：① `z42 build <toml>` 经 launcher→z42c 编出 zpkg；② `z42 publish <toml>`（zpkg 已在）产 apphost；③ `z42 publish`（zpkg 不在）自动编译再产 apphost。
- 回归：现有 `z42 publish desktop` 行为不变（package 重建 + dist 测试）；launcher/z42b 均编译通过。
- GREEN：`z42 xtask.zpkg test` 全 stage（其中 test compiler 7/7 保 z42b 仍能编、自举不破）。
