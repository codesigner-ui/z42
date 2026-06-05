# 标准库构建

标准库源码在 [`src/libraries/`](../../../src/libraries/)，22 个包（`z42.core` / `z42.io` / `z42.math` / `z42.text` / `z42.collections` / `z42.test` / `z42.toml` / `z42.json` / `z42.regex` / `z42.crypto` / `z42.net` / `z42.numerics` / … 完整列表见 `src/libraries/`）。每个 `.zpkg` 产物给 VM 加载。

## 何时需要重新编译

- 改了 `src/libraries/<lib>/src/*.z42`
- 改了 `src/libraries/<lib>/*.z42.toml`
- zbc / zpkg 格式 bump（compiler 端 minor 升级）
- 编译器有 codegen / TypeChecker 行为变更

**注**：`z42 xtask.zpkg test vm` 默认会自动重建 stdlib，开发场景**不必手动**跑 `z42 xtask.zpkg build stdlib`。

## 直接构建

```bash
z42 xtask.zpkg build stdlib            # 编译全部 lib + 扁平视图 + index.json（dev 默认）
z42 xtask.zpkg test dist               # 用打包发行版 z42c 编译并验证（package 后测试用）
```

`build stdlib` 的逻辑现已 fold 进 xtask（`scripts/xtask_stdlib.z42`），一条命令做三件事：
(1) `z42c build --workspace --release` 编译 22 个 lib；(2) hard-link 成扁平视图
`artifacts/build/libraries/dist/release/`（VM 单目录加载点）；(3) 写 namespace→zpkg 的
`index.json`。核心编译步骤等价于：

```bash
( cd src/libraries && dotnet run --project ../compiler/z42.Driver -- build --workspace --release )
```

> **冷启动**：`xtask.zpkg` 本身依赖 stdlib 才能编译，所以冷树上先由 C# 编译器直接跑上面
> 这条 `build --workspace`（primer，无 z42 程序参与）产出 `.zpkg`/`.zsym` → 编译
> `xtask.zpkg` → 再 `z42 xtask.zpkg build stdlib` 补扁平视图 + `index.json`。z42c 只读
> `.zsym`、VM 扫目录，都不需要 `index.json` 即可编译/运行 xtask，故此次序无死锁。

## 增量编译（C5）

默认开启，第二次构建跳过未变文件：

```bash
( cd src/libraries && dotnet run --project ../compiler/z42.Driver -- build --workspace --release )                    # cached: N/N
( cd src/libraries && dotnet run --project ../compiler/z42.Driver -- build --workspace --release --no-incremental )   # 强制全量
```

机制基于 SHA-256 source hash + cache zbc 存在性 + ExportedModule 可用性三重检查；命中跳过 parse + typecheck + irgen。详见 [`docs/design/compiler/project.md`](../../design/compiler/project.md) L3 build 段。

## artifacts 布局

```
artifacts/build/libraries/
├── <lib>/release/dist/<lib>.zpkg   # 每个 lib 的 workspace 构建产物（+ .zsym）
├── <lib>/release/cache/*.zbc       # 增量编译中间产物
└── dist/release/                   # 扁平视图（hard-link）= VM 单目录加载点
    ├── <lib>.zpkg + <lib>.zsym     #   build stdlib hard-link 过来
    └── index.json                  #   namespace → zpkg 索引
```

`z42 xtask.zpkg build stdlib` 同时产每-lib 构建产物 + 扁平视图 + `index.json`；
`z42 xtask.zpkg build package` 在分发版打包阶段把扁平视图整份拷进 SDK 的 `libs/`。

## 分发链端到端

```bash
z42 xtask.zpkg build package release   # 1. 打 z42c + z42vm 到 artifacts/build/runtime/release/
z42 xtask.zpkg test dist               # 2. 用分发版 z42c 重编译 stdlib 并跑 goldens（interp + jit）
z42 xtask.zpkg test dist interp        # 仅 interp 模式
```

## 与 stdlib 内 `[Test]` 测试的关系

`z42 xtask.zpkg build stdlib` 只编译 lib 本身的源码（不跑测试）；stdlib 内 `[Test]` 测试由 [`../testing/stdlib-tests.md`](../testing/stdlib-tests.md) 描述的 `z42 xtask.zpkg test lib` + z42-test-runner 跑。
