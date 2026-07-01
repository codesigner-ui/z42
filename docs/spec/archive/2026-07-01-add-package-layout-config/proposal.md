# Proposal: 配置驱动的发行包布局（publish 暂存 + packages.toml 选取）

## Why

每发布一种包（sdk / runtime / workload …），其内容由 6 个手写函数硬编码
（`_packageDesktop` / `_buildRuntimePackage` / `_buildDesktopWorkload` /
`_packageIos` / `_packageAndroid` / `_packageWasm`）。每加一个 apphost
（z42b，现在 z42d / z42i）都要在 `_packageDesktop` 里手写同一串三步舞
「z42c 编译 zpkg → 建 `programs/z42x/` + 拷 zpkg → 造 apphost 指向 payload」，
还可能要同步改 runtime/mobile 脚本。**加组件＝改打包代码**，重复、易漏、易漂移。

stdlib（glob `libs/*.zpkg`）和 z42c 七成员（读 `z42.workspace.toml`）已是声明式发现，
证明「配置驱动」可行；痛点只剩 toolchain apphost 这一段仍是命令式。

## What Changes

把打包拆成两个互不重叠的职责：

1. **publish 暂存**：每个可发布组件 publish 到一个**与包内最终布局一致**的暂存子树
   （如 `artifacts/publish/z42d/` 内含 `bin/z42d` + `programs/z42d/z42.devtools.zpkg`）。
   组件「我是 apphost / 我的 payload 放哪」继续由自己 `z42.toml` 决定（复用现有
   `[platform.desktop] apphost = true`，按约定推导布局）。publish 负责编译 + 依赖拓扑 + 造 apphost。
2. **packages.toml 选取**：新增中央 `packages.toml`，每个包只声明**包含哪些组件**
   （`include = [...]`）+ artifact 名模板。xtask 打包＝把被选组件的暂存子树合并拷入包根 + emit manifest。

**加 z42d ＝ 在 `packages.toml` 的 `sdk.include` 加一个 `"z42d"`；xtask 零改动。**

不变量副产品：sdk 与 runtime 都从同一暂存源拷 stdlib → 天然 byte-identical，删去现有
「runtime 从 sdk 复用 libs 保一致」的特例逻辑。

### 设计目标：部署布局是面向用户的通用机制（非 z42 内部捷径）

第 1 层（部署布局）将来直接开放给用户发布自己的 app —— 用户的 app 与 z42 自己的
z42c/z42b/z42d **共用同一套 `[platform.desktop]` 旋钮**。因此布局词汇必须通用、自洽、有用户文档，
不能是只服务 SDK 组装的一次性 hack。

apphost 应用天生两部分（原生启动器二进制 + payload zpkg）。布局用两个路径字段表达，各指一处：

```toml
[platform.desktop]
apphost = true
bin     = "bin/z42d"          # 原生 apphost 二进制落哪（相对部署/包根）
payload = "programs/z42d/"    # payload zpkg(+兄弟依赖)落哪
```

- `bin` ↔ `payload` 的**相对路径自动推导**（apphost 内嵌 payload 路径 = 从 bin 到 payload 的相对路径）。
- **约定默认**：未写则 `bin/<name>` + `programs/<name>/`，多数组件零配置；特例（根 `z42` launcher →
  `bin = "z42"` 落根 + `payload = "programs/launcher/"`）才显式覆盖。

**两层职责**：① 部署布局（用户面，组件 `z42.toml`，publish 据此产自洽子树）；
② 发行组装（z42 内部，`packages.toml`，选组件子树合并成 SDK）。用户发单 app 只碰 ①。

## Scope（允许改动的文件）

