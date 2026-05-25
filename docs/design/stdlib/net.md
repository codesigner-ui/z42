# z42.net

Network sockets + HTTP + WebSocket — sync today, async + TLS later.

## v0 scope (K1 + K2 + K3 + K4, 2026-05-24 / 2026-05-25 / 2026-05-25 / 2026-05-25)

**K1 = TCP** (`add-z42-net`, 2026-05-24). **K2 = UDP** (`add-z42-net-udp`,
2026-05-25). **K3 = HTTP/1.1 plaintext** (`add-z42-net-http`, 2026-05-25).
**K4 = WebSocket ws://** (`add-z42-net-websocket`, 2026-05-25). IPAddress /
DNS / TLS / HTTPS / wss:// / HTTP/2 / Async still 独立 follow-up specs.

### Public API

```z42
namespace Std.Net.Sockets;

public class TcpClient {
    public TcpClient();
    public static TcpClient ConnectTo(string host, int port);   // factory
    public void Connect(string host, int port);
    public NetworkStream GetStream();                            // lazy + cached
    public string RemoteHost();
    public int RemotePort();
    public void Dispose();
    public void Close();   // alias for Dispose
}

public class TcpListener {
    public TcpListener();
    public void Bind(string host, int port);    // port 0 → OS assigns
    public void Start();                         // BCL-compat no-op
    public TcpClient AcceptTcpClient();          // blocks
    public int LocalPort();                      // post-Bind discover
    public string BindHost();
    public void Stop();                          // alias for Dispose
    public void Dispose();
}

public class NetworkStream : Std.IO.Stream {
    override bool CanRead();
    override bool CanWrite();
    override bool CanSeek();   // always false
    override int Read(byte[] buffer, int offset, int count);   // 0 = EOF
    override void Write(byte[] buffer, int offset, int count);
    override void Close();
}

// UDP — K2 (add-z42-net-udp, 2026-05-25)
public class UdpClient {
    public UdpClient();
    public void Bind(string host, int port);                                    // port 0 → OS assigns
    public int  Send(byte[] data, int length, string remoteHost, int remotePort);  // auto-bind on first call
    public UdpReceiveResult Receive();                                          // blocking
    public int  LocalPort();                                                    // post-Bind
    public string BindHost();
    public void Dispose();
    public void Close();        // alias for Dispose
}

public class UdpReceiveResult {
    public byte[] Buffer;
    public string RemoteHost;
    public int    RemotePort;
}

// Exception hierarchy (namespace Std)
public class NetException : Exception { }
public class NetUnsupportedException : NetException { }   // wasm32
public class SocketException        : NetException { }   // io fail
public class SocketClosedException  : NetException { }   // use-after-close
```

### Architecture

```
USER CODE  ──►  TcpClient/TcpListener/NetworkStream  ──►  __net_tcp_* builtins
                          (z42 source)                      (VM corelib)
                                                                │
                                                                ▼
                                                    VmCore.tcp_{sockets,listeners}
                                                    HashMap<u64, std::net::*>
                                                                │
                                                                ▼
                                                       std::net::TcpStream / TcpListener
```

VM-side implementation: `src/runtime/src/corelib/network.rs`. Uses
`std::net::*` directly (in-VM, not cdylib) — std lib is already linked
into z42vm, no new deps. wasm32 target: all builtins return
`KIND_UNSUPPORTED` tuple → z42 throws `NetUnsupportedException`.

Slot table follows the `ProcessHandle` pattern exactly:
`alloc_tcp_socket_slot` / `alloc_tcp_listener_slot` (monotonic u64
counter, never reused) on a single `VmCore.tcp_*` `Mutex<HashMap>` shared
across threads. `TcpStream` / `TcpListener` `Drop` impl auto-closes the
fd when removed from the map.

### Return shape convention

All `__net_tcp_*` builtins (except `*_drop` which return `Value::Null`)
return a discriminated `Value::Array` with leading `KIND_*` tag:

| Shape | Meaning |
|-------|---------|
| `[I64(0), I64(slot)]` | KIND_OK — connect / accept / socket_drop |
| `[I64(0), I64(slot), I64(actual_port)]` | KIND_OK — listen |
| `[I64(0), I64(nbytes)]` | KIND_OK — read / write (0 = EOF) |
| `[I64(1), Str(message)]` | KIND_SOCKET_ERR — io failure |
| `[I64(2)]` | KIND_HANDLE_INVALID — slot missing |
| `[I64(3)]` | KIND_UNSUPPORTED — wasm32 |

Z42 facade decodes via `Std.Net.Sockets.NetTcpDecode.{ToSlot, ToInt,
ToListenSlot, Throw}` — centralised kind dispatch so each public method
doesn't repeat the switch.

### Design decisions

| # | Decision | Rationale |
|---|----------|-----------|
| 1 | In-VM `corelib/network.rs`, not cdylib | std::net 已 link 进 z42vm；compression 用 cdylib 是为了重 dep (flate2/zstd) |
| 2 | Slot table mirror `ProcessHandle` | 成熟 pattern；GC drop / Dispose 一致 |
| 3 | 同步阻塞，no timeout | 与 stdlib 现状一致；timeout / async 独立 spec |
| 4 | `(string host, int port)` API | 最小可用；`IPAddress` 独立 spec |
| 5 | `GetStream` lazy + cached | 与 `ProcessHandle.GetStdoutStream` 一致 |
| 6 | wasm32 → `NetUnsupportedException` | 允许用户 try/catch 探测；不 panic |
| 7 | Uniform `[kind, ...]` tuple shape | 简化 z42 端 decode；避免 type-discriminate |
| 8 | `TcpClient.ConnectTo(host, port)` 静态 factory 而非 ctor | z42 当前 ctor 重载与 accept-side wrapper 冲突；factory 绕开 |

