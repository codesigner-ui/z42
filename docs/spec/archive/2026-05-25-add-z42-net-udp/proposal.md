# Proposal: add z42.net UDP — datagram sockets

## Why

z42.net K1 shipped TCP-only (2026-05-24, `add-z42-net`). UDP is the K1
follow-up listed in net.md Deferred (`net-future-udp`). Use cases:

- DNS resolver implementations
- QUIC building blocks
- Real-time audio / video streaming
- Multicast / broadcast network discovery
- Lightweight RPC (gRPC over UDP variants, mDNS)

`std::net::UdpSocket` is in Rust std lib — same in-VM strategy as TCP, no
new cdylib.

## What Changes

- New `Std.Net.Sockets.UdpClient` — sync blocking UDP socket
- New `Std.Net.Sockets.UdpReceiveResult` — `{ byte[] Buffer, string RemoteHost, int RemotePort }` carrier (z42 has no out params or tuples)
- New 4 `__net_udp_*` VM builtins
- New `VmCore.udp_sockets` slot table mirroring `tcp_sockets`
- wasm32: UDP builtins throw `NetUnsupportedException`

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.net/src/UdpClient.z42` | NEW | sync UDP client class |
| `src/libraries/z42.net/src/UdpReceiveResult.z42` | NEW | datagram + sender carrier |
| `src/libraries/z42.net/src/UdpNative.z42` | NEW | `[Native]` wrappers + decode helpers (mirrors `NetTcpNative` / `NetTcpDecode`) |
| `src/libraries/z42.net/tests/udp_loopback.z42` | NEW | in-process send/receive round trip |
| `src/libraries/z42.net/tests/udp_disposal.z42` | NEW | Dispose / unbind / use-after-close |
| `src/libraries/z42.net/README.md` | MODIFY | add UDP section |
| `src/runtime/src/corelib/network.rs` | MODIFY | add UDP builtin section alongside TCP |
| `src/runtime/src/corelib/network_tests.rs` | MODIFY | add UDP Rust unit tests |
| `src/runtime/src/corelib/mod.rs` | MODIFY | register `__net_udp_*` × 4 |
| `src/runtime/src/vm_context.rs` | MODIFY | `udp_sockets: HashMap<u64, UdpSocket>` + helpers |
| `docs/design/stdlib/net.md` | MODIFY | flip `net-future-udp` Deferred → ✅ landed; add UDP API section |
| `docs/design/stdlib/overview.md` | MODIFY | z42.net catalog entry update |

**Only-read references**:
- `src/libraries/z42.net/src/TcpClient.z42` — K1 client pattern
- `src/runtime/src/corelib/network.rs` (TCP section) — kind-tuple shape + slot management

## Out of Scope

- **Connected UDP** (`Connect(host, port)` then `Send(buf)` without addr) — v0 always-explicit-addr; follow-up `add-z42-net-udp-connected`
- **Multicast / broadcast** (`JoinMulticastGroup` etc.) — follow-up
- **Async UDP** — depends on L3 async/await
- **IPAddress / IPEndPoint strong types** — still on `add-z42-net-ipaddress` follow-up; UDP uses `(string host, int port)` like TCP K1
- **Timeout** — same `add-z42-net-timeout` follow-up applies to both TCP and UDP
- **wasm32 real UDP** — WASI sockets unstable; throws `NetUnsupportedException`
