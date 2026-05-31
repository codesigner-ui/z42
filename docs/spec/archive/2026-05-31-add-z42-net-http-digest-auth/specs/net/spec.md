# Spec: HttpClient.WithDigestAuth + 401 challenge-response

## ADDED Requirements

### Requirement: HttpRequest.WithDigestAuth(user, password)

#### Scenario: 仅记录 credentials，first request 不带 Authorization

- **GIVEN** `var r = new HttpRequest("GET", "http://example/").WithDigestAuth("alice", "pwd")`
- **WHEN** 检查 `r.Headers.Contains("Authorization")`
- **THEN** `false`（与 Basic 不同 — Digest 须先收 server challenge）

### Requirement: 401 Digest challenge 自动 retry

#### Scenario: MD5 algorithm RFC 2617 §3.5 example round-trip

- **GIVEN** server 第一次返回
  ```
  HTTP/1.1 401 Unauthorized
  WWW-Authenticate: Digest realm="testrealm@host.com", qop="auth",
                    nonce="dcd98b7102dd2f0e8b11d0f600bfb0c093",
                    opaque="5ccc069c403ebaf9f0171e9517f40e41"
  ```
- **WHEN** client 用 `WithDigestAuth("Mufasa", "Circle Of Life")` 发 `GET /dir/index.html`
- **THEN** client 自动重发，第二次 request 的 Authorization header 形如
  ```
  Authorization: Digest username="Mufasa", realm="testrealm@host.com",
                 nonce="dcd98b7102dd2f0e8b11d0f600bfb0c093",
                 uri="/dir/index.html", qop=auth, nc=00000001,
                 cnonce="<16-byte-hex>",
                 response="<MD5(HA1:nonce:nc:cnonce:qop:HA2)>",
                 opaque="5ccc069c403ebaf9f0171e9517f40e41"
  ```
  其中 `HA1 = MD5("Mufasa:testrealm@host.com:Circle Of Life")`，
  `HA2 = MD5("GET:/dir/index.html")`，`response` 按上述 hash 链 RFC 2617
  §3.2.2.1 计算

### Requirement: SHA-256 algorithm（RFC 7616）

#### Scenario: WWW-Authenticate 含 `algorithm=SHA-256` → 用 SHA-256 替代 MD5

- **GIVEN** server 返 `WWW-Authenticate: Digest ..., algorithm=SHA-256, ...`
- **WHEN** client 计算 response
- **THEN** 所有 hash 调用走 `Sha256.HashStringHex` 而非 `Md5.HashStringHex`

#### Scenario: SHA-256 RFC 7616 §3.9.1 example round-trip

- **GIVEN** RFC 7616 §3.9.1 worked example：
  username="Mufasa", password="Circle of Life",
  realm="http-auth@example.org", nonce="7ypf/xlj9XXwfDPEoM4URrv/xwf94BcCAzFZH4GiTo0v",
  uri="/dir/index.html", method=GET, qop=auth, algorithm=SHA-256
- **WHEN** client 计算 response with cnonce="f2/wE4q74E6zIJEtWaHKaf5wv/H5QzzpXusqGemxURZJ"
  + nc=00000001
- **THEN** response 等同 RFC 7616 §3.9.1 给的值

### Requirement: 至多 retry 一次

#### Scenario: 第二次仍 401 → 透传 401 给调用方

- **WHEN** 第一次 401 触发 retry，第二次 server 又 401
- **THEN** HttpClient 不再 retry，返回最后那个 401 给调用方（不抛）

#### Scenario: 无 Digest credentials → 不 retry

- **WHEN** 401 但 `_digestUser` 未设
- **THEN** 不 retry，401 直接返回

### Requirement: 多 challenge 同时返回时优先 Digest

#### Scenario: 同 WWW-Authenticate 返 Basic + Digest

- **GIVEN** `WWW-Authenticate: Basic realm="x", Digest realm="x" nonce="y"`
  （RFC 7235 允许逗号分隔多 scheme）
- **WHEN** client 有 Digest credentials
- **THEN** 走 Digest 路径，忽略 Basic

### Requirement: cnonce 与 nc 生成

#### Scenario: cnonce 是 16 随机字节的 lowercase hex

- **WHEN** 每次构造 Authorization header
- **THEN** cnonce 字段长度 32 + chars ∈ [0-9a-f]；与上次值不同（randomness）

#### Scenario: nc 是 8-hex monotonic 从 00000001 起

- **WHEN** 第一次 retry
- **THEN** `nc=00000001`

### Requirement: 算法默认 MD5（RFC 2617 §3.2.2 兼容）

#### Scenario: WWW-Authenticate 无 algorithm= → 按 MD5 算

- **GIVEN** challenge 不含 `algorithm=`
- **WHEN** 计算 HA1 / HA2 / response
- **THEN** 用 `Md5.HashStringHex`

### Requirement: 不支持的 algorithm 抛 HttpProtocolException

#### Scenario: algorithm=SHA-512-256（v0 不支持）

- **GIVEN** challenge 含 `algorithm=SHA-512-256`
- **WHEN** client 尝试 retry
- **THEN** 抛 `HttpProtocolException`，message 含 "unsupported Digest algorithm 'SHA-512-256'"

### Requirement: qop=auth-int 抛 HttpProtocolException

#### Scenario: server 仅提供 auth-int

- **GIVEN** challenge `qop="auth-int"`（v0 仅 auth 支持）
- **WHEN** client 尝试 retry
- **THEN** 抛 `HttpProtocolException`，message 含 "unsupported Digest qop 'auth-int'"

## MODIFIED Requirements

### Requirement: HttpClient._sendOnce loop 加 Digest 401 retry 分支

**Before:**

`_sendOnce` 只做 redirect-follow + cookie-jar replay loop。

**After:**

`_sendOnce` 内增加：401 response + `_digestUser != null` + 未 retry 过
Digest → parse `WWW-Authenticate: Digest ...` → compute Authorization →
reissue 同 URL。仅一次。

## IR Mapping

无 — 纯 stdlib。

## Pipeline Steps

- [ ] Lexer — N/A
- [ ] Parser / AST — N/A
- [ ] TypeChecker — N/A
- [ ] IR Codegen — N/A
- [ ] VM interp — N/A
