# Design: z42.net HTTP/1.1 client (K3)

## Architecture

```
USER CODE  ──►  HttpClient / HttpRequest / HttpResponse
                  (pure-script in z42.net namespace Std.Net.Http)
                                │
                                ▼
                  serialise request → TcpClient.Send
                  deserialise response ← TcpClient.GetStream().Read
                                │
                                ▼
                    Std.Net.Sockets.TcpClient (K1)
                    Std.Net.Sockets.NetworkStream
```

**No new VM builtin.** Pure script over K1 TCP. http:// only — https://
throws `NotSupportedException` (TLS layer is `add-z42-net-tls` follow-up).

## Decisions

### Decision 1: Pure-script over TcpClient

`HttpClient.Send(request)` 内部：
1. Parse URL → host / port / path
2. Open `new TcpClient()` + `Connect(host, port)`
3. Serialise request to bytes → `stream.Write(...)`
4. Read response: status line + headers + body via `stream.Read(...)` loop
5. Parse + return `HttpResponse`
6. Close TcpClient (connection: close v0)

No VM changes. All HTTP logic 落在 z42 source.

### Decision 2: Connection: close — single request per TCP connection

V0 不做 connection pooling / keep-alive. 每个 HTTP request 开一个 fresh
TcpClient, request, response, close. Inefficient for high-rate scenarios
but minimal and correct. Keep-alive 走 `add-z42-net-http-keepalive`.

### Decision 3: Body 一次性读到 byte[]

`HttpResponse.Body` 是 `byte[]`. 不暴露 streaming body API. 大 body (> 10MB)
场景需 streaming，留 `add-z42-net-http-stream-body` follow-up.

为啥不直接 streaming：
- 简化 v0
- 大多数 HTTP responses 小于 1MB
- API simpler — body 是 byte[] 直接给

### Decision 4: Builder pattern for HttpRequest

```z42
var req = new HttpRequest(HttpMethod.Post, "http://example.com/api");
req.SetHeader("Content-Type", "application/json");
req.SetHeader("Authorization", "Bearer xyz");
req.SetBody(jsonBytes);
var resp = client.Send(req);
```

vs immutable ctor with all fields — builder pattern more ergonomic for
optional headers / body. Headers backed by `HttpHeaders` case-insensitive
dict.

### Decision 5: Convenience Get / Post

90% use cases: `client.Get(url)` / `client.Post(url, body, contentType)`.
Builder pattern with `Send(HttpRequest)` is escape hatch for full control
(custom method / headers / etc.).

### Decision 6: Chunked transfer encoding — incoming only

V0 client must HANDLE chunked responses (servers commonly use it for
unknown-length responses) — decode chunked body during response read.

V0 client does NOT SEND chunked — outgoing requests always have
Content-Length (or no body for GET/HEAD). Outgoing chunked is rarely
needed (uploading unknown-length data with streaming) and is follow-up.

### Decision 7: HttpHeaders case-insensitive

HTTP headers are case-insensitive per RFC 7230 §3.2.
`HttpHeaders.Get("content-type")` and `Get("Content-Type")` return same
value. Internal storage: normalised lowercase keys + a List of `(key,
value)` pairs for iteration order preservation when emitting.

### Decision 8: HttpUrl — internal lightweight URL parser

z42.uri 包提供 generic URI parsing. HTTP need a narrower view (scheme +
host + port + path + query, no fragment for v0). Either:
- A: depend on z42.uri, adapt its API
- B: small purpose-built `HttpUrl` parser in z42.net.http

Chose **B** — z42.uri 当前 API 可能不够稳定 / 多了 follow-up risk. HTTP
need only 5-6 fields from URL; ~50 LOC parser inlined. Can migrate to
z42.uri later if z42.uri's API stabilises.

### Decision 9: HttpStatusCode int constants

`HttpStatusCode.Ok = 200`, `NotFound = 404` 等。Just a static class with
public int fields. Could be an enum if z42 had enums (`add-language-enums`
follow-up); for now static class fields are equivalent.

### Decision 10: https:// throws NotSupportedException at v0

URL parser detects scheme. If scheme == "https" → throw
`NotSupportedException` 提示 "HTTPS is not supported in z42.net v0; needs
add-z42-net-tls follow-up". 用户知道 path forward.

