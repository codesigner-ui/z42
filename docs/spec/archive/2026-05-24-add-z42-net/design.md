# Design: z42.net K1 — TCP sockets

## Architecture

```
┌───────────────────────────────────────────────┐
│ Z42 USER CODE                                 │
│   var c = new TcpClient();                    │
│   c.Connect("example.com", 80);               │
│   var s = c.GetStream();                      │
│   s.Write(bytes, 0, n);                       │
│   var read = s.Read(buf, 0, 4096);            │
└──────────────────────┬────────────────────────┘
                       │ (z42 method dispatch)
┌──────────────────────▼────────────────────────┐
│ STD.NET.SOCKETS (z42 source)                  │
│   TcpClient { long _slotId; }                 │
│   TcpListener { long _slotId; }               │
│   NetworkStream : Stream { long _slotId; }    │
│   ──► TcpClientNative.__net_tcp_*  (extern)   │
└──────────────────────┬────────────────────────┘
                       │ (extern → BUILTINS dispatch)
┌──────────────────────▼────────────────────────┐
│ RUNTIME: src/runtime/src/corelib/network.rs   │
│   __net_tcp_connect   ──► std::net::TcpStream │
│   __net_tcp_listen    ──► std::net::TcpListener
│   __net_tcp_accept    ──► listener.accept()   │
│   __net_tcp_socket_*  ──► slot table lookup   │
│                                               │
│   VmContext {                                 │
│     tcp_sockets:   HashMap<u64, TcpStream>    │
│     tcp_listeners: HashMap<u64, TcpListener>  │
│   }                                           │
└───────────────────────────────────────────────┘
```

## Decisions

### Decision 1: In-VM corelib（不走 native-ext cdylib）

**问题**：z42.compression 走 out-of-VM cdylib（`libz42_compression.so`）；z42.net 也应该这样吗？

**选项**：
- A — In-VM `corelib/network.rs`，直接 `std::net::*`
- B — Out-of-VM cdylib `libz42_net.so`，走 `native::ext` loader

**决定**：A。理由：
- `std::net` 是 Rust std lib 一部分，已经 link 进 z42vm — 多加一个 cdylib 反而增 binary size
- compression 走外置是因为 flate2 / zstd 是重 dep（编译慢、体积大）；std::net 完全相反
- 减少 native-ext 加载路径分支，简化 wasm32 gate（直接 `#[cfg]` 整个 module 即可）

### Decision 2: Slot table 复用 ProcessHandle pattern

**问题**：socket fd 的 z42 端表示？

**选项**：
- A — `long slotId` opaque handle，VmContext 持有真实 fd
- B — 暴露原 fd 给 z42 端
- C — z42 端持 GC-managed wrapper object

**决定**：A，与 `ProcessHandle` 完全一致。理由：
- 已有成熟 pattern，VmContext.processes slot table + add_*_slot / remove_*_slot helper 一对一镜像
- z42 端不需要看到 fd 数字（避免 fd-as-int 误用 / 跨 VM 实例混淆）
- Drop / Dispose / GC 路径统一走 `__net_tcp_*_drop` builtin

### Decision 3: 同步阻塞（无 timeout、无 async）

**问题**：socket I/O 阻塞行为？

**选项**：
- A — 完全同步阻塞，无超时
- B — 默认有 30s 超时
- C — async/await（依赖 L3）

**决定**：A。理由：
- 与 `ProcessHandle` 阻塞 `.Wait()` / `.ReadStdout()` 一致 — stdlib 当前阻塞模型
- 超时是独立 follow-up spec（`add-z42-net-timeout`），需要新增 `SetReadTimeout` / `SetWriteTimeout` API
- async 依赖 L3 — z42 当前 L2 阶段

### Decision 4: Address 用 `(string host, int port)` 而非 `IPEndPoint`

**问题**：API 接受地址的形态？

**选项**：
- A — `(string host, int port)` — Rust std `TcpStream::connect("host:port")` 内部 DNS
- B — `IPAddress` / `IPEndPoint` 强类型对象
- C — 单一 `string "host:port"`

**决定**：A。理由：
- K1 最小可用形态 — 引入 `IPEndPoint` 需要先定义 `IPAddress` 不可变值类型 + parser + IPv4/IPv6 分发，太膨胀
- BCL 风格但 BCL 也有 `TcpClient(string host, int port)` 重载兼存
- 后续 K2 可以加 `IPEndPoint` overload 不破坏 K1 API

### Decision 5: NetworkStream 实例化策略

**问题**：`client.GetStream()` 每次返回新对象还是 cached singleton？

**选项**：
- A — 每次返回新 wrapper（独立 _closed 状态）
- B — Lazy + cached（与 `ProcessHandle.GetStdoutStream` 一致）

**决定**：B，复用 `ProcessHandle.GetXxxStream()` lazy-cached pattern。理由：
- ProcessHandle 这样做的原因（caller 可 identity-compare + 一致的 _closed bit）对 socket 同样适用
- 避免多个 NetworkStream 各自 close — 一次 close 全局生效，符合 socket 语义

### Decision 6: Wasm32 fallback policy

**问题**：wasm32 target 上调用 socket API 怎么办？

**选项**：
- A — VM 启动时 panic（"unsupported target"）
- B — Builtin 注册成 stub，调用时 throw `NetUnsupportedException`
- C — 自动 fallback 到 wasi-sockets（依赖 unstable WASI feature）

**决定**：B。理由：
- C 不成熟（wasi-sockets 还 unstable，主流 wasm runtime 没 ship）
- A 太粗暴 — 用户可能只用部分 stdlib，不该因为 net 不可用 hard-crash
- B 允许 wasm32 用户 try/catch 探测 + 走 host JS interop fallback，与 future K2/K3 wasm 路径平滑

