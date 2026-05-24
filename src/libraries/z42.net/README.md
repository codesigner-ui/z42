# z42.net — 网络库

## 职责

z42 标准网络类型。K1: TCP sockets only (sync blocking)。UDP / IPAddress / DNS / TLS / HTTP 走独立 follow-up spec。

## src/ 核心文件

| 文件 | 类型 | 说明 |
|------|------|------|
| `TcpClient.z42` | `TcpClient` | sync blocking TCP client (`Connect` / `GetStream` / `Close` / `Dispose`) |
| `TcpListener.z42` | `TcpListener` | sync blocking TCP server (`Bind` / `LocalPort` / `Start` / `AcceptTcpClient` / `Stop` / `Dispose`) |
| `NetworkStream.z42` | `NetworkStream` | extends `Std.IO.Stream`；read/write 字节经由 socket fd |
| `Exceptions/NetException.z42` | `NetException` | z42.net 异常基类 |
| `Exceptions/NetUnsupportedException.z42` | `NetUnsupportedException` | wasm32 / 未支持平台抛出 |
| `Exceptions/SocketException.z42` | `SocketException` | 连接 / 读写失败 |
| `Exceptions/SocketClosedException.z42` | `SocketClosedException` | 已关闭 socket 上的操作 |

## 入口点

- `Std.Net.Sockets.TcpClient`
- `Std.Net.Sockets.TcpListener`
- `Std.Net.Sockets.NetworkStream`

## 依赖关系

- `z42.core` — 基础类型 / 异常基类
- `z42.io` — `Std.IO.Stream` base class（`NetworkStream` 继承）

## Native 后端

所有 socket op 走 `__net_tcp_*` builtin，VM-side 实现位于
`src/runtime/src/corelib/network.rs`，slot table 在 `VmContext.tcp_sockets` /
`VmContext.tcp_listeners`，pattern 镜像 `Std.IO.ProcessHandle`。
