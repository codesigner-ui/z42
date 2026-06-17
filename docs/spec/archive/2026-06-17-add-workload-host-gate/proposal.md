# Proposal: add-workload-host-gate

> 状态：DRAFT（2026-06-17）。落地 B2-4 预留的 `workloads.<wl>.host` 字段：装 workload 前校验 host RID。
> 子系统锁：`toolchain`。设计已在 [runtime-workload-distribution.md](../../../design/toolchain/runtime-workload-distribution.md)（manifest schema 注 + Decision）+ launcher-command-dispatch.md 预定。

## Why

B2-4 的 manifest schema 把 `workloads.<wl>.host` 列为**保留字段，未实施**——当前任何 host 都能联网装任何
workload。但 ios workload 需要 macOS + Xcode 才能 `swift build` / `export ios`；在 Linux/Windows 上装它
只会得到一份不可用的 tooling，错误推迟到 build 时才暴露、信息也差。装前用 manifest 的 `host` 列表 gate，
失败即给清晰错误（对齐 `dotnet workload`：不支持的平台直接拒）。

## What Changes

1. **CI release.yml**：每个 `workloads.<wl>` 加 `host` 字段（host RID 列表 / `["*"]` 通配）：
   - `ios` → `["macos-arm64"]`（iOS 仅 macOS 可构建）
   - `android` → `["macos-arm64","linux-x64","linux-arm64","windows-x64"]`（任意桌面 + NDK）
   - `wasm` → `["*"]`（任意 host）
2. **launcher**：`_fetchWorkloadEntry` 一并返回 host 列表；联网装前 `_hostRid()` 不在列表（且非 `"*"`）→ 报清晰错误退出，不下载。

## Scope（允许改动的文件）

| 文件 | 变更 | 说明 |
|------|------|------|
| `.github/workflows/release.yml` | MODIFY | `workloads.<wl>` 加 `host` 字段（jq + 文档值） |
| `src/toolchain/launcher/core/launcher_network.z42` | MODIFY | `_fetchWorkloadEntry` 返回追加 host 列表；加 `_hostAllowed(string[] hosts)` |
| `src/toolchain/launcher/core/launcher_workload.z42` | MODIFY | 联网装解析 host → gate；不通过则拒 |
| `docs/design/toolchain/runtime-workload-distribution.md` | MODIFY | host 字段从「保留未实施」改为「已实施」；Deferred 摘除 host gate 条目 |

**只读引用**：`_hostRid()`（launcher_network.z42）。

## Out of Scope

- 本地 `--from` 装的 host 校验（用户显式指定本地包 = 知道自己在做什么；manifest 无 host 时不 gate）。
- `host` 的细粒度版本/特性矩阵（abi-version 维度）——后续。
- `--force` 跳过 gate——暂不加（YAGNI；需要再说）。

## 验证

本地 mock manifest：android workload 标 `host=["macos-arm64"]`（人为收窄）→ 在本机（macos-arm64）装应**放行**；
把 host 改成 `["linux-x64"]` → 同机装应**被拒**（清晰错误、不下载）；`host=["*"]` → 放行。复用 B2-4 的
mock 服务器手法。CI 改动 correct-by-construction（jq 干跑）。

## Open Questions

- 无（host 值表已在设计文档定；通配符 `"*"` 语义明确）。
