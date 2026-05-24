# Proposal: add z42.net (K1) — TCP sockets

## Why

z42 has no networking. Roadmap P1 calls for `z42.net` with sync TCP/UDP +
later HTTP. **K1 = TCP-only v0**：先把 `TcpClient` / `TcpListener` /
`NetworkStream extends Std.IO.Stream` 落地，后续 UDP / IPAddress / DNS /
HTTP 走独立 spec 叠加，不阻塞。

理由：

- Token / HTTP / RPC / DB driver 全部依赖 TCP，是 P1 网络栈最基础原语
- `std::net::TcpStream` 在 Rust std 内，desktop + iOS + Android 全部 free，
  无新 cdylib，无 cargo feature 切换 — wasm32 上 gate 掉返回 unsupported
- 复用既有 `ProcessHandle` slot-table pattern + `NetworkStream extends Stream`
  方案，无新 IR / 语法

## What Changes

- 新增 `Std.Net.Sockets.TcpClient` — sync blocking TCP client（`Connect(host, port)` / `GetStream() -> NetworkStream` / `Close()`）
- 新增 `Std.Net.Sockets.TcpListener` — sync blocking TCP server（`Bind(host, port)` / `Start()` / `AcceptTcpClient() -> TcpClient` / `Stop()`）
- 新增 `Std.Net.Sockets.NetworkStream extends Std.IO.Stream` — 双向 read/write，封装 socket fd
- 新增 VM-side native: `src/runtime/src/corelib/network.rs`，对应 `__net_tcp_*` builtin × 7
- 新增 slot table: `VmContext.tcp_sockets` + `VmContext.tcp_listeners` 镜像 `processes`
- IDisposable 协议：socket 资源 GC / Dispose 时关闭，无僵尸 fd
- wasm32 target: builtin 返回 `NetUnsupportedException`（不 panic，让 host 知道 stdlib 部分能力缺失）

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.net/z42.net.z42.toml` | NEW | 包 manifest（依赖 z42.core / z42.io） |
| `src/libraries/z42.net/src/TcpClient.z42` | NEW | TCP client + slot-handle |
| `src/libraries/z42.net/src/TcpListener.z42` | NEW | TCP listener + Accept 循环 |
| `src/libraries/z42.net/src/NetworkStream.z42` | NEW | extends `Std.IO.Stream` |
| `src/libraries/z42.net/src/Exceptions/NetException.z42` | NEW | base exception |
| `src/libraries/z42.net/src/Exceptions/NetUnsupportedException.z42` | NEW | wasm32 / 未实现平台抛 |
| `src/libraries/z42.net/src/Exceptions/SocketException.z42` | NEW | 连接 / 读写失败 |
| `src/libraries/z42.net/src/Exceptions/SocketClosedException.z42` | NEW | 已关闭 socket 操作 |
| `src/libraries/z42.net/tests/tcp_loopback.z42` | NEW | in-process echo: client ↔ listener round trip |
| `src/libraries/z42.net/tests/tcp_stream_io.z42` | NEW | `NetworkStream.Read`/`Write` + EOF / closed-socket 行为 |
| `src/libraries/z42.net/tests/tcp_disposal.z42` | NEW | Dispose + GC drop 不留 fd / 不抛 |
| `src/libraries/z42.net/README.md` | NEW | package README per code-organization.md |
| `src/libraries/z42.workspace.toml` | MODIFY | members 列表加 `z42.net` |
| `src/runtime/src/corelib/network.rs` | NEW | 7 个 `__net_tcp_*` builtin + slot table 管理 |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 静态注册 7 个 builtin 到 BUILTINS 表 |
| `src/runtime/src/vm_context.rs` | MODIFY | 加 `tcp_sockets` / `tcp_listeners` HashMap + slot allocator + remove helpers |
| `docs/design/stdlib/net.md` | NEW | z42.net design doc（K1 scope + Deferred K2/K3） |
| `docs/design/stdlib/roadmap.md` | MODIFY | z42.net 从 P1 待办移到"现状"段；加 Deferred 索引行 |
| `docs/design/stdlib/organization.md` | MODIFY | 现状表加 z42.net 行 |
| `docs/design/stdlib/overview.md` | MODIFY | Module Catalog 加 `Std.Net.Sockets.*` |
| `docs/roadmap.md` | MODIFY | 标准库基础段加一行 z42.net |

**只读引用**（理解上下文必须读，但不修改）：

- `src/libraries/z42.io/src/ProcessHandle.z42` — slot-handle pattern reference
- `src/libraries/z42.io/src/ProcessOutputStream.z42` / `ProcessStdinStream.z42` — Stream subclass pattern
- `src/runtime/src/corelib/process.rs` — slot table + builtin impl reference
- `src/runtime/src/corelib/mod.rs` BUILTINS array — registration pattern
- `src/runtime/src/vm_context.rs:533` `add_process_slot` — slot allocator pattern

## Out of Scope（独立 follow-up spec）

- **UDP** (`UdpClient`) — 留 `add-z42-net-udp` spec
- **IPAddress / IPEndPoint 类型** — K1 用 `(string host, int port)` 直接传，后续 spec 提升为 `IPEndPoint`
- **DNS** (`Dns.GetHostAddresses(host)`) — K1 倚赖 `TcpStream::connect("host:port")`，Rust std 内部自动 DNS resolve；K1 不暴露 DNS API
- **Async / non-blocking sockets** — 需 L3 async/await，独立 spec
- **TLS / HTTPS** — 需要 OpenSSL / rustls cdylib，独立 spec
- **HTTP client** — 需要 K1 + TLS 双前置，独立 spec
- **Socket options**（SO_REUSEADDR / SO_KEEPALIVE / Nagle / timeout）— K1 用 Rust std 默认值；用例驱动后续 spec
- **IPv6 explicit handling** — Rust `TcpStream::connect("host:port")` 自动支持双栈，K1 不暴露 v4/v6 选择
- **Timeout / Cancellation** — K1 无超时（阻塞调用），独立 spec 加 `SetReadTimeout` / `SetWriteTimeout`
- **wasm32 实现** — K1 在 wasm32 直接抛 `NetUnsupportedException`；后续 WASI sockets 进入 z42 dev env 后独立 spec

## Open Questions

- [ ] 接受 K1 仅 TCP（无 UDP、无 IPAddress、无 DNS API）的最小可用形态？User confirmed via "ok"
