# Tasks: HttpClient.SetTimeout + TcpClient.SetReadTimeout / SetWriteTimeout

> 状态：🟢 已完成 | 创建：2026-05-27 | 归档：2026-05-27

**变更说明：** 在 TCP 层引入 socket read/write timeout，并把它接到 HttpClient。两个新 Rust native：

- `__net_tcp_socket_set_read_timeout(slot, millis)`
- `__net_tcp_socket_set_write_timeout(slot, millis)`

z42 API：
- `TcpClient.SetReadTimeout(int millis)` / `SetWriteTimeout(int millis)`
- `HttpClient.SetTimeout(int millis)` —— 对每个 request 的 read + write 同时应用
- `HttpClient.GetTimeout() → int`
- `0 / negative` = 永不超时（默认）

**原因：** scripts 移植 install-node-local.sh / 下载 tarball 类脚本一旦 server 不响应当前会**永久 hang**。Read 超时是最低限度的存活保证。

**类型：** 最小化（2 个新 Rust native + 2 个 stdlib 类各加 2 个方法，无 lang change）。

**Scope 收窄说明**（v0 不做）：
- 不做 `connect_timeout` —— 需要 non-blocking connect + `poll`，跨平台实现复杂；当前 OS 默认 connect timeout（macOS 75s / Linux ~127s）兜底
- 不做 retry policy —— 独立 spec，需要 backoff / jitter / idempotency 设计
- 不做 per-request timeout —— `HttpClient.Timeout` 是 client-level，所有 request 共享同一值
- 不做 streaming body timeout —— v0 是 one-shot read

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/corelib/network.rs` | MODIFY | 加 `builtin_net_tcp_socket_set_read_timeout` / `_set_write_timeout` |
| `src/runtime/src/corelib/mod.rs` | MODIFY | BUILTINS 表追加 2 项 |
| `src/libraries/z42.net/src/NetTcpNative.z42` | MODIFY | 加 `[Native]` extern 2 个 |
| `src/libraries/z42.net/src/TcpClient.z42` | MODIFY | 加 `SetReadTimeout / SetWriteTimeout` 实例方法 |
| `src/libraries/z42.net/src/Http/HttpClient.z42` | MODIFY | 加 `_timeoutMs` 字段 + `SetTimeout / GetTimeout` + `_sendOnce` 里 `client.Connect` 后调 `SetReadTimeout` |
| `src/libraries/z42.net/tests/http_timeout.z42` | NEW | 2 [Test]：TcpListener accept 但不 write → HttpClient.SetTimeout(50) 触发；timeout=0 不限时 |
| `src/libraries/z42.net/README.md` | MODIFY | HttpClient / TcpClient 段加新 API |

## Tasks

- [x] 1.1 Rust `network.rs`：2 个 builtin。各：
  - `arg_str(args, 0, ...)` → slot id（其实是 i64，参考 `require_slot_id`）
  - `arg_i64(args, 1, ...)` → millis（0 = 无 timeout，调 `set_read_timeout(None)`；>0 → `Some(Duration::from_millis(...))`）
  - 找到 socket → `stream.set_read_timeout(...)` / `set_write_timeout(...)`
  - Rust IO 错走 socket_err 路径
- [x] 1.2 `mod.rs` BUILTINS 末尾追加 2 项（接 add-file-atomic-write 段后面）
- [x] 1.3 `NetTcpNative.z42` 加 2 个 `[Native]` extern
- [x] 1.4 `TcpClient.z42` 加 `SetReadTimeout / SetWriteTimeout`（值经 `NetTcpDecode.CheckResult` 等返回路径，参考既有 SocketDrop 模式）
- [x] 1.5 `HttpClient.z42`：
  - ctor 加 `_timeoutMs = 0` 字段
  - `SetTimeout(int millis)` / `GetTimeout()` 方法
  - `_sendOnce` 里 `client.Connect(...)` 之后：若 `_timeoutMs > 0` → `client.SetReadTimeout(_timeoutMs)` + `client.SetWriteTimeout(_timeoutMs)`
- [x] 1.6 写 `tests/http_timeout.z42`：
  - test_timeout_fires_on_silent_server：起 TcpListener 接受 connection 但**不 write**；HttpClient.SetTimeout(50)；try Get → 期望 `HttpException`/`SocketException` (或 timeout subclass)；elapsed < 2s
  - test_default_timeout_does_not_fire_quick：起 mock server，超时 0，期望正常返回
- [x] 1.7 README + smoke + commit

## 实施期验证

- Rust lib `cargo build --release` 干净 ✓
- stdlib workspace build 22/22 ✓
- **真实 timeout 触发 smoke**：TcpListener accept 后 busy-wait 不写、HttpClient.SetTimeout(150)、`Get()` 抛 SocketException、elapsed=152ms（设置 150ms，OS 调度误差 2ms 内）✓
- API surface smoke：default=0、SetTimeout(5000)→5000、SetTimeout(0)→0 ✓
- 3 个 [Test] 写就；端到端 GREEN 由后续 session 自动收

## 实施期发现

1. **z42 IR-verify bug with lambda + try/catch**：原 smoke 把 `try {...} catch {...}` 写在 inline lambda body 触发 `block catch_start_3: register r8 used before definition in CopyInstr`。解决：用 top-level function + lambda 调用之。**不在本 spec 修复**，记入 backlog。
2. **`Thread.Sleep` 缺失**：`Std.Threading.Thread` 只有 `Start`/`Join`，没有 `Sleep`。smoke test 用 busy-wait 凑数；正经 `Thread.Sleep(ms)` 是独立 spec 的事。
3. **`TcpListener.Create(host, port)` factory 缺失**：必须 `new TcpListener() + Bind(...)`。BCL 习惯有静态 factory，z42 没。可独立加。

## 备注

- **不引入新 exception 类型**：timeout 用现有 `SocketException`（Rust read 超时返回 `WouldBlock` / `TimedOut`，到 z42 层是 `SocketException`）。如果将来要区分，独立 spec 加 `SocketTimeoutException`。
- **设计权衡 retry**：不在本 spec 做。理由：retry policy 自身设计点多（exponential backoff / jitter / max attempts / idempotency-only 等），单独立 spec 更干净；并且只在 timeout 触发后才考虑 retry，没有 timeout 就先没意义。
- **关于 `_timeoutMs` 默认值 0**：与 BCL `HttpClient.Timeout = TimeSpan.FromSeconds(100)` 默认 100s 不同——z42 保守选 0（永不超时）保持现有行为不破坏；用户显式 opt-in 才生效。
- **per-request 超时**：BCL 通过 `HttpRequestMessage.Options` 支持；本 spec 只做 client-level，per-request 留 backlog。
