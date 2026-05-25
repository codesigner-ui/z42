# Tasks: add z42.net HTTP/1.1 client (K3)

> 状态：🟢 已完成 | 创建：2026-05-25 | 类型：feat (stdlib pure-script extension)
> Spec 类型：完整流程

## 进度

- [x] HttpMethod / HttpStatusCode / HttpException / HttpProtocolException
- [x] HttpUrl parser (http:// only, https:// throws NotSupportedException)
- [x] HttpHeaders case-insensitive (raw `string[]` storage + count, z42 不允许 field generic type params)
- [x] HttpRequest builder + HttpResponse 容器
- [x] HttpClient.Get / Post / PostString / Send
- [x] `_serialiseRequest` — wire format with Host + Content-Length + Connection: close auto-injection
- [x] `_readResponse` — status line + headers + body (Content-Length / Transfer-Encoding chunked / read-until-EOF)
- [x] `_readChunkedBody` — RFC 7230 §4.1 chunked decoder (with extensions)
- [x] `_LineReader` internal helper — CRLF/LF tolerant + buffered read-ahead
- [x] Tests: 13 z42 (http_get_basic 2 / http_post 1 / http_headers 7 / http_chunked 2 / http_https_throws 3)
- [x] 验证 GREEN: 39/39 z42.net tests pass

## 实施期发现

1. **z42 field generic type params 丢弃**：`private List<string> _keys;` parser
   silently drops `<string>` → field becomes raw `List`. Hit when refactoring
   HttpHeaders. Workaround per `Std.Toml.TomlValue` pattern: use raw arrays +
   `_count` field for manual capacity management.

2. **`Count` method needs `()` to invoke**：`while (i < headers.Count)` treats
   `Count` as method reference (closure value), error
   `"type mismatch in comparison: I64(0) vs Closure { fn_name: '..._Count$0__' }"`.
   Fix: `headers.Count()`.

3. **没有 List<byte[]> / List<int>**：z42 `List<T>` requires T: IEquatable +
   IComparable. byte[] doesn't satisfy → no `List<byte[]>`. Chunked decoder
   used a single growing byte[] instead (capacity 2× growth + finalise-trim).
   Line reader switched to growing char[] for the same reason.

## Out of scope (now follow-up specs)

- `add-z42-net-tls` — TLS over TcpClient + https:// scheme support
- `add-z42-net-http-keepalive` — Connection: keep-alive + connection pool
- `add-z42-net-http-stream-body` — `HttpResponse.GetStream() → Stream`
- `add-z42-net-http-redirects` — auto-follow 301/302/303/307/308
- `add-z42-net-http-cookies` — Set-Cookie + cookie jar
- `add-z42-net-http-auth` — Basic / Bearer / Digest helpers
- `add-z42-net-http-compression` — Accept-Encoding + auto-gzip-decode
- `add-z42-net-http-server` — `HttpListener` / `HttpServer`
- `add-z42-net-http2` — HTTP/2 binary framing + HPACK
