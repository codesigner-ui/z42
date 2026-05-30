# Design: z42.net socket options extended

## Architecture

```
z42.net stdlib                  corelib (network.rs)              std / socket2
─────────────────              ─────────────────────             ──────────────────
TcpClient
  _connectTimeoutMs  ────►  __net_tcp_connect_with_timeout  ──►  TcpStream::connect_timeout
  SetKeepAlive(b)    ────►  __net_tcp_socket_set_keepalive  ──►  socket2::SockRef::set_tcp_keepalive

TcpListener
  _reuseAddress      ────►  __net_tcp_listen_with_options   ──►  socket2::Socket::new +
                                                                   set_reuse_address +
                                                                   bind + listen → into()

UdpClient
  SetReadTimeout     ────►  __net_udp_set_read_timeout      ──►  UdpSocket::set_read_timeout
  SetWriteTimeout    ────►  __net_udp_set_write_timeout     ──►  UdpSocket::set_write_timeout
```

## Decisions

### Decision 1: `socket2` crate vs raw libc / winapi

**问题**：SO_REUSEADDR / SO_KEEPALIVE 需要 setsockopt，跨平台 (Unix
libc + Windows winsock) 差异大。

**选项：**
- A — 加 `socket2 = "0.5"` 依赖
- B — 自写 `cfg(unix)` libc + `cfg(windows)` 直接 FFI winsock
- C — 仅支持 Unix，Windows throw NotSupported

**决定**：A。

**理由**：
- socket2 是 Rust networking working group 维护、tokio 等核心生态共用的事实标准
- 体积小，仅依赖 libc on Unix；Windows 内置自己的 winsock binding，不引入额外 dep
- 自写 raw FFI 容易踩 `c_int` vs `i32` 类型陷阱、Windows winsock ABI 与 BSD socket 差异
- 拒绝 Windows = roadmap.md "z42 build-driver prerequisites" 的目标倒退

### Decision 2: Connect timeout — pre-set 字段 vs `Connect()` 重载

**问题**：怎么给 `Connect` 加 timeout？

**选项：**
- A — `SetConnectTimeout(ms)` 存到字段，`Connect()` 读字段决定路径
- B — 新 arity 重载 `Connect(host, port, timeoutMs)`
- C — 单独方法 `ConnectWithTimeout(host, port, ms)`

**决定**：A。

**理由**：
- z42 编译器尚有 `compiler-future-typed-overload-resolution` Deferred —
  同元不同类型重载暂不支持（见 roadmap.md Deferred Backlog Index）；
  方案 B 在当前编译器路径上要走 mangled-name 工程绕，得不偿失
- 方案 C 把"配置"和"动作"绑成两个名字 (Connect / ConnectWithTimeout)，
  调用方在通用代码里需要 if 分支选 API；方案 A 通过字段把配置与动作正交
- BCL `TcpClient.ConnectTimeout` 用 `ReceiveTimeout` 同形态字段（虽然
  BCL 字段是 `ReceiveTimeout`，对应 SO_RCVTIMEO）；本设计的字段保持私有，
  仅通过 setter 暴露 — 与 z42.net 既有 `SetReadTimeout` 形态一致

### Decision 3: REUSEADDR pre-Bind 状态机校验

**问题**：SO_REUSEADDR 必须在 `bind` 之前 setsockopt（POSIX 语义；后设无效）。
怎么暴露给 z42 调用方？

**选项：**
- A — `SetReuseAddress(bool)` 仅在 pre-Bind 状态合法；post-Bind 抛
  `InvalidOperationException`
- B — 静默吞掉 post-Bind 调用（"set"但不生效）
- C — `TcpListener.BindWithOptions(host, port, reuse)` 一次性 atomic 设
  + bind

**决定**：A（with side-effect: 实施时也产 `TcpListener.Create(host, port, reuse)`
factory 方便单测）。

**理由**：
- B 是 silent failure 反模式，违反 [philosophy.md "修复必须从根因出发"](../../../../.claude/rules/philosophy.md)
- C 是好的辅助 API 但不替代主路径 — 多数调用方用 `new + Bind` 分两步，
  C 强制 atomic 让两步 API 失意义
- A 与 BCL `TcpListener.ExclusiveAddressUse` 同语义（pre-Start 设；post-Start 抛）

