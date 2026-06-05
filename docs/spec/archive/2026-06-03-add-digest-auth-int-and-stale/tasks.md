# Tasks: Digest auth-int + stale=true support

> 状态：🟡 进行中 | 创建：2026-06-03 | 类型：stdlib feat

**变更说明：** Extend the existing `WithDigestAuth` flow with two of
the RFC 7616 / RFC 2617 "extras" most likely to surface in real
deployments: `qop=auth-int` (body-integrity-protected variant) and
`stale=true` (server-signalled nonce rotation, no user re-prompt).

**原因：** Some enterprise APIs (banking middleware, internal SSO
proxies) require `auth-int`. Long-lived sessions hitting servers
with short nonce windows trip `stale=true` on every Nth request;
without handling it the client gives up after one 401 retry.

**还在延后的 Digest 拓展** (split as separate spec ID `net-future-
http-digest-extras-residual`): MD5-sess / SHA-256-sess (session-
keyed A1 — uncommon in practice), SHA-512-256 (RFC 7616 §3 but no
real-world server enforces), userhash (privacy variant — rare),
proxy-Digest (Proxy-Authorization / Proxy-Authenticate — separate
flow).

## Tasks
- [ ] 1.1 Change `_buildDigestAuthHeader` signature to accept
      `byte[] body` (currently ignored). When `qop=auth` (the
      default), `body` is unused. When `qop=auth-int`,
      HA2 = H(method:uri:H(body)).
- [ ] 1.2 qop selection: still prefer `auth` if offered; otherwise
      take `auth-int` if it's the only one offered (currently
      throws); otherwise error.
- [ ] 1.3 Update the caller in `Send` to pass `cur.Body` (zero-byte
      array for body-less requests still hashes correctly —
      H("") is well-defined for both MD5 and SHA-256).
- [ ] 1.4 stale=true handling: when first 401 retry itself returns
      401 with a `Digest` challenge containing `stale=true`, do
      ONE more retry with the fresh nonce/cnonce from the new
      challenge. Cap total Digest attempts at 2 retries (3 total
      requests) to prevent infinite loops on misconfigured servers.
- [ ] 1.5 Tests in `http_digest_auth.z42`:
      - `test_digest_auth_int_qop_succeeds` — server offers
        `qop=auth-int` only; client computes HA2 = H(...:H(body))
        and the request succeeds
      - `test_digest_auth_int_prefers_auth_when_both_offered` —
        server offers `qop="auth,auth-int"`; client picks `auth`
      - `test_digest_stale_true_triggers_second_retry` — server
        responds 401 first (issues nonce X), then 401-stale-true
        (issues fresh nonce Y), then 200; client uses Y
      - `test_digest_max_retries_exhausted_returns_401` — server
        always replies 401 (no stale flag on 2nd); after 2 retries
        client surfaces the 401 to the caller without further
        looping
- [ ] 1.6 Doc sync: `docs/design/stdlib/net.md` digest-extras entry
- [ ] 1.7 `./scripts/test-all.sh` — full GREEN
- [ ] 1.8 Commit + push
