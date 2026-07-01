# Design: 配置驱动的发行包布局

## Architecture

```
组件 z42.toml                publish（暂存）              packages.toml + xtask
[platform.desktop]    ──►   artifacts/publish/<comp>/  ──►   [package.sdk]
  apphost=true               （= 包内最终布局子树）            include=[...]
  bin=...  payload=...                                          │
        │                          │                            ▼
   ① 部署布局（用户面）        自洽布局子树             ② 选取+合并子树 → 包根 + manifest
```

两层互不重叠：
- **① 部署布局**（用户面）：组件声明自己的 apphost bin/payload 落点；publish 编译 + 拓扑依赖 + 造 apphost，输出一个**与包内最终路径一致**的暂存子树。用户发自己的 app 只用这层。
- **② 发行组装**（z42 内部）：`packages.toml` 声明每个发行包 = 哪些组件子树合并；xtask 纯拷贝 + emit manifest。

## Decisions

### Decision 1: 布局放组件 toml（用户面），组装放 packages.toml（内部）
**问题**：「组件落哪」的信息放组件自己 toml 还是中央 packages.toml？
**决定**：放组件 toml。因为这层将来开放给用户发布自己的 app——用户的 app 与 z42 的 z42c/z42b/z42d 共用同一套 `[platform.desktop]` 旋钮。布局属于「组件自治」，是用户唯一会碰的旋钮，必须属于组件。packages.toml 只做 z42 内部 SDK 组装（选哪些组件），用户单 app 部署不需要它。

### Decision 2: apphost 用 `bin` + `payload` 双字段，相对路径自动推导
**问题**：apphost 二进制进 `bin/`、payload zpkg 进 `programs/<name>/`，这个拆分怎么配？
**决定**：`[platform.desktop]` 两个可选**完整相对路径**字段 `bin`（apphost 二进制路径）+ `payload`（payload zpkg 路径），均相对部署/包根。用完整路径而非目录：apphost 内嵌的就是精确 zpkg 路径，且 toolchain 现状有重命名（`z42.launcher.zpkg`→`programs/launcher/launcher.zpkg`），目录+推导名表达不了。apphost 内嵌路径 = 从 `bin` 目录到 `payload` 的相对路径，由 publish 计算（用户不手算）。
- 约定默认：未写 → `bin = "bin/<name>"`，`payload = "programs/<name>/<name>.zpkg"`。
- 特例覆盖：根 launcher `z42` → `bin = "z42"`（落根）+ `payload = "programs/launcher/launcher.zpkg"`。
- 镜像现状：z42c→`bin/z42c`+`programs/z42c/`，z42b→`bin/z42b`+`programs/z42b/`，z42→根+`programs/launcher/`。即把现在硬编码的布局转成声明。

### Decision 3: publish 直接输出布局子树（扩 `_cmdPublishDesktop`）
**问题**：「输出包布局子树」由 publish 本身做，还是 xtask 加一层 staging 包装？
**选项**：A — 扩 `_cmdPublishDesktop`：除产 apphost 外，把 payload zpkg 也放到 `payload` 目录、apphost 放到 `bin`，整体落 `--output <staging>/<comp>/`。B — publish 不变（只产 apphost），xtask staging 自己摆 zpkg。
**决定**：选 A。理由：① 用户面一致——用户 `z42 publish` 一条命令就拿到可部署的自洽子树，不是半成品；② 单一真相——布局逻辑只在 publish 一处，xtask 打包退化为纯 `include → 拷子树`，无布局知识。代价：`_cmdPublishDesktop` 从「产一个二进制」升级为「产一个子树」，但这本就是 publish 该有的语义。

