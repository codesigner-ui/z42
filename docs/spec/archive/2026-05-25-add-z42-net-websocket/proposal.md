# Proposal: add z42.net WebSocket client (K4) — RFC 6455 over TCP

## Why

K3 shipped HTTP/1.1; WebSocket (RFC 6455) builds on it for full-duplex
realtime messaging. Use cases:
- Live notifications / pub-sub
- Real-time game / chat
- Server-Sent-Events alternative for bidirectional
- Replacement for long polling

ws:// (plaintext) only — wss:// (TLS) waits for `add-z42-net-tls`.

## What Changes

新增 `Std.Net.WebSockets` namespace:
- `WebSocketClient` — top-level client; connect / send / receive / close
- `WebSocketMessage` — `{ MessageType, Buffer }` carrier returned by Receive
- `WebSocketMessageType` — text / binary / close enum-like constants
- `WebSocketException` — base for protocol violations
- `WebSocketState` — connecting / open / closing / closed constants (informational)

V0 features:
- ws:// upgrade handshake (HTTP/1.1 Upgrade: websocket + Sec-WebSocket-Key /
  Sec-WebSocket-Accept SHA-1 challenge per RFC 6455 §4)
- Frame encode / decode (RFC 6455 §5): fin / opcode / mask / payload-length
- Client → server masking (required by RFC)
- Text + Binary messages
- Ping / Pong control frames (auto-reply to server ping)
- Close handshake (opcode 0x8 + status code)
- No fragmentation send (v0 sends single-frame messages); receive handles
  multi-frame fragmentation (assemble into one message)

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.net/src/WebSockets/WebSocketClient.z42` | NEW | client class |
| `src/libraries/z42.net/src/WebSockets/WebSocketMessage.z42` | NEW | `{ MessageType, Buffer }` carrier |
| `src/libraries/z42.net/src/WebSockets/WebSocketMessageType.z42` | NEW | int constants (Text=1, Binary=2, Close=8, Ping=9, Pong=10) |
| `src/libraries/z42.net/src/WebSockets/WebSocketState.z42` | NEW | int constants (Connecting=0, Open=1, CloseSent=2, Closed=3) |
| `src/libraries/z42.net/src/WebSockets/WebSocketException.z42` | NEW | base exception (namespace Std) |
| `src/libraries/z42.net/src/WebSockets/WebSocketProtocolException.z42` | NEW | RFC 6455 frame parse / handshake violations |
| `src/libraries/z42.net/src/WebSockets/_FrameCodec.z42` | NEW | RFC 6455 §5 frame encode / decode helpers (internal) |
| `src/libraries/z42.net/tests/ws_handshake.z42` | NEW | upgrade handshake + Sec-WebSocket-Accept validation |
| `src/libraries/z42.net/tests/ws_echo.z42` | NEW | client sends text / binary; server echoes |
| `src/libraries/z42.net/tests/ws_ping_pong.z42` | NEW | server sends ping → client auto-pongs |
| `src/libraries/z42.net/tests/ws_close.z42` | NEW | close handshake; subsequent Send throws |
| `src/libraries/z42.net/tests/ws_fragmentation.z42` | NEW | server sends multi-frame message → assembled |
| `src/libraries/z42.net/README.md` | MODIFY | WebSocket section |
| `docs/design/stdlib/net.md` | MODIFY | flip `net-future-websocket` → ✅ landed |
| `docs/design/stdlib/roadmap.md` | MODIFY | WebSocket Deferred → ✅ |

## Out of Scope

- **wss:// (TLS)** — waits for `add-z42-net-tls`
- **WebSocket server** — `add-z42-net-websocket-server` follow-up
- **Sending fragmented messages** — v0 always single-frame send; receiving
  fragmented messages is in scope
- **Per-message-deflate extension (RFC 7692)** — `add-z42-net-websocket-deflate`
- **Subprotocols** — Sec-WebSocket-Protocol negotiation; v0 doesn't negotiate
- **Async** — depends L3 async/await
- **Heartbeat / keepalive** — application-layer concern; v0 supports manual
  Ping send + auto Pong reply only
