# 跨平台构建

> **状态**：📋 placeholder。完整文档随 SemVer **0.2.5** 多平台 CI matrix 上线。
>
> 平台**支持矩阵设计**见 [`docs/design/runtime/cross-platform.md`](../../design/runtime/cross-platform.md)。

## 当前可用

主开发 + 测试在 **macOS arm64**；其他平台理论可跑（dotnet / cargo 都跨平台），但未做 CI 验证。Windows 开发的具体路径（Git Bash / .NET MSI / Rust MSVC toolchain）见 [`../windows.md`](../windows.md)。

```bash
# 在任意 macOS / Linux / Windows 工作站上：
dotnet build src/compiler/z42.slnx                    # 编译器
cargo build --manifest-path src/runtime/Cargo.toml    # VM
```

## 本地打 9 RID per-arch SDK package（已落地，2026-05-13）

```bash
z42 xtask.zpkg package release --rid <rid>    # 9 个 RID 之一
z42 xtask.zpkg --help                               # RID 矩阵 + 选项
```

完整 RID 矩阵 + cross-host 规则 + 平台前置 + 验证 + 失败排查见 [`../packaging.md`](../packaging.md)。

## 0.2.5 之后

CI matrix 将覆盖：

| 平台 | 编译器 | VM | NativeAOT |
|------|:---:|:---:|:---:|
| macOS x86_64 | ✅ | ✅ | ✅ |
| macOS arm64 | ✅ | ✅ | ✅ |
| Linux x86_64 | ✅ | ✅ | ✅ |
| Linux arm64 | ✅ | ✅ | ✅ |
| Windows x86_64 | ✅ | ✅ | ✅ |
| Windows arm64 | ✅ | ✅ | ⚠️ 1.0-rc2 |
| WASM (wasm32-wasi) | — | ✅ VM only | — | 0.9.7 |

详细 schedule 与依赖见 [`docs/roadmap.md`](../../roadmap.md) "多平台支持矩阵"。

## NativeAOT

z42c 当前用 `dotnet run`；release 阶段（0.2.6+）由 NativeAOT 打成单文件 binary，无需 .NET runtime。详见 [`release.md`](../release.md)。

## WASM target

VM 编译为 wasm32-wasi 在 0.9.7 启用。届时本文将补完整流程（wasm-pack / wasmtime 等）。
