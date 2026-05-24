# Spec: TCP sockets (K1)

## ADDED Requirements

### Requirement: TcpClient — sync blocking client

#### Scenario: Connect to listener and exchange bytes
- **WHEN** user `new TcpClient()` then `client.Connect("127.0.0.1", port)` where `port` 监听中
- **THEN** 返回成功；`client.GetStream()` 返回 `NetworkStream`，`Write` / `Read` 可正常往返字节

#### Scenario: Connect to closed port
- **WHEN** 目标端口没有 listener
- **THEN** `Connect` 抛 `SocketException` 包含 program-readable error code 与 host:port 信息

#### Scenario: GetStream after Dispose
- **WHEN** `client.Dispose()` 之后调用 `client.GetStream()`
- **THEN** 抛 `SocketClosedException`

#### Scenario: Repeated GetStream returns same instance
- **WHEN** 同一 `TcpClient` 多次调用 `GetStream()`
- **THEN** 返回**同一个** `NetworkStream` 对象引用（identity-equality）

### Requirement: TcpListener — sync blocking server

#### Scenario: Bind + Accept loopback connection
- **WHEN** `listener.Bind("127.0.0.1", 0)` (port 0 = OS 分配) + `listener.Start()` + 另一线程发 `TcpClient` 连接
- **THEN** `listener.AcceptTcpClient()` 阻塞返回新 `TcpClient`，可读写

#### Scenario: LocalPort exposed after Bind
- **WHEN** `Bind("127.0.0.1", 0)` 后查 `listener.LocalPort`
- **THEN** 返回 OS 实际分配的非零端口

#### Scenario: Stop releases port
- **WHEN** `listener.Stop()` 后再次 `Bind` 同端口
- **THEN** 成功（前一个 listener 已释放）

#### Scenario: Accept after Stop
- **WHEN** `listener.Stop()` 后调用 `AcceptTcpClient()`
- **THEN** 抛 `SocketClosedException`

### Requirement: NetworkStream extends Std.IO.Stream

#### Scenario: Read returns 0 on remote close (EOF)
- **WHEN** peer 关闭连接，stream `Read(buf, 0, n)` 调用
- **THEN** 返回 0（EOF）；后续 Read 持续返回 0；不抛异常

#### Scenario: Write after peer close
- **WHEN** peer 关闭，stream `Write(buf, 0, n)`
- **THEN** 抛 `SocketException`（broken pipe / connection reset）

#### Scenario: CanRead / CanWrite / CanSeek
- **WHEN** 已连接 NetworkStream
- **THEN** `CanRead() == true`, `CanWrite() == true`, `CanSeek() == false`

#### Scenario: Seek throws
- **WHEN** 调用 `stream.Seek(0, SeekOrigin.Begin)`
- **THEN** 抛 `NotSupportedException`（继承自 `Stream` base 默认）

#### Scenario: Close after Dispose 幂等
- **WHEN** 同一 NetworkStream 上重复 `Close()` / `Dispose()`
- **THEN** 不抛；只第一次真正关闭 fd

### Requirement: Resource cleanup (IDisposable)

#### Scenario: Dispose closes underlying fd
- **WHEN** `TcpClient.Dispose()` 或 `TcpListener.Dispose()`
- **THEN** 底层 socket fd 关闭，slot 从 `VmContext.tcp_sockets` / `tcp_listeners` 移除

#### Scenario: GC drop closes fd
- **WHEN** 未 Dispose 的 `TcpClient` 被 GC
- **THEN** 走 `__net_tcp_socket_drop` finalizer，等价 Dispose 路径

### Requirement: wasm32 unsupported gating

#### Scenario: TcpClient on wasm32
- **WHEN** `new TcpClient()` 或 `TcpListener` 在 wasm32 target 运行
- **THEN** 抛 `NetUnsupportedException` 而非 panic / segfault

## IR Mapping

无新 IR 指令。所有 socket 操作走现有 `[Native(...)]` extern → `Call.Native` 既有路径。

## Pipeline Steps

受影响的 pipeline 阶段：
- [ ] Lexer — 无变化
- [ ] Parser / AST — 无变化
- [ ] TypeChecker — 无变化（用现有类 + extern method）
- [ ] IR Codegen — 无变化
- [x] VM interp — 新增 `__net_tcp_*` × 7 builtin handler + slot table 操作

## Native builtin contract（详 design.md）

| Builtin | Args | Returns | Errors (Value::Null/Array marker) |
|---------|------|---------|----------------------------------|
| `__net_tcp_connect` | (string host, int port) | i64 slot_id | tuple `[1, msg]` on connect fail |
| `__net_tcp_socket_read` | (i64 slot, byte[] buf, int off, int n) | i64 nbytes (0=EOF) | tuple `[1, msg]` on io error; `[2]` on closed |
| `__net_tcp_socket_write` | (i64 slot, byte[] buf, int off, int n) | i64 nbytes written | tuple `[1, msg]` / `[2]` |
| `__net_tcp_socket_drop` | (i64 slot) | null | null（idempotent） |
| `__net_tcp_listen` | (string host, int port) | tuple `[i64 slot, i64 actual_port]` | tuple `[1, msg]` on bind fail |
| `__net_tcp_accept` | (i64 listener_slot) | i64 socket_slot | tuple `[1, msg]` / `[2]` (stopped) |
| `__net_tcp_listener_drop` | (i64 slot) | null | null |

Convention 与 `ProcessHandle` builtins 完全一致：成功返回直接值或 tuple，失败返回 `[kind, ...payload]` Array，z42 端用 `is Array` discriminate。
