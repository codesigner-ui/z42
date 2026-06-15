# CI 工作流

z42 CI 在 GitHub Actions 运行，配置见 [`.github/workflows/`](../../.github/workflows/)。

## 当前 workflow

| 文件 | 触发 | 内容 |
|------|------|------|
| [`ci.yml`](../../.github/workflows/ci.yml) | push / PR | bootstrap（compiler + runtime + stdlib + xtask.zpkg）→ `z42 xtask.zpkg test all`（build + test 全套）|
| [`bench-update.yml`](../../.github/workflows/bench-update.yml) | 定时 / 手动 | 跑 benchmark + 更新 baseline |
| [`release.yml`](../../.github/workflows/release.yml) | tag / 手动 | 跨平台 SDK 包产出 |

[`.github/workflows/README.md`](../../.github/workflows/README.md) 是当前 CI 入口说明。

## GREEN 标准

任何 commit / PR 要 merge 必须满足下列全部条件（见 [`.claude/rules/workflow.md`](../../.claude/rules/workflow.md) 阶段 8）：

```bash
dotnet build src/compiler/z42.slnx                    # 无编译错误
cargo build --manifest-path src/runtime/Cargo.toml    # 无编译错误
dotnet test src/compiler/z42.Tests/z42.Tests.csproj   # 100% 通过
./xtask test vm                                       # 100% 通过（interp + jit 双模）
./xtask test stdlib                                   # 100% 通过
./xtask test cross-zpkg                               # 100% 通过
```

简化入口：

```bash
./xtask test      # build + test 全套
```

任何测试失败（含 pre-existing）都不得 commit / push —— 见 workflow.md "禁止行为"。

## GREEN 演进时间表

随版本升级，新增 CI 项进入 GREEN 标准。完整时间表见 [`docs/roadmap.md`](../roadmap.md) "GREEN 标准演进" 段。摘要：

| 起始版本 | 新增 GREEN 项 |
|:------:|------|
| 当前 | dotnet build + cargo build + dotnet test + z42 xtask.zpkg test vm |
| 0.2.3 | Perf CI（≥10% 退化阻塞）|
| 0.2.5 | 多平台 CI matrix |
| 0.4.6 | `z42c test` 100% 通过 |
| 0.4.7 | `z42c bench --diff` |
| 0.5.7 | LSP conformance |
| 0.8.6 | 多线程压力 / TSan |
| 0.8.7 | DAP conformance |
| 0.9.7 | WASM target |
| 0.10.0 | philosophy §9 五指标基线 |
| 1.0.0 | 自举 byte-identical + 跨架构 perf |

## 多平台 CI matrix

`ci.yml` 已在 4-OS matrix 上跑 build + test：`ubuntu-latest` / `ubuntu-24.04-arm` / `macos-15` / `windows-latest`。详见 [`building/cross-platform.md`](building/cross-platform.md)。

## Release 自动化

git tag → 跨平台 binary 自动产出在 0.2.6 启用。详见 [`release.md`](release.md)。
