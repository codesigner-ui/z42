# Tasks: add z42.net HTTP server (K3 follow-up)

> 状态：🟢 已完成 | 创建：2026-05-25 | 类型：feat (stdlib pure-script extension)
> Spec 类型：minimal mode

## 备注 (实施期发现)

- z42 lambda 不能从 `Action<T>` 上下文推断参数类型 — handler 需要显式
  `(HttpServerContext ctx) => {...}` 注解。每个 test 都加了显式类型。
- HttpServer 走 `HandleNext` (single request) + `Serve` (loop until Stop)
  两个 entry。Stop() 关 listener → `AcceptTcpClient` 抛 SocketClosedException
  → Serve return。
- Handler 抛异常 / 漏 Send* 都触发 auto-500 via `_tryEmitErrorResponse`。
- 测试 12 通过：basic 5 + routing 4 + post 3。

**变更说明**：新增 `Std.Net.Http.HttpServer` + `HttpServerContext` + handler
delegate. Pure-script over K1 `TcpListener`. http:// only (https:// 等
`add-z42-net-tls`). Single-threaded blocking serve loop v0；one connection
at a time. Concurrency 走 follow-up `add-z42-net-http-server-threaded`
（用 `Std.Threading.Thread` 每 accept 一线程，简单但够用）。

## Why

K3 shipped HttpClient — symmetric piece is the server. Use cases:
- Local development server / API mock
- Embedded service in z42 process
- Webhook receiver
- Health check endpoint
- WebSocket server upgrade path (K4 only client)

## API

```z42
namespace Std.Net.Http;

public class HttpServer {
    public HttpServer();
    public void Bind(string host, int port);    // port 0 → OS assigns
    public int  LocalPort();
    public string BindHost();
    public void HandleNext(Action<HttpServerContext> handler);   // blocks for ONE request
    public void Serve(Action<HttpServerContext> handler);        // infinite loop until Stop
    public void Stop();                                          // signals serve loop to exit + closes listener
    public void Dispose();
}

public class HttpServerContext {
    public HttpRequest  Request;
    public HttpResponse Response;          // mutable; handler fills in
    public bool         ResponseSent;      // tracking flag
    public void Send(HttpResponse response);   // emits response on the wire + marks sent
    public void Send(int statusCode, string reasonPhrase, byte[] body, string contentType);   // convenience
    public void SendText(int statusCode, string text);   // text/plain UTF-8
    public void SendJson(int statusCode, string jsonBody);   // application/json
}
```

Handler pattern:
```z42
var server = new HttpServer();
server.Bind("127.0.0.1", 0);
int port = server.LocalPort();
server.Serve((ctx) => {
    if (ctx.Request.Url == "/hello") {
        ctx.SendText(200, "Hello, world!");
    } else {
        ctx.SendText(404, "Not Found");
    }
});
```

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.net/src/Http/HttpServer.z42` | NEW | server class — Bind / Serve / Stop |
| `src/libraries/z42.net/src/Http/HttpServerContext.z42` | NEW | per-request context |
| `src/libraries/z42.net/src/Http/_HttpRequestParser.z42` | NEW | server-side request parser (mirrors client response parser in HttpClient.z42) |
| `src/libraries/z42.net/tests/http_server_basic.z42` | NEW | client ↔ server round trip on loopback |
| `src/libraries/z42.net/tests/http_server_routing.z42` | NEW | handler dispatches by URL path |
| `src/libraries/z42.net/tests/http_server_post.z42` | NEW | handler reads request body |
| `src/libraries/z42.net/README.md` | MODIFY | server section |
| `docs/design/stdlib/net.md` | MODIFY | flip `net-future-http-server` Deferred → ✅ landed |
| `docs/design/stdlib/roadmap.md` | MODIFY | same |

## Out of scope

- **Multi-connection concurrency** — v0 serializes (one request at a time);
  threaded variant is `add-z42-net-http-server-threaded`
- **Keep-alive** — v0 always `Connection: close` per response
- **Streaming request body** — v0 reads full body upfront
- **WebSocket upgrade** — server side WS is `add-z42-net-websocket-server`
- **TLS** — `add-z42-net-tls`
- **Routing framework** (express-style middleware chain) — handler is single Action
- **Static file serving** — handler-builds-response only

## Tasks

- [x] 1.1 `_HttpRequestParser.z42` — parses request line + headers + body
- [x] 1.2 `HttpServerContext.z42` — fields + Send variants
- [x] 1.3 `HttpServer.z42` — Bind / Serve / Stop loop
- [x] 2.1 tests/http_server_basic.z42 — HttpClient.Get from in-process HttpServer
- [x] 2.2 tests/http_server_routing.z42 — handler dispatches by Request.Url
- [x] 2.3 tests/http_server_post.z42 — handler reads + echoes request body
- [x] 3.1 README + net.md + roadmap docs
- [x] 4.1 build + test
- [x] 4.2 commit + push + archive
