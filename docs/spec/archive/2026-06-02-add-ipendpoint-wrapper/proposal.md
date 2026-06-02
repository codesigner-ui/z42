# Proposal: add `Std.Net.Sockets.IPEndPoint`

## Why

`IPAddress` exists (`add-z42-net-ipaddress`, 2026-05-27) but the
`(IPAddress, int port)` pair every socket call actually needs is
still passed as two parameters. BCL / .NET, Java NIO, Go's `netip`,
and the Linux/Win32 socket APIs all surface a single
`IPEndPoint`-shaped value so callers can:

- store / pass a single value through APIs that take "an endpoint"
  (`TcpClient.Connect(IPEndPoint)`, `Bind(IPEndPoint)`, etc.);
- parse/format the canonical `host:port` and `[host]:port` syntaxes
  (URL authority component / IRC server lists / Helm / Docker
  bridges all emit this form);
- compare two endpoints for equality with one call.

Listed in `docs/design/stdlib/net.md` Deferred under
`net-future-ipaddress` along with two unrelated niche items
(IPv4-in-IPv6 dotted form, IPv6 zone-id suffix). This spec ships the
IPEndPoint piece — the only one with broad demand. The other two
stay in net.md Deferred as separate IDs.

## What Changes

1. **New public class `Std.Net.Sockets.IPEndPoint`** with the
   minimal BCL-aligned surface:
   - `IPEndPoint(IPAddress address, int port)` constructor (rejects
     `port < 0` or `port > 65535`; rejects `null` address)
   - `Address() → IPAddress`
   - `Port() → int`
   - `ToString() → string`: `addr:port` for IPv4, `[addr]:port` for
     IPv6 (URL authority convention — brackets disambiguate the
     port colon from address colons)
   - `Parse(string s) → IPEndPoint`: accepts `addr:port`,
     `[addr]:port`, or even bare IPv6 `[::1]:80`; throws
     `FormatException` on missing port / malformed address / out-of-
     range port
   - `Equals(IPEndPoint other) → bool`: same address + same port

2. **Tests** under `src/libraries/z42.net/tests/ipendpoint.z42`:
   constructor validation / accessors / ToString IPv4 + IPv6 round-
   trip / Parse IPv4 + IPv6 + bracketed / Parse error paths /
   Equals symmetric / Equals with different family / large port
   range edges (0, 65535).

3. **Doc sync**: `docs/design/stdlib/net.md` — split the
   `net-future-ipaddress` Deferred entry; IPEndPoint marked ✅,
   remaining two niches (`net-future-ipaddress-v4mapped` /
   `net-future-ipaddress-zoneid`) stay listed.

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.net/src/IPEndPoint.z42` | NEW | the new class |
| `src/libraries/z42.net/tests/ipendpoint.z42` | NEW | tests (constructor validation / accessors / ToString / Parse / Equals / error paths) |
| `docs/design/stdlib/net.md` | MODIFY | mark IPEndPoint slice ✅; split residual into v4mapped + zoneid Deferred entries |
| `docs/design/stdlib/roadmap.md` | MODIFY | refine `net-future-ipaddress` row to reflect the split |
| `docs/spec/changes/add-ipendpoint-wrapper/` | NEW | this spec dir |

**只读引用**：

- `src/libraries/z42.net/src/IPAddress.z42` — for constructor /
  Parse / ToString / Equals shape to mirror
- `src/libraries/z42.net/tests/ipaddress.z42` — test patterns to
  mirror (if exists)

## Out of Scope

- **IPv4-in-IPv6 dotted form** (`::ffff:192.0.2.1`) — separate
  follow-up `net-future-ipaddress-v4mapped`. Touches
  `IPAddress._parseIPv6` / `_formatIPv6`, not `IPEndPoint`.
- **IPv6 zone-id suffix** (`fe80::1%eth0`) — separate follow-up
  `net-future-ipaddress-zoneid`. Same area of `IPAddress` but
  orthogonal to endpoint wrapping.
- **`TcpClient.Connect(IPEndPoint)` / `Bind(IPEndPoint)`
  overloads** — once IPEndPoint is the canonical type, wire it
  through existing socket APIs in a follow-up. Out of scope to keep
  this spec narrow.
- **`UdpClient.SetMulticastInterface(IPAddress)` updates** —
  unrelated.

## Open Questions

- [ ] **`Parse` empty-bracket form `[]:80`**: BCL throws
  `FormatException`. Match that — `[]:80` is meaningless.
- [ ] **`Parse` percent-encoded IPv6 brackets in URLs**:
  `[%5B::1%5D]:80`. Out of scope — `Std.Uri` already handles URL
  decoding upstream; callers should decode first.
