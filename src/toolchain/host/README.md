# toolchain/host — 🚧 解散迁移中（consolidate-platform-into-workload）

> 本目录正在按 [consolidate-platform-into-workload](../../../docs/spec/changes/consolidate-platform-into-workload/) **解散**：平台相关的一切迁入 `workload/`，平台无关核心留 `runtime/`。本目录将在迁移 S5 移除。

## 内容已迁出

| 原内容 | 现位置 | 迁移步 |
|--------|--------|--------|
| `embed/`（Tier 2 `z42-host` crate）| [`../workload/host-api/`](../workload/host-api/) | S1 ✅ |
| `platforms/{ios,android,wasm,desktop}/`（Tier 3 facade + 测试）| [`../workload/platforms/`](../workload/platforms/) | S3' ✅ |
| Tier 1 C ABI + 头文件 | 始终在 [`../../runtime/src/host/`](../../runtime/src/host/) + [`../../runtime/include/`](../../runtime/include/)（不迁，runtime 留最小核）| — |

## 仍待迁

- 文档收口 + 本目录移除 → S5。
- 目标架构与门控模型见 [embedding.md](../../../docs/design/runtime/embedding.md) §3 代码归属 + [runtime-workload-distribution.md](../../../docs/design/toolchain/runtime-workload-distribution.md)。