### Decision 7: Bind port 0 → 用 `[slot, actual_port]` tuple 返回

**问题**：`TcpListener.Bind("127.0.0.1", 0)` 时 OS 分配 port，如何把分配的 port 透出给 z42 端？

**选项**：
- A — `__net_tcp_listen` 直接返回 `[slot, port]` tuple
- B — 单独 `__net_tcp_listener_local_addr(slot)` builtin
- C — Z42 端用 `getsockname` syscall 包装

**决定**：A。理由：
- 简化为单次 builtin 调用就拿到全部所需信息
- 测试场景（端口 0 + Bind + 立刻 Connect）最常见 — 减少 round-trip
- `[slot, port]` Array 与"失败 tuple" Array 用第一字段 kind 区分（`[0, slot, port]` 不需要因为成功就是 `[slot, port]`；改用 spec 中 `[i64, i64]` Array 表示成功）

实施细节：失败用 `[1, msg]` 长度 2 数组（kind=1）；成功用 `[slot_i64, port_i64]` 同样长度 2 数组。区分：成功时两个字段都是 i64 数字；失败时 [0] 是 kind = 1 i64，[1] 是 string。z42 端 `if (raw[1] is string) { throw new SocketException(...) }`。

> **替代方案**（实施期可能改）：成功用 `[0, slot, port]` 三元组、失败 `[1, msg]` 二元组，**长度区分** 更显式。实施时取这个版本以避免 type-discriminate 复杂度。

## Implementation Notes

### Slot allocator

`VmContext::next_tcp_socket_slot() -> u64` / `next_tcp_listener_slot() -> u64`：原子递增计数器，永不复用。与 `next_process_slot` 同 pattern。

### Builtin error 路径

Rust 侧 `std::io::Result<...>` 失败：
- `ConnectionRefused` / `TimedOut` / `BrokenPipe` 等 → kind=1 错误 tuple，message 用 `format!("{:?}: {}", err.kind(), err)` 透传 `io::ErrorKind` debug
- Slot 不存在 → kind=2 closed/invalid

Z42 端 catch 路径：
- `if (raw is byte[])`（按 z42 类型判定 — Array of [kind, msg]）→ 检查 kind 抛 SocketException / SocketClosedException
- 成功路径直接拿值

### Drop 顺序

`TcpClient.Dispose()` 路径：
1. Set `_disposed = true`（z42 侧）
2. Cached `_stream` 如果存在，标记 stream 的 `_closed = true`（防后续 Read 调用越过 drop）
3. 调 `__net_tcp_socket_drop(slot)`：Rust 侧 `tcp_sockets.lock().remove(slot)` — `TcpStream` Drop impl 自动关 fd

### Read 0-length buffer 边界

`stream.Read(buf, 0, 0)` 直接返回 0，不调底层 — 镜像 `Std.IO.MemoryStream` 行为。

### NetworkStream EOF 语义

`std::io::Read::read(...)` 返回 `Ok(0)` 时 = EOF。Rust 侧 builtin 直接透传到 z42（z42 端不再特殊处理，依赖 spec 中 "返回 0 = EOF" 约定）。

### 跨平台 gate

```rust
// src/runtime/src/corelib/network.rs

#[cfg(not(target_arch = "wasm32"))]
mod imp {
    pub fn net_tcp_connect(host: &str, port: u16) -> Result<u64, ...> {
        let stream = std::net::TcpStream::connect((host, port))?;
        // ... add to slot table, return slot_id
    }
}

#[cfg(target_arch = "wasm32")]
mod imp {
    pub fn net_tcp_connect(_: &str, _: u16) -> Result<u64, ...> {
        Err(...)  // → kind=1 unsupported tuple
    }
}
```

## Testing Strategy

### Unit / Integration tests（z42 端）

1. **tcp_loopback.z42** — 同进程 listener + client round trip：
   - listener `Bind("127.0.0.1", 0)`, 拿 `LocalPort`
   - 新线程 `client.Connect("127.0.0.1", port)` + 写 "hello"
   - listener `AcceptTcpClient()` + stream.ReadExactly(5) == "hello"
   - 反向 write/read 也通

2. **tcp_stream_io.z42** — `NetworkStream` API 覆盖：
   - `CanRead/CanWrite/CanSeek` 返回正确
   - `Seek` 抛 `NotSupportedException`
   - peer close → `Read` 返回 0（EOF）
   - 已关闭 stream 调 `Read`/`Write` 抛 `SocketClosedException`

3. **tcp_disposal.z42** — 资源生命周期：
   - `Dispose` 幂等
   - 未 Dispose 让 GC 回收不抛
   - Bind 同端口在前一 listener Stop 后能成功

### Rust 单测（runtime 侧）

`src/runtime/src/corelib/network_tests.rs`：
- Slot allocator monotonic
- Connect to invalid address → 正确返回 error tuple
- Read after drop slot → kind=2 closed

### test-all.sh GREEN gate

加 `./scripts/test-stdlib.sh` 自动跑 z42.net 三个测试文件。

## Deferred / Open

K1 留的债务（独立 spec）：
- UDP：`add-z42-net-udp`
- IPAddress / IPEndPoint 类型：`add-z42-net-ipaddress`
- Timeout：`add-z42-net-timeout`
- TLS / HTTPS：`add-z42-net-tls`
- HTTP client：`add-z42-net-http`
- Async：依赖 L3 async/await
- Wasm32 真实 socket（WASI-sockets）：依赖 WASI runtime feature 成熟