### Decision 4: KEEPALIVE bool-only vs tuning tuple

**问题**：SO_KEEPALIVE 真正生效需要 `TCP_KEEPIDLE` / `TCP_KEEPINTVL` /
`TCP_KEEPCNT` 三个 setsockopt 调优；各 OS 默认值差异极大（macOS 2hr,
Linux 75min, Windows 2hr）。

**选项：**
- A — v0 仅 `SetKeepAlive(bool)` 启用/禁用，三个 tuning 留 follow-up
  `net-future-keepalive-tuning`
- B — 一次性 `SetKeepAlive(bool, int idleSec, int intervalSec, int probes)`
  四参数

**决定**：A。

**理由**：
- B 在不同 OS 上的 TCP_KEEPIDLE/INTVL/CNT 单位（秒 vs 毫秒）+ 边界条件
  各有微小差异，需要细测；v0 不卡这个
- 大多数用例只需"启用 / 不启用"，OS 默认值够用
- B 留作显式 follow-up Deferred 项，避免 API 过早收死

### Decision 5: UDP timeout `<= 0` 语义

**问题**：与 TCP timeout 一样的兜底语义？

**决定**：是。`millis <= 0` → `None`（无 timeout / 阻塞），与
`apply_timeout` 既有 helper 行为一致：

```rust
let dur = if millis <= 0 { None } else { Some(Duration::from_millis(millis as u64)) };
```

复用现有 `apply_timeout` 模式不重新实现。

### Decision 6: TCP connect-timeout — 单独 builtin vs 复用现 connect

**问题**：`__net_tcp_connect(host, port)` 已存在。要不要给它加 millis
optional arg？

**选项：**
- A — 新建 `__net_tcp_connect_with_timeout(host, port, millis)`，原 builtin 不动
- B — 现 `__net_tcp_connect` 加 optional 第 3 arg

**决定**：A。

**理由**：
- z42 builtin 系统按 (name, arity) 注册，arity 改变 = 等价新 builtin
- 不影响现有 `__net_tcp_connect` 调用方（如 K3 HttpClient 等都通过 TcpClient.Connect()
  间接调；改 z42 stdlib 把 timeout 字段引入即可）
- 错误信息更清晰（带 timeout 的失败 vs 普通 connect 失败的区分）

## Implementation Notes

### corelib changes

```rust
// network.rs — TCP module
pub fn builtin_net_tcp_connect_with_timeout(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    const NAME: &str = "__net_tcp_connect_with_timeout";
    let host = arg_str(args, 0, NAME)?.to_string();
    let port = require_port(args, 1, NAME)?;
    let millis = arg_i64(args, 2, NAME)?;
    let dur = if millis <= 0 { Duration::from_millis(u32::MAX as u64) }
              else { Duration::from_millis(millis as u64) };

    let addr = format!("{}:{}", host, port);
    let socket_addr = match addr.to_socket_addrs().and_then(|mut it| it.next()
            .ok_or_else(|| std::io::Error::new(std::io::ErrorKind::AddrNotAvailable, "no addrs"))) {
        Ok(a) => a,
        Err(e) => return Ok(socket_err(ctx, format!("connect to {}: {}", addr, e))),
    };
    match TcpStream::connect_timeout(&socket_addr, dur) {
        Ok(stream) => { let slot_id = ctx.alloc_tcp_socket_slot(stream); Ok(ok_value(ctx, slot_id as i64)) }
        Err(e) => Ok(socket_err(ctx, format!("connect to {} (timeout {}ms): {}", addr, millis, e))),
    }
}

pub fn builtin_net_tcp_socket_set_keepalive(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    // Lookup slot → try_clone → socket2::SockRef::from(&clone).set_tcp_keepalive
    // (or .set_keepalive(bool) — the bool form sets SO_KEEPALIVE without tuning)
}

pub fn builtin_net_tcp_listen_with_options(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let reuse = match args.get(2) { Some(Value::Bool(b)) => *b, ... };
    let addr = format!("{}:{}", host, port).to_socket_addrs()...
    let domain = if addr.is_ipv4() { Domain::IPV4 } else { Domain::IPV6 };
    let sock = socket2::Socket::new(domain, Type::STREAM, None)?;
    sock.set_reuse_address(reuse)?;
    sock.bind(&addr.into())?;
    sock.listen(128)?;
    let listener: std::net::TcpListener = sock.into();
    // alloc slot like __net_tcp_listen
}

// UDP module
pub fn builtin_net_udp_set_read_timeout(ctx: &VmContext, args: &[Value]) -> Result<Value> { ... }
pub fn builtin_net_udp_set_write_timeout(ctx: &VmContext, args: &[Value]) -> Result<Value> { ... }
```

