# Tasks: add `TcpClient.SetKeepAlive(bool, int, int, int)` tuning overload

> 状态：🟢 已完成 | 创建：2026-06-03 | 归档：2026-06-03 | 类型：stdlib feat

**变更说明：** Extend the existing 1-arg `SetKeepAlive(bool)` with a
4-arg overload `SetKeepAlive(bool enable, int idleSecs, int
intervalSecs, int probes)` that maps onto socket2's `TcpKeepalive`
builder for per-OS fine-grained tuning. Closes
`net-future-keepalive-tuning`.

**原因：** SO_KEEPALIVE alone uses OS defaults which are wildly
different (Linux: 2 hours, macOS: 2 hours, Windows: 2 hours, with
different probe-interval / count defaults). Practical use of
keepalive — detecting dead peers in chat / streaming / RPC
protocols — requires setting these explicitly.

## Tasks
- [x] 1.1 Rust: new builtin
      `builtin_net_tcp_socket_set_keepalive_tuned(slot, enable,
      idle_secs, interval_secs, probes)` in
      `src/runtime/src/corelib/network.rs`. Uses
      `socket2::TcpKeepalive::new()` builder with cfg-gated
      `with_time` / `with_interval` / `with_retries`. `enable=false`
      falls back to plain `set_keepalive(false)` and ignores the
      tuning args. Zero/negative tuning values return a socket_err.
- [x] 1.2 Rust: wasm32 stub returning `unsupported`
- [x] 1.3 Rust: register in `src/runtime/src/corelib/mod.rs`
- [x] 1.4 z42: `NetTcpNative.SocketSetKeepaliveTuned` extern decl
- [x] 1.5 z42: `TcpClient.SetKeepAlive(bool, int, int, int)`
      overload — same pre-connect / disposed checks as the 1-arg
      sibling
- [x] 1.6 Tests: 4 new in `tcp_keepalive_reuseaddr.z42` —
      tuned-success / disable-ignoring-args / zero-idle-throws /
      pre-connect-throws
- [x] 1.7 Doc sync: `docs/design/stdlib/net.md` Deferred entry +
      `docs/design/stdlib/roadmap.md` row
- [x] 1.8 `./scripts/test-all.sh` — full GREEN
- [x] 1.9 Commit + push

## 备注

Per-OS field support:
- `idleSecs` — Unix + Windows
- `intervalSecs` — Unix + Windows (via WSAIoctl)
- `probes` — Linux / Android / FreeBSD only (silently ignored on
  macOS / iOS / Windows / *BSD other than FreeBSD)

This matches socket2 0.5's documented cfg matrix. Callers always
pass all four args; the kernel rejects unrepresentable values via
the `set_tcp_keepalive` call which routes through libc.
