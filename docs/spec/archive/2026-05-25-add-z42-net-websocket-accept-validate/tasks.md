# Tasks: WebSocket Sec-WebSocket-Accept validation (K4 follow-up)

> 状态：🟢 已完成 | 创建：2026-05-25 | 类型：fix (security hardening)
> Spec 类型：minimal mode

**变更说明**：补全 K4 WebSocket 客户端的 Sec-WebSocket-Accept 验证（RFC 6455 §4.2.2）。K4 v0 跳过该验证因 z42.crypto 缺 SHA-1；2026-05-25 `add-sha1-to-crypto` 落地后该 follow-up 可做。

**Why**: 严格符合 RFC 6455 — 客户端 SHOULD 验证 `Sec-WebSocket-Accept = base64(SHA-1(client-key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"))`. 防御性 — 防止意外升级到非 WS 端点。

## Tasks

- [x] `WebSocketClient._verifyHandshakeResponse(stream, clientKey)`:
  - 收 status line + headers
  - 抓 `Sec-WebSocket-Accept:` header；missing → `WebSocketProtocolException`
  - 计算 expected = `Base64.Encode(Sha1.HashString(clientKey + magic))`
  - 不匹配 → `WebSocketProtocolException`
- [x] `WebSocketClient._computeExpectedAccept(clientKey)` helper
- [x] z42.net manifest 加 `z42.crypto` dep
- [x] tests/ws_handshake.z42:
  - 加 `test_handshake_bad_accept_throws_protocol_exception`
  - 加 `test_handshake_missing_accept_throws_protocol_exception`
  - 加 `test_computed_accept_matches_rfc_example` (RFC 6455 §1.3 example)
- [x] tests/ws_echo.z42, ws_ping_pong.z42, ws_close.z42, ws_fragmentation.z42:
  - server-side handshake helpers compute proper Accept from received key
- [x] GREEN: 15/15 WS tests pass

## 备注

- 5 test files needed updating because each one had inline server-side
  handshake — z42 doesn't auto-include sibling test files
- helpers had to be re-prefixed (_clFindKey, _frFindKey, _ppFindKey) in
  each file to avoid global name collision across compiled tests
