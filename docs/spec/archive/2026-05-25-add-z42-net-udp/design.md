# Design: z42.net UDP datagram sockets

## Architecture

```
USER CODE  ──►  UdpClient / UdpReceiveResult  ──►  __net_udp_* builtins
                  (z42 source)                      (VM corelib/network.rs)
                                                            │
                                                            ▼
                                                VmCore.udp_sockets:
                                                  HashMap<u64, UdpSocket>
                                                            │
                                                            ▼
                                                   std::net::UdpSocket
```

Slot table mirrors `tcp_sockets`. `UdpSocket::drop` closes fd; we use the same
allocator/drop pattern (`alloc_udp_socket_slot` / remove-on-drop builtin).

## Decisions

### Decision 1: In-VM corelib, sibling to TCP

`std::net::UdpSocket` 已 link 进 z42vm — same call to add UDP alongside TCP in
`corelib/network.rs`, no new cdylib.

### Decision 2: No `Connect()` for v0

.NET BCL `UdpClient.Connect(host, port)` sets a default remote so subsequent
Send doesn't need address. Postponed to `add-z42-net-udp-connected` — adds
state to UdpClient (connected bool + default remote) which complicates the
class without major user-benefit for v0. Always pass remote per-Send.

### Decision 3: `UdpReceiveResult` carrier class

z42 has no out params / multi-value return / structs-by-value. Receive must
return ALL of: datagram bytes + remote host + remote port. Wrap in a public
class with three public fields (read-after-construct convention — no setters).

### Decision 4: Receive allocates new byte[] per datagram

Two options:
- A: Caller provides buffer; result has `int Length` instead of `byte[] Buffer`
- B: Receive allocates and returns `byte[]` sized to actual datagram

Chose **B**. Reasons:
- Matches BCL `UdpClient.Receive(ref IPEndPoint) → byte[]`
- UDP datagrams have unknown size up-front; caller's pre-allocated buffer may
  truncate (Rust `UdpSocket::recv_from` returns the truncation length, and
  user-facing semantics around silent truncation are surprising)
- Allocation cost is acceptable for typical UDP packet sizes (< 1500 bytes)
- Buffer-fill variant can be follow-up `add-z42-net-udp-recv-into`

### Decision 5: Send returns bytes sent

Rust `UdpSocket::send_to` returns `usize`. Forward this as `int` to z42. For
normal UDP, sent == requested unless the buffer is too large (> MTU); in that
case the OS fragments or returns an error. Z42 just trusts the OS return.

### Decision 6: Same uniform `[kind, ...]` tuple return as TCP

Reuse `NetTcpDecode.Throw` for error dispatch — `UdpDecode` will be a thin
analog (or even merged with `NetTcpDecode` if convenient). v0 spec keeps
them separate for symmetry; merge follow-up if duplication grows.

Actually — looking ahead to HTTP/WebSocket, they'll need their own decode
helpers too. Let's NOT merge: `UdpDecode` stays z42.net.sockets-local.

### Decision 7: Slot allocator mirrors TCP

`VmCore.next_udp_socket_id: AtomicU64` + `udp_sockets: Mutex<HashMap<u64, UdpSocket>>`. Helper methods `alloc_udp_socket_slot` / `udp_socket_slot_count`. Same monotonic-never-reused pattern.

## Implementation Notes

### Builtin error paths

- Bind: `Err(io::Error)` from `UdpSocket::bind(addr)` → `KIND_SOCKET_ERR` with formatted message
- Send: `Err` from `send_to(buf, addr)` → `KIND_SOCKET_ERR`
- Recv: `Err` from `recv_from(&mut buf)` → `KIND_SOCKET_ERR`
- Drop: always Null

### Receive buffer sizing

`UdpSocket::recv_from(&mut buf)` requires the buffer be large enough or it
truncates the datagram silently (returns the truncation length). Use a 65536
byte temp buffer — larger than any normal UDP datagram including IPv6 jumbo —
then copy only the bytes filled into the result `byte[]`.

### Z42-side API

```z42
public class UdpClient {
    public UdpClient();
    public void Bind(string host, int port);   // port 0 → OS assigns
    public int  Send(byte[] data, int length, string remoteHost, int remotePort);
    public UdpReceiveResult Receive();           // blocking
    public int  LocalPort();                     // post-Bind
    public string BindHost();
    public void Dispose();
    public void Close();                         // alias for Dispose
}

public class UdpReceiveResult {
    public byte[] Buffer;
    public string RemoteHost;
    public int RemotePort;
}
```

For Send: if caller hasn't Bound, OS auto-binds on first send to an
ephemeral port. We implement this by lazy-binding on first Send if `_slotId
== -1`. Actually simpler: require explicit Bind for v0; if `Send` called
before Bind, auto-bind to `("0.0.0.0", 0)`. Caller can query `LocalPort()`
afterwards to see what port they got.

## Testing Strategy

### Z42 integration tests

1. `tests/udp_loopback.z42`:
   - Two UdpClients on 127.0.0.1; A bound to OS-assigned port; B sends to A;
     A.Receive() returns expected data + B's local port as RemotePort
   - Multiple datagrams round-trip in order (UDP doesn't guarantee order over
     network but loopback typically preserves it)
   - Reply path: A sends back to RemoteHost:RemotePort it just learned;
     B.Receive() gets it
   - Empty datagram (length 0) survives round trip

2. `tests/udp_disposal.z42`:
   - Dispose idempotent
   - Send/Receive after Dispose throws SocketClosedException
   - LocalPort after Bind, before Bind throws InvalidOperationException
   - Auto-bind on first Send without explicit Bind

### Rust unit tests

`corelib/network_tests.rs` 加 UDP section:
- Slot allocator monotonic
- Bind to valid + invalid port (port already in use should err, but hard to
  reliably trigger in tests — skip)
- Send/Recv unknown slot → handle-invalid
- Loopback round trip (host-side UdpSocket + ctx-side via builtins)

## Deferred / Open

K1 UDP scope cuts that become follow-up specs:
- `add-z42-net-udp-connected` — Connect(host, port) + bare Send/Receive
- `add-z42-net-udp-multicast` — JoinMulticastGroup + multicast send
- `add-z42-net-udp-recv-into` — buffer-fill variant of Receive
- `add-z42-net-udp-recv-timeout` — timeout on Receive (when timeouts come)
- `add-z42-net-async` — async/await variants