> 本变更 **Phase 1 只覆盖 desktop SDK + runtime 两个包**（承载 toolchain apphost、是痛点所在）。
> mobile（ios/android/wasm）打包暂不动，列 Deferred（它们不含 toolchain apphost，无 z42d/z42i 压力）。

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `scripts/package/packages.toml` | NEW | 中央包布局描述：每包 `include = [组件]` + artifact 名模板 + manifest 模式 |
| `scripts/package/xtask_packages_config.z42` | NEW | 读 `packages.toml` + 解析 include 列表的小模块 |
| `scripts/package/xtask_package.z42` | MODIFY | 打包分发改为「按 packages.toml 选组件 → 从暂存树拷」；保留稳定 staging 产物（cargo bin/native、stdlib glob、z42c 成员）作固定 staging step |
| `scripts/package/xtask_package_desktop.z42` | MODIFY | `_packageDesktop` / `_buildRuntimePackage` 改为消费暂存树 + packages.toml；删 apphost 三步舞硬编码 + runtime 的 reuse-from-sdk 特例 |
| `src/toolchain/launcher/core/launcher_export.z42` | MODIFY | `_cmdPublishDesktop` 扩展为可输出「包布局子树」（apphost 落 `bin/`，payload zpkg 落 `programs/<stem>/`），由 `--stage-layout` 或约定驱动；保持现有单 apphost 输出行为兼容默认 |
| `src/libraries/z42.project/src/DesktopConfig.z42` | MODIFY | `[platform.desktop]` 增可选 `bin` + `payload` 两个路径字段（apphost 二进制位置 / payload 目录），默认按约定 `bin/<name>` + `programs/<name>/` 推导；用户面通用旋钮 |
| `src/libraries/z42.project/src/ManifestLoader.z42` | MODIFY | 解析 `bin` / `payload` 字段 |
| `docs/design/compiler/project.md` | MODIFY | 用户面文档：`[platform.desktop]` 的 `bin`/`payload` 部署布局字段说明 + 默认约定 + 示例 |
| `docs/design/toolchain/packaging.md` | NEW | 实现原理：暂存布局约定、packages.toml schema、producer 边界、与 publish/build 的关系 |
| `src/toolchain/README.md` | MODIFY | 指向 packaging.md + packages.toml |
| `scripts/package/tests/packages-config/` | NEW | packages.toml 解析 + include 解析的单元测试（z42c.* 不适用，放 script 级 dogfood 或 xtask 自测） |
| `src/toolchain/devtools/core/z42.devtools.z42.toml` | MODIFY | 补 `bin`/`payload` 显式布局 + 登记进 `packages.toml`（2026-07-01 User 裁决扩展 Scope，见下）|
| `src/toolchain/interactive/core/z42.interactive.z42.toml` | MODIFY | 同上 |
| `src/toolchain/devtools/README.md` | MODIFY | 状态段同步"已打包"（子命令仍未实现）|
| `src/toolchain/interactive/README.md` | MODIFY | 同上 |

**只读引用**：
- `scripts/build/xtask_stdlib.z42` — 理解 `_compilerMembers` / `_libsDir` / 暂存源路径
- `src/toolchain/builder/core/z42.builder.z42.toml`、`launcher/` 的 toml — 理解组件声明形态
- `docs/spec/archive/2026-06-30-gate-apphost-on-config/` — apphost gate 的既有约定

## Out of Scope

- mobile（ios/android/wasm）包改造 → Deferred（见 packaging.md）
- packages.toml 自动从组件 toml 反向发现（全 selector 模式）→ Deferred（Phase 1 用显式 include，简单可见）
- CI release 编排（`xtask_release.z42` / release-index.json）不变

### Scope 扩展记录（2026-07-01）

原定「把 z42d / z42i 真正登记进 build/workspace 是独立变更」（见 tasks.md 备注）被 User
显式裁决推翻：User 要求现在就把 devtools/interactive 注册进 `packages.toml` 并打进 SDK
包，接受两者仍是纯占位（子命令/入口只打印 "planned"）的现状。已据此把上面两个 toml +
两个 README 纳入本变更 Scope（原只读引用升级为 MODIFY）。

## Open Questions

- [ ] `packages.toml` 落 `scripts/package/`（贴近消费者 xtask）还是更顶层？暂定前者。
- [ ] publish 输出包布局子树：扩 `_cmdPublishDesktop` 本身，还是 xtask 加一层 staging 包装调用 publish？（design.md Decision 2）
