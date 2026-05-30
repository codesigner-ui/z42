# Proposal: HttpClient.SetAutoDecompress — wire brotli

## Why

`docs/design/stdlib/roadmap.md` Deferred 行 `net-future-http-compression`
当前状态 "(gzip 已落地 2026-05-27, brotli 仍延后)" — brotli 延后的前置
依赖 `compression-future-brotli` 已于 2026-05-27 落地（`Std.Compression.Brotli.Compress` /
`Decompress`，纯 Rust 实现含 wasm32），HttpClient 端只剩 wiring：

- `Accept-Encoding` header 加 `br`
- 响应 `Content-Encoding: br` 时调 `Brotli.Decompress` 替换 `resp.Body`

不做：调用方爬大量 brotli-encoded 公共 API（npm registry / cargo registry /
ghcr.io 等）时仍要手写 brotli 检测分支 — 与现有 `SetAutoDecompress(true)`
透明 gzip 的"开关即生效"约定不一致。

## What Changes

`src/libraries/z42.net/src/Http/HttpClient.z42`：

- `Accept-Encoding` set 从 `"gzip"` 改为 `"gzip, br"`（quality value 默认
  相等，按 server 偏好选）
- response decode 加 `enc == "br"` 分支调 `Brotli.Decompress(resp.Body)`
- 既有 gzip 分支不变；错误 wrapping (HttpException) 形态对齐

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.net/src/Http/HttpClient.z42` | MODIFY | Accept-Encoding 加 br + decode 分支 |
| `src/libraries/z42.net/tests/http_brotli_decode.z42` | NEW | brotli 响应解码单测 + Accept-Encoding 断言 |
| `docs/design/stdlib/net.md` | MODIFY | Deferred 段：brotli 标 ✅ |
| `docs/design/stdlib/roadmap.md` | MODIFY | Deferred 索引行注释更新 |

**只读引用**：

- `src/libraries/z42.compression/src/Brotli.z42`（API 已在）
- `src/libraries/z42.net/tests/http_compression.z42`（既有 gzip 测试模板）

## Out of Scope

- Streaming brotli decode（z42.compression v0 仅 one-shot；HttpClient body
  整 byte[] 消费已对齐）
- `Accept-Encoding` quality value 调优（如 `gzip;q=0.9, br;q=1.0`）— 默认
  equal-quality 由 server 决策即可
- 请求端 brotli encoding（client → server 发 brotli body）— 罕见用例
- `zstd` 作为 HTTP encoding（非标准，no broad server support）

## Open Questions

- [ ] 无：API 形态 `Accept-Encoding: gzip, br` 是事实标准（curl / Chrome / Firefox 都这么发），
      decode 路径与 gzip 完全对称。