## Implementation Notes

### Request wire format

```
{METHOD} {PATH_AND_QUERY} HTTP/1.1\r\n
Host: {host[:port]}\r\n
{header_name}: {header_value}\r\n
...
Content-Length: {body_length}\r\n     # only if body
Connection: close\r\n
\r\n
{body bytes}
```

User-Agent default: `z42-http/0.1`. Host header auto-added from URL.
Content-Length auto-added if body present. Connection: close auto-added.

### Response parsing

State machine:
1. Read line by line until status line + all headers
2. Look at Transfer-Encoding / Content-Length to decide body strategy:
   - `Transfer-Encoding: chunked` → chunked decoder
   - `Content-Length: N` → read exactly N bytes
   - else (e.g. HTTP/1.0 server without Content-Length) → read until EOF
3. Return HttpResponse

Status line parse: `HTTP/1.1 200 OK\r\n` →
`(version, statusCode, reasonPhrase)`. Reject non-`HTTP/1.x` versions.

Header parsing:
- Read line until `\r\n\r\n` boundary
- Each header `name: value\r\n` — split on first `:`, trim value
- Continuation lines (RFC 7230 obs-fold) deprecated — reject with HttpProtocolException

### Chunked decoder

```
chunk_size_hex\r\n
chunk_data (chunk_size bytes)\r\n
0\r\n
trailing_headers (often empty)
\r\n
```

Loop: read line, parse hex size, read that many bytes + trailing CRLF.
Size 0 → end. Optional trailing headers read until empty line.

### Reading via NetworkStream

`stream.Read(buf, off, n)` may return short (< n) — UDP-like behavior on
TCP boundary. Wrap in a `_ReadExactly(stream, n) → byte[]` helper that
loops until n bytes accumulated or EOF.

Line reading: `_ReadLine(stream) → string` accumulates bytes until `\r\n`
boundary (handle CR + LF separately to be robust against `\n`-only
servers).

### Streaming buffer choice

Read 4KB chunks at a time into a growable accumulator (similar to
`Stream.ReadAllBytes`). Body finalised at known length OR EOF.

## Testing Strategy

### In-process server pattern

z42.net K1 tests use `Thread.Start` to spawn a TCP client in parallel.
HTTP tests use same pattern: a `Thread.Start` listener thread:
1. `TcpListener.Bind / AcceptTcpClient`
2. Read request bytes
3. Write canned response bytes
4. Close

Then main thread `HttpClient.Get(...)` and verifies response.

### Tests

1. `http_get_basic.z42`:
   - Server responds 200 OK with `Hello` body + Content-Length: 5
   - Client `.Get(url).StatusCode == 200`, `.ReasonPhrase == "OK"`, `.Body` matches
2. `http_post.z42`:
   - Server echoes request method + Content-Type + body in response body
   - Client posts JSON; response body contains expected echo
3. `http_headers.z42`:
   - Server response has `Content-TYPE: text/plain` (odd casing)
   - Client `response.Headers.Get("content-type") == "text/plain"`
4. `http_chunked.z42`:
   - Server responds with `Transfer-Encoding: chunked` + 2 chunks + 0\r\n\r\n
   - Client decodes correctly; body concatenated
5. `http_https_throws.z42`:
   - `client.Get("https://example.com")` throws NotSupportedException
   - Error message mentions `add-z42-net-tls`

### Rust unit tests

None needed — no VM changes.

## Deferred / Open

K3 cuts that become follow-up specs:
- `add-z42-net-http-keepalive` — Connection: keep-alive + pool
- `add-z42-net-http-stream-body` — `HttpResponse.GetStream() → Stream`
- `add-z42-net-http-redirects` — auto-follow 301/302/303/307/308
- `add-z42-net-http-cookies` — Set-Cookie parsing + Cookie jar
- `add-z42-net-http-auth` — Basic / Bearer / Digest helpers
- `add-z42-net-http-compression` — gzip / br Accept-Encoding + auto-decompress
- `add-z42-net-tls` — TLS over TcpClient (precondition for https://)
- `add-z42-net-http2` — HTTP/2 framing
- `add-z42-net-http-server` — `HttpListener` / `HttpServer`
- `add-z42-net-async` — async/await variant