### Decision 4: 稳定 staging 产物保留为固定 step；z42c 七成员靠 publish 自动依赖打包（不设 z42c-seed 特例）（2026-07-01 修订）
**问题**：cargo bin（z42vm）、cargo native（libz42.*）、stdlib glob、z42c 七成员——也要塞进 packages.toml？
**决定**：cargo/native/stdlib 三类仍是不变的固定 staging step，`include` 用稳定名引用（`"z42vm"`/`"native"`/`"stdlib"`），xtask 各有一个固定 staging handler。**z42c 七成员不再是第四个固定 staging handler**——`z42c.driver` 的 6 个非 driver 兄弟库是它在 workspace 内的**项目依赖**（区别于 stdlib 依赖，写法相同但语义不同），`z42 publish` 发布一个 apphost 时，自动解析该组件 `[dependencies]` 中命中当前 workspace 成员的条目（按 `z42.workspace.toml` `default-members` + 同名目录约定识别、递归取传递闭包），build-if-needed 后与主 zpkg 一起落到同一个 `payload` 目录。因此 packages.toml 里 z42c 不需要单独的 "z42c-seed" 条目，`include` 里只写一次 `"z42c"`，6 个兄弟库随它自动带出——这是**通用行为**（任何 z42 app 依赖本地 workspace 库都适用），不是 z42c 专属逻辑。诚实承诺：**加常规 apphost（含带项目内依赖的）= 纯配置（组件 toml + packages.toml 一行）；加一种全新产物类型（cargo/native/stdlib 之外）= 偶尔加一个 staging handler**。
实现位置：`builder_publish.z42` 新增 workspace 成员发现（`_pubFindWorkspaceRoot` 沿 tomlPath 向上找 `z42.workspace.toml` + 读 `default-members`）+ 依赖闭包解析（比对当前组件 `[dependencies]` key 与成员名，BFS 取传递闭包）+ 逐个复用既有 `_pubEnsureBuilt`（build-if-needed）后拷进 payload 目录。z42b 只依赖 stdlib 的既有约束不变——这段逻辑只用 `Std.Toml` 手写 TOML 遍历，不 import `z42c.project`/`z42c.pipeline`。

### Decision 5: 构建顺序从依赖图推导，不靠 include/layout 顺序
**问题**：launcher zpkg 依赖 `z42.workload.desktop` 先进 Z42_LIBS。
**决定**：publish/build 阶段按各 `z42.toml` 的 `[dependencies]` 拓扑排序产 zpkg（xtask 自举 build 已有此能力）。packages.toml 的 `include` 顺序**只影响拷贝顺序、不影响构建顺序**，避免顺序 bug。

### Decision 6：z42 组件先 publish 到暂存根，各包从暂存根拷贝——不是各包各自直接 publish（2026-07-01 确认）
**问题**：`z42c` 这类被 `sdk`/`runtime` 两个包同时 `include` 的组件，装配时怎么做？4.1a 实施期先斩后奏走了「每个包各自直接 `z42b publish --output <包目录>`」的捷径（省了暂存层，验证 3 个 apphost publish 链路本身可行）——但这与本节原定的暂存根架构不一致，需要在推进 3.2/3.3 组装引擎前明确哪个是最终形态。
**选项**：A — 每个需要该组件的包各自直接对组件 toml 跑一次 `z42b publish`（利用 build-if-needed 幂等，省去暂存层，但产出与组装是同一步、拷贝逻辑分散在各包各跑一次 publish 里）；B — 组件先 `z42b publish` 到统一暂存根 `artifacts/publish/<comp>/`，`sdk`/`runtime` 两个包各自从这个暂存根拷贝一份进包目录（产出与组装严格分层，`include` 解析永远只是「读一个目录树、拷贝」，不知道 publish 怎么跑）。
**决定**：选 B。暂存根路径确认为 `artifacts/publish/<comp>/`（不再是 Phase 1 暂定，转正）。理由：① 与 Decision 1 的「① 部署布局 / ② 发行组装」两层分离精神一致——xtask 的 `include` 解析器不应该知道"怎么 publish"，只应该知道"去哪拷"；直接 publish 会让每个 `[package.*]` 都重新携带一份"如何产出组件"的知识（apphost toml 路径、`Z42_APPHOST_TEMPLATE` 等），与 packages.toml 的组装-only 定位冲突。② byte-identical 硬验收更直接——sdk 与 runtime 从同一份已 publish 好的暂存子树逐字节拷贝，天然保证一致；直接 publish 两次虽然理论幂等，但仍是两次独立的产出动作，byte-identical 靠"信任 publish 无副作用"而非"物理上同一份文件"。③ 为将来 selector 式配置（packages-future-selector）铺路：组件只管把自己 publish 好，包配置只管选，两者解耦。
**代价**（已知晓、接受）：多一层暂存目录 + 一次额外拷贝；xtask 需要新写"从暂存根合并进包目录"的逻辑（当前完全不存在，3.2/3.3 的范围）。4.1a 现在的"直接 publish 进 pkgDir"实现是过渡态，4.1b 落地暂存根架构后需要改为"publish 进暂存根 + xtask 从暂存根拷贝"，`_packageDesktop` 的 `_z42bPublish` 调用点届时会相应调整（不是推倒重来——`_z42bPublish` 本身只是换一个 `--output` 目标，从 `pkgDir` 改成 `artifacts/publish/<comp>/`，再加一步 `_copyIfExists` 式的目录拷贝）。

