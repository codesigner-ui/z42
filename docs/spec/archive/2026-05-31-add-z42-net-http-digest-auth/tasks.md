# Tasks: HttpClient Digest authentication

> 状态：🟢 已完成 | 创建：2026-05-31 | 归档：2026-05-31 | 类型：feat（新 stdlib 行为）

## 进度

- [x] 1.1 MODIFY `src/libraries/z42.net/src/Http/HttpRequest.z42` —
      加 `_digestUser` / `_digestPass` 字段 + `WithDigestAuth(user, password)`
- [x] 1.2 MODIFY `src/libraries/z42.net/src/Http/HttpClient.z42` —
      `_sendOnce` 401 检查；challenge parser + response builder + retry
- [x] 1.3 NEW `src/libraries/z42.net/tests/http_digest_md5.z42` —
      RFC 2617 §3.5 round-trip + 不 retry / 双 401 / 多 challenge 场景
- [x] 1.4 NEW `src/libraries/z42.net/tests/http_digest_sha256.z42` —
      RFC 7616 §3.9.1 worked example round-trip
- [x] 1.5 MODIFY `docs/design/stdlib/net.md` — Deferred 行 Digest 标 ✅；
      API 表加 `WithDigestAuth`
- [x] 1.6 MODIFY `docs/design/stdlib/roadmap.md` — Deferred Backlog Index 同步
- [x] 1.7 GREEN: `./scripts/build-stdlib.sh` + `./scripts/test-stdlib.sh z42.net`
      + `./scripts/test-all.sh`
- [x] 1.8 归档 → `docs/spec/archive/2026-05-31-add-z42-net-http-digest-auth/`
- [x] 1.9 commit + push

## 备注

- 实施前需 `add-md5-to-crypto` GREEN（同日落地，依赖关系链）
- challenge parser：tokenize `key=value` pairs + 处理 quoted-string
  (RFC 7235 ABNF) — 写一个 small `_DigestChallenge` 解析器；token68 复杂
  syntax 极少用，跳过
- response builder：hash chain 严格按 RFC 2617 §3.2.2.1 顺序；HA1/HA2 都
  hex-lowercase；qop 单字面 `auth`；nc 8-hex; cnonce 32-hex 随机
- v0 不维护跨 request nc 计数（每次 retry 起 00000001）— 简单又对绝大
  多数 server 工作；如果未来撞到严格 server 才 stateful
- cnonce 用 `Std.Crypto.SecureRandom.Bytes(16)` + `Hex.Encode`
- 测试用 mock TcpListener + 手写 HTTP/1.1 response（同 http_chunked.z42
  / http_brotli_decode.z42 模板）；server 端按 RFC 算预期 response 与
  client 实际发送的对比

## 实施备注（2026-05-31）

- 7 tests GREEN：MD5 round-trip + RFC 2617 §3.5 hash chain validation +
  no-credentials no-retry + at-most-one-retry + auth-int rejection + SHA-256
  round-trip with RFC 7616 §3.9.1 example + SHA-512-256 algorithm rejection
- Send loop 加 Digest 401 retry **在 redirect logic 之前** —— Digest 通常
  绑定 realm + URI，不应该 follow 3xx 跨 host 再带 credentials
- `digestRetried` flag 局部 var 在 `Send` body 内（不在 HttpClient 字段），
  并发 Send 调用各自独立
- `_parseDigestParams` 是手写 RFC 7235 ABNF 子集 tokenizer：quoted-string
  / token / 逗号分隔；不支持嵌套 `\"` escape（真实 Digest header 不出现）
- cnonce = `SecureRandom.GetBytes(16)` + `Hex.Encode` = 32 hex chars
- nc 每次 Send 起 `00000001` (no per-realm state) — 与 curl `--digest`
  非 `keep-alive` 模式一致；多数 server 不严格 enforce 单调性
- 不支持的 algorithm / qop 抛 `HttpProtocolException`，调用方接得住
