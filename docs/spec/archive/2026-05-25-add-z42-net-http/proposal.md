# Proposal: add z42.net HTTP/1.1 client (K3)

## Why

K1+K2 shipped TCP+UDP. HTTP/1.1 is the next-level abstraction users want for
fetching URLs / making API requests. .NET `HttpClient` / Python `requests`
对标。

Pure-script over existing `TcpClient` (K1) — no new VM builtin. http://-only
v0 (no TLS). https:// throws `NotSupportedException` until `add-z42-net-tls`
ships.

## What Changes

新增 `Std.Net.Http` namespace + 类:
- `HttpClient` — top-level facade
- `HttpRequest` / `HttpResponse` — request/response containers
- `HttpMethod` — static constants (Get / Post / Put / Delete / Patch / Head / Options)
- `HttpHeaders` — header dict (case-insensitive key lookup)
- `HttpStatusCode` — common status code constants
- `HttpException` — base HTTP exception

V0 features:
- `HttpClient.Get(url) → HttpResponse` — convenience GET
- `HttpClient.Post(url, body, contentType) → HttpResponse` — convenience POST
- `HttpClient.Send(HttpRequest) → HttpResponse` — full control
- Headers: parse + emit; case-insensitive lookup
- Body: Content-Length OR chunked transfer encoding
- Status line + status code + reason phrase
- URL parsing: scheme://host[:port][/path][?query]
- Auto Content-Length on outgoing body (no chunked for v0 outgoing)
- Connection: close (single-request-per-connection v0; keep-alive 是 follow-up)

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.net/src/Http/HttpClient.z42` | NEW | top-level Send / Get / Post |
| `src/libraries/z42.net/src/Http/HttpRequest.z42` | NEW | method / url / headers / body 容器 + builder pattern |
| `src/libraries/z42.net/src/Http/HttpResponse.z42` | NEW | statusCode / reasonPhrase / headers / body 容器 |
| `src/libraries/z42.net/src/Http/HttpHeaders.z42` | NEW | case-insensitive header dict |
| `src/libraries/z42.net/src/Http/HttpMethod.z42` | NEW | static const strings |
| `src/libraries/z42.net/src/Http/HttpStatusCode.z42` | NEW | static const ints |
| `src/libraries/z42.net/src/Http/HttpException.z42` | NEW | base exception (namespace Std) |
| `src/libraries/z42.net/src/Http/HttpProtocolException.z42` | NEW | malformed response / unexpected EOF |
| `src/libraries/z42.net/src/Http/HttpUrl.z42` | NEW | URL parser (internal helper class) |
| `src/libraries/z42.net/tests/http_get_basic.z42` | NEW | in-process listener+pure-z42 HTTP server reply 200 OK with body |
| `src/libraries/z42.net/tests/http_post.z42` | NEW | POST with body + Content-Type echoed |
| `src/libraries/z42.net/tests/http_headers.z42` | NEW | case-insensitive header lookup |
| `src/libraries/z42.net/tests/http_chunked.z42` | NEW | server sends chunked response; client decodes |
| `src/libraries/z42.net/tests/http_https_throws.z42` | NEW | https:// scheme throws NotSupportedException |
| `src/libraries/z42.net/README.md` | MODIFY | HTTP section |
| `docs/design/stdlib/net.md` | MODIFY | flip K3 Deferred → ✅ landed; HTTP API + decisions |
| `docs/design/stdlib/roadmap.md` | MODIFY | net-future-http → ✅ |

## Out of Scope

- **HTTPS / TLS** — `add-z42-net-tls` 独立 spec (rustls / OpenSSL cdylib)
- **HTTP/2 / HTTP/3** — 独立 spec，需要 binary framing + HPACK / QPACK
- **Keep-alive / connection pooling** — v0 single request per TcpClient；follow-up
- **Cookies / sessions** — follow-up (`add-z42-net-http-cookies`)
- **Streaming body** (`HttpResponse.GetStream() → Stream`) — v0 一次性读 body 到 byte[]；follow-up
- **Chunked outgoing transfer** — v0 outgoing 总走 Content-Length
- **Auth (Basic / Bearer / Digest)** — 用户层自己设 Authorization header；专门 helpers 是 follow-up
- **Async** — depends L3 async/await
- **Compression** (gzip / br on Accept-Encoding) — follow-up；用户可 z42.compression 手动解压
- **Redirect following** (301/302/303/307/308) — v0 直接返回 redirect response；用户层 follow；automatic follow 是 follow-up
- **Timeout** — `net-future-timeout` 已是单独 spec，HTTP 复用
- **HTTP server** — 独立 spec `add-z42-net-http-server`
