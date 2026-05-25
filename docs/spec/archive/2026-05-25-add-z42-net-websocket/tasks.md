# Tasks: add z42.net WebSocket client (K4)

> 状态：🟢 已完成 | 创建：2026-05-25 | 类型：feat (stdlib pure-script extension)
> Spec 类型：完整流程

## 进度

- [x] `WebSocketMessageType` / `WebSocketState` int constants
- [x] `WebSocketException` / `WebSocketProtocolException`
- [x] `WebSocketMessage` carrier (Buffer + MessageType + close helpers)
- [x] `_FrameCodec` — RFC 6455 §5 frame encode/decode (client masking, multi-length-field)
- [x] `WebSocketClient`:
  - Handshake: HTTP/1.1 GET Upgrade with Sec-WebSocket-Key + version 13
  - Response validation: 101 + Upgrade: websocket header
  - SendText / SendBinary / SendPing
  - Receive: assembles fragmented messages; auto-pong server pings; drops pongs
  - Close(code, reason) + Dispose idempotent
- [x] z42.net manifest 加 z42.encoding + z42.random deps
- [x] Tests: 12 z42 (handshake 4 + echo 3 + ping_pong 1 + close 3 + fragmentation 1)
- [x] 验证 GREEN: 51/51 z42.net tests pass (TCP 13 + UDP 13 + HTTP 13 + WS 12)

## 实施期发现

1. **No SHA-1 in z42.crypto v0** — RFC 6455 §4.2 specifies Sec-WebSocket-Accept
   = base64(SHA-1(key + magic)). K4 v0 skips client-side validation
   (`add-z42-net-websocket-accept-validate` follow-up) and relies on:
   - 101 status code check
   - presence of `Upgrade: websocket` response header
   This is acceptable: validation hardens against accidentally upgrading
   non-WS endpoints but isn't a security boundary (the server is trusted).
   Follow-up adds SHA-1 to z42.crypto + Accept verification.

2. **z42 has no `out` parameters** — `_parseWsUrl` originally took
   `out string`, `out int` etc. Refactored to return a `_WsUrl` carrier
   class. Same workaround as `UdpReceiveResult` in K2.

3. **No internal/package-private visibility** — `_FrameCodec`,
   `_DecodedFrame`, `_WsUrl`, `_WsLineReader` are all `public` with
   underscore-prefix convention indicating "implementation detail; do
   not consume from user code". z42 currently lacks `internal`.

4. **Cross-file class-token mismatch avoided** — learned from K2:
   `WebSocketMessage` is constructed inline in `WebSocketClient.Receive`
   (not via a helper in `_FrameCodec`) so the constructor + return-type
   token are in the same file.

## Out of scope (now follow-up specs)

- `add-z42-net-tls` — TLS support → unlocks `wss://`
- `add-z42-net-websocket-accept-validate` — SHA-1 + Sec-WebSocket-Accept verification (depends on z42.crypto Sha1)
- `add-z42-net-websocket-server` — server-side WebSocketListener
- `add-z42-net-websocket-deflate` — per-message-deflate compression (RFC 7692)
- `add-z42-net-websocket-fragmented-send` — `SendText(text, fragmented=true)` for outgoing fragmentation
- `add-z42-net-websocket-subprotocol` — Sec-WebSocket-Protocol negotiation
- `add-z42-net-async` — async/await variants
