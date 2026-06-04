# 标准库构建

标准库源码在 [`src/libraries/`](../../../src/libraries/)，6 个包：`z42.core` / `z42.io` / `z42.math` / `z42.text` / `z42.collections` / `z42.test`。每个 `.zpkg` 产物给 VM 加载。

## 何时需要重新编译

- 改了 `src/libraries/<lib>/src/*.z42`
- 改了 `src/libraries/<lib>/*.z42.toml`
- zbc / zpkg 格式 bump（compiler 端 minor 升级）
- 编译器有 codegen / TypeChecker 行为变更

**注**：`z42 xtask.zpkg test vm` 默认会自动重建 stdlib，开发场景**不必手动**跑 `build-stdlib.sh`。

## 直接构建

```bash
./scripts/build-stdlib.sh              # 用 dotnet run 编译（dev 默认）
./scripts/build-stdlib.sh --use-dist   # 用 artifacts/build/runtime/release/z42c 编译（package 后测试用）
```

等价于：

```bash
( cd src/libraries && dotnet run --project ../compiler/z42.Driver -- build --workspace --release )
```

随后产物自动从 `artifacts/build/libraries/<lib>/dist/<lib>.zpkg` sync 到 `artifacts/build/libs/release/<lib>.zpkg`（VM 默认加载路径）。

## 增量编译（C5）

默认开启，第二次构建跳过未变文件：

```bash
( cd src/libraries && dotnet run --project ../compiler/z42.Driver -- build --workspace --release )                    # cached: N/N
( cd src/libraries && dotnet run --project ../compiler/z42.Driver -- build --workspace --release --no-incremental )   # 强制全量
```

机制基于 SHA-256 source hash + cache zbc 存在性 + ExportedModule 可用性三重检查；命中跳过 parse + typecheck + irgen。详见 [`docs/design/compiler/project.md`](../../design/compiler/project.md) L3 build 段。

## artifacts 布局

```
artifacts/
├── libraries/<lib>/             # workspace 模式 stdlib 构建产物
│   ├── dist/<lib>.zpkg          # 最终包
│   └── cache/*.zbc              # 中间产物（debug/indexed 模式时生成）
└── z42/libs/<lib>.zpkg          # VM 默认加载路径（build-stdlib.sh sync 过来）
```

`build-stdlib.sh` 同时写两端；`package.sh` 在分发版打包阶段额外保证一份最终拷贝（路径见 `./scripts/package.sh`）。

## 分发链端到端

```bash
./scripts/package.sh                  # 1. 打 z42c + z42vm 到 artifacts/build/runtime/release/
./scripts/build-stdlib.sh --use-dist  # 2. 用分发版 z42c 重编译 stdlib
z42 xtask.zpkg test dist                # 3. 跑分发版 binary 跑 goldens（interp + jit）
z42 xtask.zpkg test dist interp         # 仅 interp 模式
```

## 与 stdlib 内 `[Test]` 测试的关系

`build-stdlib.sh` 只编译 lib 本身的源码（不跑测试）；stdlib 内 `[Test]` 测试由 [`../testing/stdlib-tests.md`](../testing/stdlib-tests.md) 描述的 `z42 xtask.zpkg test lib` + z42-test-runner 跑。
