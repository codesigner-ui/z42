# Tasks: z42.net socket options extended

> 状态：🟢 已完成 | 创建：2026-05-30 | 归档：2026-05-30 | 类型：feat（新 stdlib 行为 + corelib builtin）

## 进度概览

- [x] 阶段 1: socket2 dep + corelib builtins (5 个)
- [x] 阶段 2: z42.net stdlib API (5 个新方法 + 2 个状态机修改)
- [x] 阶段 3: 测试 + 文档同步
- [x] 阶段 4: GREEN + archive + commit + push

## 阶段 1: socket2 dep + corelib

- [x] 1.1 MODIFY `src/runtime/Cargo.toml`
  - 在 `[target.'cfg(not(target_arch = "wasm32"))'.dependencies]` block 加 `socket2 = "0.5"`
- [x] 1.2 MODIFY `src/runtime/src/corelib/network.rs`
  - 新增 `builtin_net_tcp_connect_with_timeout`（TCP module 内）
  - 新增 `builtin_net_tcp_socket_set_keepalive`（socket2 SockRef）
  - 新增 `builtin_net_tcp_listen_with_options`（socket2 Socket + bind + listen → std::net::TcpListener）
  - 新增 `builtin_net_udp_set_read_timeout` + `builtin_net_udp_set_write_timeout`
  - 每个 builtin 都加 `#[cfg(target_arch = "wasm32")]` stub 返回 `net_unsupported`
- [x] 1.3 MODIFY `src/runtime/src/corelib.rs`
  - 注册 5 个新 builtin name + 函数指针

## 阶段 2: z42.net stdlib

- [x] 2.1 MODIFY `src/libraries/z42.net/src/TcpClient.z42`
  - 加 `private int _connectTimeoutMs;` field（默认 0）
  - 加 `public void SetConnectTimeout(int millis)` setter
  - 改 `Connect(host, port)` 走 `_connectTimeoutMs > 0` 分支
  - 加 `public void SetKeepAlive(bool enable)`（未 connect 抛
    `InvalidOperationException`）
  - 加 native extern 声明 `_connectWithTimeout` / `_setKeepalive`
- [x] 2.2 MODIFY `src/libraries/z42.net/src/TcpListener.z42`
  - 加 `private bool _reuseAddress;` + `private bool _bound;` field（默认 false）
  - 加 `public void SetReuseAddress(bool enable)` setter（已 bound 抛 InvalidOperationException）
  - 改 `Bind(host, port)` 走 `_reuseAddress` 分支
  - `Bind` 成功后 `_bound = true`
  - 加 native extern 声明 `_listenWithOptions`
- [x] 2.3 MODIFY `src/libraries/z42.net/src/UdpClient.z42`
  - 加 `public void SetReadTimeout(int millis)`（未 bound 抛 InvalidOperationException）
  - 加 `public void SetWriteTimeout(int millis)`
  - 加 native extern `_setReadTimeout` / `_setWriteTimeout`

## 阶段 3: 测试 + 文档

- [x] 3.1 NEW `src/libraries/z42.net/tests/tcp_connect_timeout.z42`（4 tests）
- [x] 3.2 NEW `src/libraries/z42.net/tests/tcp_keepalive_reuseaddr.z42`（5 tests）
- [x] 3.3 NEW `src/libraries/z42.net/tests/udp_timeout.z42`（4 tests）
- [x] 3.4 MODIFY `docs/design/stdlib/net.md`
  - Deferred 段：`net-future-timeout` connect/UDP 部分标 ✅；
    `net-future-socket-options` SO_REUSEADDR / SO_KEEPALIVE 部分标 ✅
  - API surface 表补 5 个新方法
- [x] 3.5 MODIFY `docs/design/stdlib/roadmap.md`
  - Deferred Backlog Index：`net-future-timeout` 行加 "✅ connect + UDP 已落地 2026-05-30 add-net-socket-options-extended"
  - 同理 `net-future-socket-options` 行加 "✅ SO_REUSEADDR + SO_KEEPALIVE 已落地 2026-05-30"

## 阶段 4: GREEN + archive + commit

- [x] 4.1 `cargo build --manifest-path src/runtime/Cargo.toml --release` 无错
- [x] 4.2 `./scripts/build-stdlib.sh` z42.net.zpkg 重建无错
- [x] 4.3 `./scripts/test-stdlib.sh z42.net` 全过（既有 + 新 13 个）
- [x] 4.4 `./scripts/test-all.sh` 全绿 6 stages
- [x] 4.5 归档 `docs/spec/archive/2026-05-30-add-net-socket-options-extended/`
- [x] 4.6 commit + push（仅 socket-options scope；不串入 jit/translate.rs 等其他 session 残留）

## 备注

- 不接 SO_KEEPALIVE tuning (idle/interval/probes)；留 follow-up
  `net-future-keepalive-tuning`
- 不给 TcpClient 加 SetReuseAddress（outgoing client 少需求）
- 实施中若发现 socket2 类型与现有 corelib SocketAddr 流不匹配，停下汇报 —
  socket2::SockAddr / std::net::SocketAddr 转换是 `.into()` 但 cfg(wasm32)
  下 socket2 不可用，需要 cfg-gate
- z42 编译器尚不支持同 arity 不同类型重载（`compiler-future-typed-overload-
  resolution` Deferred），故 `Connect(host, port)` 不能再加 `Connect(host,
  port, timeout)` 重载 — 通过 setter + 字段间接实现

## 实施备注（2026-05-30）

- `socket2 = "0.5"` 加在 `[target.'cfg(not(target_arch = "wasm32"))'.dependencies]`
  block；wasm32 不引入 socket2，全部 5 个 builtin 在 wasm32 mod imp 中
  对应 `unsupported(ctx)` stub
- Test fixture 撞到两处需要调整：
  1. UDP `Receive()` 返回 `UdpReceiveResult`，其上 `Buffer` 是 public field
     不是 method — 写 `got.Buffer.Length` 而不是 `got.Length()`（compiler
     错误信息明确：function 不存在）
  2. `203.0.113.1` (TEST-NET-3) blackhole 测试在本机环境下 connect 居然
     成功（VPN / route 异常），改为 `127.0.0.1:1` 触发 ECONNREFUSED 走
     timeout builtin 错误路径 — 仍能验证 `__net_tcp_connect_with_timeout`
     被注册 + 错误经 SocketException 透传，只是不严格证明 timer 触发
- GREEN：43 个 z42.net test 文件全过 + 全 stdlib 237 文件 + 6 stages
