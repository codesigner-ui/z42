# Tasks: add z42.net HTTP server thread-per-accept (follow-up)

> 状态：🟢 已完成 | 创建：2026-05-26 | 类型：feat (stdlib extension)
> Spec 类型：minimal mode

## 备注 (实施期发现)

- z42 IR verifier rejects nested try/catch in `Thread.Start(() => {...})`
  closures (register-flow check: empty catch blocks failed verification).
  Extracted the worker body to `_workerHandle(workerPeer, handler)`
  instance method; lambda becomes a thin `this._workerHandle(...)` call.
- `server.Stop()` doesn't unblock an in-progress `AcceptTcpClient()` on
  macOS — closing the TcpListener fd doesn't interrupt the blocking
  syscall. Tests work around by connecting one probe `TcpClient` AFTER
  Stop() to push the accept loop forward; probe-connect failure also OK
  (means accept already exited). True interruption needs
  `add-z42-net-timeout` / non-blocking sockets, a separate concern.

**变更说明**：在 `Std.Net.Http.HttpServer` 加 `ServeThreaded(handler)`方法 — 每接受一个 TCP connection 就 spawn 一个 worker thread 处理它，主 accept loop 不阻塞。基础 `Serve` (single-threaded) 保留作为 simpler-cost 选项。

**Why**: 顺手扩展刚 ship 的 `add-z42-net-http-server`。现实场景中即便是低流量服务，一个慢 client 也会卡住所有后续请求。Thread-per-accept 是最简单的并发改进，z42.threading 已提供。

**模式**:
```z42
var server = new HttpServer();
server.Bind("127.0.0.1", 0);
server.ServeThreaded((HttpServerContext ctx) => {
    // 每个 request 在独立 thread 上跑；handler 必须 thread-safe（不共享 mutable state 或自己加锁）
    ctx.SendText(200, "Hello, world!");
});
```

## API 变化

```z42
public class HttpServer {
    // ...existing API...

    /// Accept loop with thread-per-connection. Each `AcceptTcpClient` result
    /// is handed to a fresh `Std.Threading.Thread` running the handler.
    /// Main thread continues accepting while workers process.
    public void ServeThreaded(Action<HttpServerContext> handler);
}
```

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.net/src/Http/HttpServer.z42` | MODIFY | 加 `ServeThreaded` 方法 |
| `src/libraries/z42.net/tests/http_server_threaded.z42` | NEW | 并发 client 测试 — N 个 client 同时发请求都得到响应 |
| `docs/design/stdlib/net.md` | MODIFY | flip `add-z42-net-http-server-threaded` Deferred → ✅ |
| `docs/design/stdlib/roadmap.md` | MODIFY | same |

## 设计

- 主 thread `AcceptTcpClient()` 循环，每个 accept spawn worker thread + continue
- Worker thread 持有 TcpClient，调用 `_dispatchOne` (现有 helper) 走 handler，最后 `peer.Dispose()`
- Stop() 路径不变 — closes listener → main accept 抛 → main thread return
- 不 join workers — fire-and-forget；workers 自然 drain after request done

## Out of scope

- Worker pool (bounded number of threads + queue) — 现 v0 是 unbounded
  spawn；DoS 风险存在但 acceptable trade-off for v0；follow-up
  `add-z42-net-http-server-pool` 处理
- Per-request timeout — `net-future-timeout` 跨越多个 spec
- TLS — `add-z42-net-tls`

## Tasks

- [x] 1.1 HttpServer.z42: 加 `ServeThreaded` 方法
- [x] 2.1 tests/http_server_threaded.z42: spawn N clients 并行请求 +
      验证所有响应正确
- [x] 3.1 docs update
- [x] 4.1 build + test
- [x] 5.1 commit + push + archive
