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

## Implementation Notes

- `packages.toml` schema（Phase 1）：
  ```toml
  [package.sdk]
  artifact = "z42-{rid}"
  include  = ["z42vm", "native", "stdlib", "z42c", "launcher", "z42b"]
  manifest = "auto"     # [contents] 仍按组装后树自动 glob（现状机制不变）

  [package.runtime]
  artifact = "z42-runtime-{version}-{rid}"
  include  = ["z42vm", "native", "stdlib", "z42c"]
  ```
- `include` 名解析：apphost 类名（launcher/z42c/z42b/z42d）→ 该组件 publish 暂存子树（z42c 的暂存子树已含 6 个兄弟库，见 Decision 4）；稳定名（z42vm/native/stdlib）→ 固定 staging handler 的产物。
- byte-identical：sdk 与 runtime 引用同一 `stdlib`/`native` 暂存源 → 同字节，现有 reuse-from-sdk 特例删除。
- 暂存根：`artifacts/publish/<comp>/`（Phase 1 暂定）。

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
