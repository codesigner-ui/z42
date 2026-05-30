# Spec: z42.net — connect timeout + UDP timeout + REUSEADDR + KEEPALIVE

## ADDED Requirements

### Requirement: TcpClient.SetConnectTimeout

`TcpClient.SetConnectTimeout(int millis)` 记录到下一次 `Connect()` 时
使用。`millis <= 0` 视为"无 timeout / 长阻塞"。

#### Scenario: 成功 connect 在 timeout 之内

- **WHEN** `c.SetConnectTimeout(5000)`，然后 `c.Connect(localhost, port)`，
  对端正常 accept
- **THEN** Connect 正常返回，无异常

#### Scenario: connect 超过 timeout 抛 IOException

- **WHEN** `c.SetConnectTimeout(200)`，然后 `c.Connect(blackhole_addr, port)`
  （选不可路由地址 `203.0.113.1` 即 TEST-NET-3）
- **THEN** 200ms 后抛 `Std.IOException`，消息含 `timed out` / `timeout`

#### Scenario: 调用顺序错（先 Connect 后 Set）

- **WHEN** `c.Connect(...)`，再 `c.SetConnectTimeout(...)`
- **THEN** 不抛异常；记录值对**下一次** Connect 生效

### Requirement: TcpClient.SetKeepAlive

`TcpClient.SetKeepAlive(bool enable)` 在已连接 socket 上启用 / 禁用
SO_KEEPALIVE。仅在 `Connect()` 之后调用合法。

#### Scenario: 已连接 socket 设 keepalive

- **WHEN** TCP loopback 连上后 `c.SetKeepAlive(true)`
- **THEN** 调用正常返回；socket 仍可读写

#### Scenario: 未 connect 状态 set 抛 InvalidOperationException

- **WHEN** `c = new TcpClient()`（未 Connect），调用 `c.SetKeepAlive(true)`
- **THEN** 抛 `Std.InvalidOperationException`，消息含 `not connected`

### Requirement: TcpListener.SetReuseAddress

`TcpListener.SetReuseAddress(bool enable)` 记录到下一次 `Bind()` 用，
在 socket 创建后 bind 之前设 SO_REUSEADDR。`Bind()` 后再调抛
`InvalidOperationException`。

#### Scenario: pre-Bind 设 + Bind 成功

- **WHEN** `l.SetReuseAddress(true)`，然后 `l.Bind(localhost, 0)`
- **THEN** Bind 正常返回；`l.LocalPort()` 返回 OS 分配端口

#### Scenario: 同端口快速重启不抛 Address already in use

- **WHEN** 第一次 Bind/Stop 后立即再 Bind 同 port 且 `SetReuseAddress(true)`
- **THEN** 第二次 Bind 成功（不依赖 TIME_WAIT 窗口）

#### Scenario: post-Bind 设 SetReuseAddress 抛错

- **WHEN** `l.Bind(...)` 之后调 `l.SetReuseAddress(true)`
- **THEN** 抛 `Std.InvalidOperationException`，消息含 `already bound`

### Requirement: UdpClient.SetReadTimeout / SetWriteTimeout

`UdpClient.SetReadTimeout(int millis)` / `SetWriteTimeout(int millis)`
镜像 TcpClient 同名 API。`millis <= 0` 视为"无 timeout"。要求 socket
已 Bind（否则抛 InvalidOperationException）。

#### Scenario: Recv 超时抛 IOException

- **WHEN** `u.Bind(localhost, 0)`，`u.SetReadTimeout(200)`，没有 sender
  情况下 `u.Receive()`
- **THEN** 200ms 后抛 `Std.IOException`，消息含 `timed out` / `timeout`

#### Scenario: Send 正常完成不超时

- **WHEN** `u.SetWriteTimeout(5000)`，`u.Send(buf, len, peer, peerPort)`
  对 valid loopback peer
- **THEN** Send 正常返回

#### Scenario: 未 Bind 调 SetReadTimeout 抛错

- **WHEN** `u = new UdpClient()` 未 Bind，调 `u.SetReadTimeout(500)`
- **THEN** 抛 `Std.InvalidOperationException`，消息含 `not bound`

### Requirement: wasm32 stubs throw NetUnsupportedException

5 个新 builtin 在 wasm32 target 下走与既有 TCP/UDP 一致的兜底路径，
throw `Std.Net.NetUnsupportedException`。

## MODIFIED Requirements

### Requirement: TcpClient.Connect 走 timeout 分支

**Before:**

`Connect(host, port)` 调 `__net_tcp_connect(host, port)` 一律无 timeout。

**After:**

`Connect(host, port)` 若 `_connectTimeoutMs > 0`，调
`__net_tcp_connect_with_timeout(host, port, _connectTimeoutMs)`；
否则保持原 `__net_tcp_connect(host, port)` 路径。错误处理一致。

### Requirement: TcpListener.Bind 走 options 分支

**Before:**

`Bind(host, port)` 调 `__net_tcp_listen(host, port)`。

**After:**

`Bind(host, port)` 若 `_reuseAddress == true`，调
`__net_tcp_listen_with_options(host, port, true)`；否则保持原
`__net_tcp_listen(host, port)` 路径。

## IR Mapping

无新 IR — 纯 stdlib + corelib builtin 扩展。

## Pipeline Steps

- [ ] Lexer — N/A
- [ ] Parser / AST — N/A
- [ ] TypeChecker — N/A
- [ ] IR Codegen — N/A
- [ ] VM interp — N/A（builtin 注册即可）