## Implementation Notes

- `packages.toml` schema（Phase 1）：
  ```toml
  [package.sdk]
  artifact = "z42-{rid}"
  include  = ["z42vm", "native", "stdlib", "z42c", "launcher", "z42b"]
  manifest = "auto"     # [contents] 仍按组装后树自动 glob（现状机制不变）

  [package.runtime]
  artifact = "z42-runtime-{version}-{rid}"
  include  = ["native", "stdlib"]   # 纯嵌入式运行时，不含 z42c/z42vm（host 专属工具，2026-07-01 修订）
  ```
- `include` 名解析：apphost 类名（launcher/z42c/z42b/z42d）→ 该组件 publish 暂存子树（z42c 的暂存子树已含 6 个兄弟库，见 Decision 4）；稳定名（z42vm/native/stdlib）→ 固定 staging handler 的产物。runtime 包不 include z42vm/z42c——两者都是 host 平台工具，runtime 包可能跨 host 使用（如 android runtime 装在 Windows/macOS host 上），塞单一 host 平台意义的二进制没有意义；z42c 自举种子改由 SDK 包的 `programs/z42c/` 提供（见 self-hosting.md）。
- byte-identical：sdk 与 runtime 引用同一 `stdlib`/`native` 暂存源 → 同字节，现有 reuse-from-sdk 特例删除。
- 暂存根：`artifacts/publish/<comp>/`（Decision 6 已转正，非 Phase 1 暂定）。

## Testing Strategy

- 单元：`packages.toml` 解析 + `include` 解析 + `bin`/`payload` 默认推导（含特例覆盖）。
- 端到端：`xtask build package desktop release` 产出的 sdk 包树与改造前**逐文件/逐字节一致**（`_pkgSha256Check` + 目录 diff）；runtime 包同。这是本重构的硬验收（纯重构不得改产物）。
- GREEN：`z42 xtask.zpkg test`（全 stage）+ `test dist`（发行包验证）。

## Deferred / Future Work

### packaging-future-mobile: mobile 包配置化
- **来源**：本变更 Phase 1 划界
- **触发原因**：ios/android/wasm 有原生 facade + codesign + NDK，producer 更杂；且不含 toolchain apphost，无 z42d/z42i 压力
- **前置依赖**：Phase 1 暂存+packages.toml 机制稳定
- **触发条件**：mobile 打包再次需要加组件时

### packaging-future-selector: packages.toml 自动发现（全 selector）
- **来源**：探索期讨论
- **触发原因**：Phase 1 用显式 include（一处全看清）；selector（组件 toml 声明 `packages=[...]`，中央只写规则）可做到「加组件零中央编辑」，但牺牲可见性
- **触发条件**：apphost 数量多到显式 include 维护成本显现时
