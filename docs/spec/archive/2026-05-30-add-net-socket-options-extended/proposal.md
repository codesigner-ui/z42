# Proposal: z42.net — connect timeout + UDP timeout + SO_REUSEADDR + SO_KEEPALIVE

## Why

`docs/design/stdlib/roadmap.md` Deferred 段两条尚未落地：

1. **`net-future-timeout`** — TCP `SetReadTimeout` / `SetWriteTimeout`
   于 2026-05-27 落地；但 **TCP connect timeout** 与 **UDP read / write
   timeout** 仍缺失。任何 production-grade TCP client（防止恶意 peer
   "握手时无限拖"）和任何 UDP 接收循环（无 timeout 就 `Recv` 永远阻塞）
   都受影响。
2. **`net-future-socket-options`** — Nagle + IP_TTL 已落地；但 **SO_REUSEADDR**
   （server 重启避免 `Address already in use` 的 TIME_WAIT 残留）和
   **SO_KEEPALIVE**（long-lived TCP 连接探活、对端死机检测）仍延后。

两项各自的 v0 实现需要的"setsockopt 跨平台抽象"是同一个：用 `socket2`
crate 一次接入解决 UNIX / Windows 的差异。合并成一个 spec 既能复用
crate 引入成本，也避免两次小改 z42.net 包结构。

不做会怎样：

- TcpClient 调用方写不了正确的 connect-with-timeout 模式，只能 spawn
  thread + Join 自己实现，丑且易泄漏
- UdpClient.Recv 不能避免无限阻塞，所有 UDP 设计被迫加外层 Thread + Join
  workaround
- Server 重启在 TIME_WAIT 窗口内被拒，pkill + sleep 的脚本反模式蔓延
- 长连接 TCP 没有 keep-alive 探活，对端断电后僵尸连接保留 hours

## What Changes

### z42.net 新增 5 个 stdlib API

| 类 | 新方法 | 行为 |
|----|-------|------|
| `TcpClient` | `SetConnectTimeout(int millis)` | 记录到下一次 `Connect()` 用 |
| `TcpClient` | `SetKeepAlive(bool enable)` | SO_KEEPALIVE on 当前已连接 socket |
| `TcpListener` | `SetReuseAddress(bool enable)` | 记录到下一次 `Bind()` 用 |
| `UdpClient`  | `SetReadTimeout(int millis)` | UDP recv timeout |
| `UdpClient`  | `SetWriteTimeout(int millis)` | UDP send timeout |

`millis <= 0` 含义统一："no timeout / blocking forever"（与现有 TCP
timeout API 行为一致）。

### corelib 新增 5 个 builtin

| Builtin | 实现 |
|---------|------|
| `__net_tcp_connect_with_timeout(host, port, millis)` | `TcpStream::connect_timeout(addr, dur)` |
| `__net_tcp_socket_set_keepalive(slot, enable)` | `socket2::SockRef::from(&stream).set_tcp_keepalive(…)` |
| `__net_tcp_listen_with_options(host, port, reuse_addr)` | `socket2::Socket::new` + `set_reuse_address` + `bind` + `listen` → `into()` `std::net::TcpListener` |
| `__net_udp_set_read_timeout(slot, millis)` | `UdpSocket::set_read_timeout(dur)` |
| `__net_udp_set_write_timeout(slot, millis)` | `UdpSocket::set_write_timeout(dur)` |

wasm32 stub 路径添加 throw `NetUnsupportedException` 兜底，镜像现有
TCP/UDP 模式。

### 依赖变更

`src/runtime/Cargo.toml` 添加 `socket2 = "0.5"`（Rust networking
working group 维护，仅依赖 libc on Unix；同时为 Windows 提供 winsock
绑定），用于 SO_REUSEADDR / SO_KEEPALIVE 的跨平台 setsockopt。

### 文档同步

- `docs/design/stdlib/net.md` — 把两条 Deferred 项标 ✅ 已落地，附 spec 引用
- `docs/design/stdlib/roadmap.md` — Deferred Backlog Index 删除 `net-future-timeout`
  和 `net-future-socket-options` 两条索引行（或保留并加 ✅ 注释）

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/Cargo.toml` | MODIFY | add `socket2 = "0.5"` to base + target.'cfg(not wasm32)' deps |
| `src/runtime/src/corelib/network.rs` | MODIFY | 5 个新 builtin + 1 个 wasm stub patch |
| `src/runtime/src/corelib.rs` | MODIFY | 注册 5 个新 builtin name 到 `register_corelib_builtins` |
| `src/libraries/z42.net/src/TcpClient.z42` | MODIFY | `_connectTimeoutMs` 字段 + `SetConnectTimeout` / `SetKeepAlive` + `Connect` 走 timeout 分支 |
| `src/libraries/z42.net/src/TcpListener.z42` | MODIFY | `_reuseAddress` 字段 + `SetReuseAddress` + `Bind` 走 options 分支 |
| `src/libraries/z42.net/src/UdpClient.z42` | MODIFY | `SetReadTimeout` / `SetWriteTimeout` |
| `src/libraries/z42.net/tests/tcp_connect_timeout.z42` | NEW | TCP connect 超时单测 |
| `src/libraries/z42.net/tests/tcp_keepalive_reuseaddr.z42` | NEW | SO_KEEPALIVE + SO_REUSEADDR 单测 |
| `src/libraries/z42.net/tests/udp_timeout.z42` | NEW | UDP read/write 超时单测 |
| `docs/design/stdlib/net.md` | MODIFY | Deferred → ✅ 已落地；API surface 表补 5 个新方法 |
| `docs/design/stdlib/roadmap.md` | MODIFY | Deferred Backlog Index 标 ✅ + spec 名 |

**只读引用**：

- `src/runtime/src/corelib/network.rs` 既有 `set_read_timeout` / `set_nodelay`
  / `set_ttl` builtins（参考模板）
- `src/libraries/z42.net/src/TcpClient.z42` 既有 `SetReadTimeout` 写法
- `src/libraries/z42.net/tests/tcp_disposal.z42` 既有 TCP 测试 fixture 模板

## Out of Scope

- TCP / UDP `SetReuseAddress` 在 connected socket / bound listener 之后再调
  （仅支持 pre-Bind / pre-Connect 设定；BCL 同语义）
- SO_KEEPALIVE 的细粒度 `(idleSec, intervalSec, probes)` 调优（OS 默认即可；
  细粒度 setsockopt 各 OS 差异大，留 follow-up `net-future-keepalive-tuning`）
- TcpClient 的 `SetReuseAddress`（outgoing 客户端少见用例；如需可后续 spec）
- TcpClient `SetConnectTimeout` 在 `Connect()` 已被调过后再 reset（要求顺序：
  Set 在前 Connect 在后；二次 Connect 复用同一 timeout 值；不抛错）
- TLS / HTTPS（`net-future-tls` 独立 spec）
- Async/await timeouts（L3 async/await 语法依赖）

## Open Questions

- [ ] 无：API 形态对齐 .NET BCL `TcpClient.ReceiveTimeout` / `SendTimeout` /
      `SocketOptionLevel.Socket / KeepAlive` / `ExclusiveAddressUse` 同语义，
      不需要新决策。`socket2` 引入也是行业标准。
