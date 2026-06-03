# Proposal: HTTPS for z42.net (add-z42-net-tls)

## Why
z42.net's HttpClient is HTTP-only (`HttpUrl.Parse` throws on `https://`). This
blocks `z42 install` (GitHub Releases are HTTPS) and any real-world client use.
Add client-side TLS so `HttpClient.Get("https://…")` works.

## What Changes
- **Runtime (Rust)**: add `rustls` (pure-Rust TLS, no OpenSSL) + bundled
  Mozilla roots (`webpki-roots`). New native builtins mirroring TCP:
  `__net_tls_connect(host, port, timeoutMs)` (TCP connect + rustls handshake,
  SNI=host, **cert verification ON**) → slot; `__net_tls_socket_read` /
  `_write` / `_set_read_timeout` / `_set_write_timeout` / `_drop`. New TLS
  slot table on VmCore (parallel to tcp sockets).
- **z42.net**: `HttpUrl.Parse` accepts `https` (default port 443). A
  TLS-backed stream (`TlsClient`/`TlsStream` over `__net_tls_*`, mirroring
  `TcpClient`/`NetworkStream`); `HttpClient._sendOnce` selects TCP vs TLS by
  scheme — the existing body-framing (`_buildBodyStream`) is reused.
- **Launcher**: re-enable `z42 install` over HTTPS (swap the curl-bridge plan
  back to `HttpClient`).

## Scope
| 文件 | 类型 | 说明 |
|------|------|------|
| `src/runtime/Cargo.toml` | MODIFY | rustls + webpki-roots deps |
| `src/runtime/src/corelib/tls.rs` | NEW | __net_tls_* builtins (connect/read/write/timeout/drop) |
| `src/runtime/src/corelib/mod.rs` | MODIFY | register __net_tls_* |
| `src/runtime/src/vm_context.rs` | MODIFY | TLS slot table + alloc/get/drop |
| `src/libraries/z42.net/src/Http/HttpUrl.z42` | MODIFY | accept https + port 443 |
| `src/libraries/z42.net/src/TlsClient.z42` / `TlsStream.z42` | NEW | z42 over __net_tls_* |
| `src/libraries/z42.net/src/Http/HttpClient.z42` | MODIFY | scheme → TCP/TLS select |
| `src/libraries/z42.net/tests/*.z42` | NEW | https smoke (real endpoint) |
| `docs/design/stdlib/net.md` | MODIFY | TLS section |

## Out of Scope
- TLS **server** (https listener); client certificates; custom CA / pinning;
  TLS for raw TcpClient users (only the HttpClient path + a TlsClient API).
- HTTP/2; SNI-less / IP-host TLS edge cases beyond basic.

## Open Questions
- [ ] roots: **webpki-roots**(打包 Mozilla 根,确定性、跨平台、无系统证书库依赖) vs `rustls-native-certs`(系统证书库)。本 spec 取 webpki-roots。
- [ ] rustls 加密后端:默认 `ring` vs `aws-lc-rs`。本 spec 取默认(ring,纯 Rust 构建友好)。
