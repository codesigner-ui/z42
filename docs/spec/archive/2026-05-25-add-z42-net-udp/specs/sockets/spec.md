# Spec: UDP datagram sockets

## ADDED Requirements

### Requirement: UdpClient — sync blocking datagram socket

#### Scenario: Send + receive on loopback
- **WHEN** `clientA.Bind("127.0.0.1", 0)`, query `LocalPort()`, `clientB.Send(data, len, "127.0.0.1", portA)`
- **THEN** `clientA.Receive()` 返回 `UdpReceiveResult` 其中 Buffer 内容匹配 data 前 len 字节，RemoteHost == "127.0.0.1"，RemotePort == clientB.LocalPort()

#### Scenario: Bind port 0 → OS assigns
- **WHEN** `client.Bind("127.0.0.1", 0)`, then `client.LocalPort()`
- **THEN** 返回 OS 分配的非零端口

#### Scenario: Send without explicit bind
- **WHEN** new `UdpClient()` (未 Bind) 调 `Send(data, len, host, port)`
- **THEN** OS auto-binds 到 ephemeral 端口；Send 成功返回 bytes sent

#### Scenario: Receive blocks until datagram
- **WHEN** bound UdpClient 调 `Receive()`，无 incoming datagram
- **THEN** 调用阻塞；当 datagram 到达 → 返回 `UdpReceiveResult`

#### Scenario: Send returns bytes sent
- **WHEN** `client.Send(data, 100, host, port)` 成功
- **THEN** 返回 `100` (UDP 不分片，写入要么全成功要么 fail)

#### Scenario: Send to unroutable address throws
- **WHEN** `client.Send(data, len, "192.0.2.1", 65000)` — TEST-NET-1 reserved address
- **THEN** UDP send 不保证成功；macOS / Linux 行为各异。OK 路径：返回 bytes sent 即可，因为 UDP 无 connection ack。不需特别测试此 path。

#### Scenario: Use after Dispose throws
- **WHEN** `client.Dispose()`, then `client.Send(...)` / `client.Receive()` / `client.LocalPort()`
- **THEN** 抛 `SocketClosedException`

#### Scenario: Dispose is idempotent
- **WHEN** `client.Dispose(); client.Dispose();`
- **THEN** 第二次调用不抛

### Requirement: UdpReceiveResult — datagram + sender carrier

#### Scenario: 公开字段
- **WHEN** access `result.Buffer` / `result.RemoteHost` / `result.RemotePort`
- **THEN** Buffer 是收到的 datagram 完整字节 (`byte[]`，长度 == datagram size)，RemoteHost / RemotePort 是发送方地址

### Requirement: Wasm32 unsupported gating

#### Scenario: UDP on wasm32
- **WHEN** `new UdpClient()` 或任何方法在 wasm32 target 运行
- **THEN** 抛 `NetUnsupportedException`（不 panic / segfault）

## IR Mapping

无新 IR 指令。沿用既有 `Call.Native` 路径。

## Pipeline Steps

- [x] Lexer — 无变化
- [x] Parser / AST — 无变化
- [x] TypeChecker — 无变化
- [x] IR Codegen — 无变化
- [x] VM interp — 新增 4 个 `__net_udp_*` builtin

## Native builtin contract

| Builtin | Args | Returns |
|---------|------|---------|
| `__net_udp_bind` | (string host, int port) | `[0, slot, actual_port]` ok / `[1, msg]` err / `[3]` unsupported |
| `__net_udp_send` | (long slot, byte[] buf, int offset, int count, string host, int port) | `[0, bytes_sent]` ok / `[1, msg]` err / `[2]` invalid |
| `__net_udp_recv` | (long slot) | `[0, byte[] buf, string remote_host, long remote_port]` ok / `[1, msg]` err / `[2]` invalid |
| `__net_udp_drop` | (long slot) | `Value::Null` (idempotent) |

Convention 与 TCP K1 builtins 完全一致 — uniform `[kind, ...]` Array tuple，centralised decode through `UdpDecode` z42 helper.
