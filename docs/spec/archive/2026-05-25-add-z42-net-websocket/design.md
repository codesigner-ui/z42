# Design: z42.net WebSocket client (K4) — RFC 6455 over TCP

## Architecture

```
USER CODE  ──►  WebSocketClient / WebSocketMessage / WebSocketMessageType
                  (pure-script in z42.net namespace Std.Net.WebSockets)
                                │
                                ▼
                  HTTP/1.1 Upgrade handshake (inlined; doesn't use HttpClient
                  because handshake takes over the socket; we just emit raw
                  bytes + parse the response)
                                │
                                ▼
                  Frame encode/decode via `_FrameCodec`
                                │
                                ▼
                    Std.Net.Sockets.TcpClient (K1)
                    Std.Net.Sockets.NetworkStream
```

**No new VM builtin.** Pure script over K1 TCP. ws:// only — wss:// throws
`NotSupportedException` (TLS layer is `add-z42-net-tls` follow-up).

## Decisions

### Decision 1: Pure-script over TcpClient

Same approach as K3 HTTP. `WebSocketClient.Connect(url)` opens
`new TcpClient()` + `Connect(host, port)`, sends the HTTP/1.1 Upgrade
request bytes, reads the response, then takes over the stream for raw
frame I/O.

### Decision 2: Skip Sec-WebSocket-Accept validation v0

RFC 6455 §4.2.2: server's `Sec-WebSocket-Accept` should be
`base64(SHA-1(client-key + magic))`. K4 v0 doesn't validate this because
z42.crypto v0 lacks SHA-1. Instead, accept any response with:
- HTTP/1.x 101 status code
- `Upgrade: websocket` response header

This is sufficient to confirm the server intends to speak WebSocket.
Strict validation is `add-z42-net-websocket-accept-validate` follow-up
(depends on SHA-1 in z42.crypto).

### Decision 3: Single-frame send, multi-frame receive

Outgoing: every Send call produces one frame with FIN=1. No fragmentation
support v0 — there's a single API surface to send a complete message,
no streaming send.

Incoming: handles RFC 6455 fragmentation per §5.4 — server can send a
data frame (Text or Binary) with FIN=0 followed by zero or more
Continuation (opcode 0) frames with FIN=0, ending with FIN=1. The
Receive method assembles these into one logical message before returning.

### Decision 4: Auto-pong server pings inside Receive

RFC 6455 §5.5.2: a Ping frame MUST be replied to with a Pong frame as
soon as practical. K4's `Receive()` loop:
- detects Ping (opcode 9) → sends Pong (opcode 10) with same payload →
  continues loop (doesn't surface ping to user)
- detects Pong (opcode 10) → drops silently (heartbeat path)
- detects Close (opcode 8) → echoes Close → transitions to Closed state
  → returns the Close message to caller

This keeps user code simple — `Receive()` only returns Text / Binary /
Close messages, not control frames.

### Decision 5: Close payload format

RFC 6455 §5.5.1: Close frame may carry a 2-byte big-endian status code
+ optional UTF-8 reason. `Close(code, reason)` builds this payload.
`WebSocketMessage.CloseStatus()` / `CloseReason()` decode incoming
close payloads.

### Decision 6: Random key + mask via `Std.Random.Random`

`Sec-WebSocket-Key`: 16 random bytes base64-encoded (RFC 6455 §4.1).
Frame masking key: 4 random bytes per outgoing frame (§5.3).

Both use `Std.Random.Random` with wall-clock default seed. Not
cryptographically secure but adequate per RFC (the mask is for
backwards-compat with broken intermediaries, not security).

### Decision 7: All HTTP read helpers inlined

K3's `_LineReader` could in principle be shared with K4's handshake
response parsing, but cross-file class references between Http and
WebSockets risks the K2 cross-file type-token mismatch issue. K4
inlines `_WsLineReader` (~60 LOC) to avoid the cross-module dep.

## Frame layout reference

RFC 6455 §5.2:

```
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-------+-+-------------+-------------------------------+
|F|R|R|R| opcode|M| Payload len |    Extended payload length    |
|I|S|S|S|  (4)  |A|     (7)     |             (16/64)           |
|N|V|V|V|       |S|             |   (if payload len==126/127)   |
| |1|2|3|       |K|             |                               |
+-+-+-+-+-------+-+-------------+ - - - - - - - - - - - - - - - +
|     Extended payload length continued, if payload len == 127  |
+ - - - - - - - - - - - - - - - +-------------------------------+
|                               |Masking-key, if MASK set to 1  |
+-------------------------------+-------------------------------+
| Masking-key (continued)       |          Payload Data         |
+-------------------------------- - - - - - - - - - - - - - - - +
```

Client MUST mask outgoing frames; server MUST NOT mask. `_FrameCodec`
enforces both (rejects masked frames on read with
`WebSocketProtocolException`).

## Testing Strategy

In-process server using `TcpListener.AcceptTcpClient` + manual handshake
+ raw frame emit (helpers in test files compute frames bit-by-bit since
client mask path is in `_FrameCodec` but server emit needs unmasked).

Tests cover:
- `ws_handshake.z42` — 101 accepted, non-101 throws, wss/bad scheme errors
- `ws_echo.z42` — text + binary round trip, 200-byte payload (16-bit length path)
- `ws_ping_pong.z42` — server ping → client auto-pong
- `ws_close.z42` — send-after-close throws, idempotent Dispose, server close received
- `ws_fragmentation.z42` — 3-frame fragmented text message assembled

## Deferred / Open

- `add-z42-net-tls` — wss://
- `add-z42-net-websocket-accept-validate` — SHA-1 + verification (after Sha1 lands in z42.crypto)
- `add-z42-net-websocket-server` — server side
- `add-z42-net-websocket-deflate` — per-message-deflate compression
- `add-z42-net-websocket-fragmented-send` — outgoing fragmentation
- `add-z42-net-websocket-subprotocol` — subprotocol negotiation