UDP `set_*_timeout` 模式直接镜像 TCP `apply_timeout` 但对 `udp_sockets`
slot table 操作（UdpSocket impl `set_read_timeout` / `set_write_timeout`
原生支持 — std::net 内置）。

### z42.net stdlib changes

```z42
// TcpClient.z42
public class TcpClient {
    private long _slotId;
    private bool _connected;
    private int  _connectTimeoutMs;   // 0 = no preset

    public void SetConnectTimeout(int millis) { this._connectTimeoutMs = millis; }

    public void Connect(string host, int port) {
        ...
        long[] r;
        if (this._connectTimeoutMs > 0) {
            r = TcpClient._connectWithTimeout(host, port, this._connectTimeoutMs);
        } else {
            r = TcpClient._connect(host, port);
        }
        ...
    }

    public void SetKeepAlive(bool enable) {
        if (!this._connected) {
            throw new InvalidOperationException("SetKeepAlive: TcpClient not connected");
        }
        Std.Net.Sockets._netTcpSocketSetKeepalive(this._slotId, enable);
    }
}

// TcpListener.z42
public class TcpListener {
    ...
    private bool _reuseAddress;
    private bool _bound;

    public void SetReuseAddress(bool enable) {
        if (this._bound) {
            throw new InvalidOperationException("SetReuseAddress: TcpListener already bound");
        }
        this._reuseAddress = enable;
    }

    public void Bind(string host, int port) {
        long[] r;
        if (this._reuseAddress) {
            r = TcpListener._listenWithOptions(host, port, true);
        } else {
            r = TcpListener._listen(host, port);
        }
        this._bound = true;
        ...
    }
}

// UdpClient.z42 — mirror of TCP timeout setters
```

### wasm32 stubs

```rust
#[cfg(target_arch = "wasm32")]
pub fn builtin_net_tcp_connect_with_timeout(ctx: &VmContext, _args: &[Value]) -> Result<Value> {
    Ok(net_unsupported(ctx, "TCP connect-timeout"))
}
// etc.
```

模式与现有 wasm32 UDP/TCP stub 一致。

## Testing Strategy

### tcp_connect_timeout.z42（4 tests）

- `Connect` 在 timeout 内成功（loopback）
- `Connect` 超 timeout 抛 `IOException`（用 `203.0.113.1`，TEST-NET-3）
- `SetConnectTimeout(0)` 行为等同未设（无 timeout）
- `SetConnectTimeout` 调用顺序错（先 Connect 后 Set）不抛、下次 Connect 用

### tcp_keepalive_reuseaddr.z42（5 tests）

- `SetKeepAlive(true)` on connected loopback → 不抛
- `SetKeepAlive` 未 Connect 抛 `InvalidOperationException`
- `SetReuseAddress(true)` + `Bind(localhost, 0)` 正常
- 第二个 Listener 同端口（port != 0）+ `SetReuseAddress(true)` Bind 不抛
  `Address already in use`（loopback 上 stop + restart）
- `SetReuseAddress` 在 Bind 之后抛 `InvalidOperationException`

### udp_timeout.z42（4 tests）

- `Bind` + `SetReadTimeout(200)` + `Receive` 无 sender → IOException
- `SetReadTimeout(0)` → 长阻塞（用 short fixture：spawn thread send 后即 Recv，
  确认收到；不验证"等多久"）
- `SetWriteTimeout(5000)` + `Send` loopback → 正常
- `SetReadTimeout` 未 Bind 抛 `InvalidOperationException`

### GREEN 验证

- `./scripts/build-stdlib.sh` — z42.net.zpkg 重建
- `./scripts/test-stdlib.sh z42.net` — 既有 + 新增全过
- `./scripts/test-all.sh` — 跨 stage 全绿（含 cross-zpkg）

### Pre-existing 回归

- TCP `SetReadTimeout` / `SetWriteTimeout` / Nagle / TTL 等既有测试不变
- UDP TTL / multicast 等不变
