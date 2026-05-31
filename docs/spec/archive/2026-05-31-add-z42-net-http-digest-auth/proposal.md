# Proposal: HttpClient Digest authentication (RFC 7616 + RFC 2617)

## Why

z42.net `net-future-http-auth` Deferred 行：Basic / Bearer 已落地
2026-05-27 (`HttpRequest.WithBasicAuth` / `WithBearerToken`)；**Digest
仍延后**。Digest 是 legacy 但极其常见 —— 几乎所有路由器 admin
（Cisco / Mikrotik / Ubiquiti）、老版 Apache `htdigest` realm、若干
古老 REST API（Splunk, Bitbucket Server, JIRA Data Center）默认走
Digest-MD5。

Basic + Bearer 用 `HttpRequest.WithBasicAuth(user, pass)` 是 stateless
"在第一个 request 就贴 Authorization header"；**Digest 是 challenge-
response**：

1. Client 发 unauthenticated request
2. Server 返 401 + `WWW-Authenticate: Digest realm="...", nonce="...",
   qop=auth, algorithm=MD5`
3. Client 计算 `response = MD5(HA1:nonce:nc:cnonce:qop:HA2)`，重发带
   `Authorization: Digest username="...", realm="...", nonce="...",
   uri="...", response="...", qop=auth, nc=..., cnonce="..."`

这要求 HttpClient 在 401 时 parse challenge → compute → auto-retry，与
redirect / cookie auto-replay 同一形态 (already in HttpClient pipeline).

不做：Digest 是 RFC 7235 framework 的 builtin；任何想跟老 device admin /
legacy 内网 service 交互的脚本都被卡，调用方手算 Digest 极易写错（nc
counter / cnonce 生成 / quote escape 全是雷）。

## What Changes

### z42.net 新 API

```z42
public class HttpRequest {
    public HttpRequest WithDigestAuth(string user, string password);   // NEW
}
```

`WithDigestAuth` 仅记录 credentials；不在 first request 时贴 header
（与 Basic 不同 — Digest 需要 server 的 nonce 才能 compute response）。

### HttpClient 401 auto-retry

`HttpClient.Send` 既有 redirect / cookie loop：

```
loop {
    send request
    if 3xx + SetMaxRedirects → reissue at new URL
    else break
}
```

扩展为：

```
loop {
    send request
    if 401 + has Digest credentials + first attempt:
        parse WWW-Authenticate Digest challenge
        compute response (MD5 or SHA-256 per algorithm=)
        reissue with Authorization: Digest <fields>
    if 3xx + SetMaxRedirects → reissue at new URL
    else break
}
```

仅允许一次 401 retry（避免无限循环 — server 故意一直 401）；第二次仍
401 则把 401 response 透传给调用方。

### Digest 计算算法

按 RFC 7616 §3 + RFC 2617 §3.2.2.1：

- `HA1 = H(username:realm:password)` (algorithm `MD5` / `MD5-sess` /
  `SHA-256` / `SHA-256-sess`)
- `HA2 = H(method:uri)` (qop=auth) 或 `H(method:uri:H(body))` (qop=auth-int)
- `response = H(HA1:nonce:nc:cnonce:qop:HA2)`
- `cnonce` = 16 random bytes hex-encoded（用 `Std.Crypto.SecureRandom`）
- `nc` = 8-hex monotonic counter starting at `00000001`

v0 支持：

- `algorithm=MD5` (default) ✅
- `algorithm=SHA-256` (RFC 7616) ✅
- `qop=auth` (most common) ✅
- 仅一次 retry（不维护 per-realm cnonce state across requests，每次
  401 重起 cnonce + nc=00000001 即可对绝大多数 server 工作）

v0 不支持（留 follow-up `net-future-http-digest-extras`）：

- `qop=auth-int` (body integrity；少用，需要 Body H())
- `algorithm=MD5-sess` / `SHA-256-sess` (session variant；几乎无 server 用)
- `algorithm=SHA-512-256` (RFC 7616；几乎无服务器实现)
- `userhash=true` (RFC 7616 §3.4.4 user privacy)
- "stale=true" challenge auto-rehandshake（细 corner case，server 重发
  401 with new nonce — v0 调用方手处理）
- `Proxy-Authenticate` + `Proxy-Authorization` (proxy chain)

### MD5 前置依赖

✅ `add-md5-to-crypto` (2026-05-31) 同日落地 — 提供 `Md5.Hash` +
`HmacMd5`. Digest 直接用 `Md5.HashStringHex` 即可。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.net/src/Http/HttpRequest.z42` | MODIFY | `WithDigestAuth(user, password)` + 内部 `_digestUser` / `_digestPass` 字段 |
| `src/libraries/z42.net/src/Http/HttpClient.z42` | MODIFY | `_sendOnce` 加 Digest 401 retry 分支 + Digest challenge parser + response builder |
| `src/libraries/z42.net/z42.net.z42.toml` | MODIFY（如需）| z42.crypto 已是 dep；无新增 |
| `src/libraries/z42.net/tests/http_digest_md5.z42` | NEW | RFC 2617 §3.5 example round-trip 单测 |
| `src/libraries/z42.net/tests/http_digest_sha256.z42` | NEW | RFC 7616 §3.9.1 example 单测 |
| `docs/design/stdlib/net.md` | MODIFY | Deferred 行 Digest 部分标 ✅；API 表加 WithDigestAuth |
| `docs/design/stdlib/roadmap.md` | MODIFY | Deferred Backlog Index 同步 |

**只读引用**：

- `src/libraries/z42.crypto/src/Md5.z42`（HA1/HA2/response 用）
- `src/libraries/z42.crypto/src/Sha256.z42`（algorithm=SHA-256 用）
- `src/libraries/z42.crypto/src/SecureRandom.z42`（cnonce 用）
- `src/libraries/z42.net/src/Http/HttpClient.z42` redirect / cookie loop（实施模板）
- `src/libraries/z42.encoding/src/Hex.z42`（cnonce hex encoding）

## Out of Scope

- v0 不支持的 Digest 变体（auth-int / sess / SHA-512-256 / userhash / stale rehandshake）
- Proxy Digest (`Proxy-Authenticate`)
- Per-realm cnonce / nc 持久化跨多 request（state-keeping —— 多数 server 不严格要求；留 follow-up）

## Open Questions

- [ ] cnonce 长度：固定 16 bytes（32 hex chars）— RFC 不严格规定；与
      curl 等主流 client 一致
- [ ] Default algorithm 处理：server 不指明 `algorithm=` 时按 RFC 2617
      §3.2.2 默认 MD5 — 实施按此约定
- [ ] 多 challenge 同时返回（罕见）：仅取首个 Digest scheme 处理，忽略
      Basic / Negotiate 等