### 命名 / API ergonomics

- `TcpClient.Close()` 是 `Dispose()` 的 alias，与 .NET BCL 一致
- `TcpListener.Stop()` 是 `Dispose()` 的 alias
- `TcpListener.Start()` 是 BCL-compat no-op — Rust `TcpListener::bind`
  原子 bind+listen，无需第二步
- 端口 0 = OS 分配；测试代码标准约定避免硬编码端口冲突

## Deferred / Future Work

### ~~`net-future-udp`~~ — **✅ 已落地 2026-05-25 (add-z42-net-udp K2)**

Shipped: `Std.Net.Sockets.UdpClient` + `UdpReceiveResult` carrier + 4 `__net_udp_*`
builtins (`bind` / `send` / `recv` / `drop`). Same kind-tagged tuple shape +
slot-table pattern as K1 TCP; new `VmCore.udp_sockets` HashMap. Auto-bind on
first Send (BCL `UdpClient` semantics). 13 z42 tests (loopback round-trip,
reply path, empty datagram, auto-bind, port discovery, disposal idempotent,
use-after-close, before-bind error paths) + 5 Rust unit tests.

Out of scope (now their own follow-up specs):
- `add-z42-net-udp-connected` — `Connect(host, port)` + bare Send/Receive
- `add-z42-net-udp-multicast` — `JoinMulticastGroup` + multicast send
- `add-z42-net-udp-recv-into` — buffer-fill variant (avoid per-call allocation)
- `add-z42-net-udp-recv-timeout` — Receive timeout (covered by general `net-future-timeout`)

### `net-future-ipaddress` — IPAddress / IPEndPoint 强类型

- **来源**：K1 简化为 `(string host, int port)`
- **触发原因**：K1 最小可用；强类型 IPAddress 需要 v4/v6 parser + IPv6 zone-id 处理 + ToString round-trip
- **触发条件**：用户需要 IPv4/IPv6 区分（如绑定特定 interface）
- **当前 workaround**：`(string host, int port)` Rust std 自动支持双栈

### `net-future-dns` — `Std.Net.Dns.GetHostAddresses(host)`

- **来源**：K1 内部依赖 Rust `to_socket_addrs` 自动 resolve
- **触发原因**：用户级 DNS lookup API（非 connect 路径）
- **触发条件**：HTTP client 需要 SRV/MX 等高级 lookup
- **当前 workaround**：用 `TcpClient.Connect("host", port)` 间接 DNS

### `net-future-timeout` — read/write/connect timeout

- **来源**：K1 同步阻塞，无超时
- **触发原因**：与 `ProcessHandle.Wait` 阻塞模型一致；超时是独立维度
- **触发条件**：网络分区 / 慢响应攻击防御用例
- **当前 workaround**：调用方用线程 + Kill 自行超时

### `net-future-tls` — TLS / HTTPS

- **来源**：K1 明确 out of scope
- **触发原因**：需要 OpenSSL / rustls cdylib（重 dep + 跨平台分发复杂）
- **触发条件**：HTTP client / 安全 RPC 需求
- **当前 workaround**：shell out to curl

### ~~`net-future-http`~~ — **✅ 已落地 2026-05-25 (add-z42-net-http K3)**

Shipped: `Std.Net.Http.{HttpClient, HttpRequest, HttpResponse, HttpHeaders,
HttpMethod, HttpStatusCode, HttpException, HttpProtocolException, HttpUrl}`.
Pure-script over TcpClient (K1), no new VM builtin. http:// only —
https:// throws NotSupportedException pending `add-z42-net-tls`. Supports
Content-Length and Transfer-Encoding: chunked incoming; outgoing always
Content-Length. Case-insensitive HttpHeaders (raw string[]+count storage
since z42 field generic types are unsupported). 13 z42 tests cover
GET/POST/chunked/headers/scheme errors.

Out of scope (now their own follow-up specs):
- `add-z42-net-http-keepalive` — Connection: keep-alive + pool
- `add-z42-net-http-stream-body` — `HttpResponse.GetStream() → Stream`
- `add-z42-net-http-redirects` — auto-follow 3xx
- `add-z42-net-http-cookies` — Set-Cookie + jar
- `add-z42-net-http-auth` — Basic / Bearer / Digest helpers
- `add-z42-net-http-compression` — gzip / br Accept-Encoding + auto-decompress
- `add-z42-net-http-server` — `HttpListener` / `HttpServer`
- `add-z42-net-http2` — HTTP/2 binary framing + HPACK

### `net-future-async` — async/await sockets

- **来源**：K1 同步阻塞
- **触发原因**：依赖 L3 async/await 语法 + 调度器
- **触发条件**：L3 stage 解锁
- **当前 workaround**：用 `Std.Threading.Thread.Start` 每连接一线程

### `net-future-socket-options` — SO_REUSEADDR / SO_KEEPALIVE / Nagle / etc.

- **来源**：K1 用 std::net 默认值
- **触发原因**：高性能服务 / 短连接重启场景需要 tuning
- **触发条件**：bench 揭示瓶颈或 production 配置需求
- **当前 workaround**：默认值满足大部分用例

### `net-future-wasm-wasi-sockets` — wasm32 真实 socket

- **来源**：K1 wasm32 直接 throw `NetUnsupportedException`
- **触发原因**：wasi-sockets 标准 unstable，主流 wasm runtime 未实现
- **触发条件**：WASI sockets stable + z42 wasm dev env 进入主线
- **当前 workaround**：try/catch + host JS interop fallback
