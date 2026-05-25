# Tasks: add z42.net UDP — datagram sockets

> 状态：🟢 已完成 | 创建：2026-05-25 | 类型：feat (vm + stdlib extension)
> Spec 类型：完整流程（lang/ir/vm — 新 VM builtin）

## 进度概览
- [x] 阶段 1: VM-side UDP builtin (Rust)
- [x] 阶段 2: Z42-side UDP classes
- [x] 阶段 3: 测试 + 文档同步
- [x] 阶段 4: 验证 + commit + archive

## 阶段 1: VM-side builtin (Rust)

- [x] 1.1 `src/runtime/src/vm_context.rs` MODIFY:
  - 加 `udp_sockets: Mutex<HashMap<u64, std::net::UdpSocket>>` (gated `cfg(not(wasm32))`)
  - 加 `next_udp_socket_id: AtomicU64`
  - 加 `alloc_udp_socket_slot` / `udp_socket_slot_count` helpers
- [x] 1.2 `src/runtime/src/corelib/network.rs` MODIFY:
  - 加 4 个 builtin (`__net_udp_bind` / `_send` / `_recv` / `_drop`)
  - 复用既有 `KIND_OK` / `KIND_SOCKET_ERR` / `KIND_HANDLE_INVALID` / `KIND_UNSUPPORTED` 常量
  - wasm32 fall-through 走既有 `unsupported(ctx)`
- [x] 1.3 `src/runtime/src/corelib/mod.rs` MODIFY:
  - BUILTINS 表追加 4 个 entry
- [x] 1.4 `src/runtime/src/corelib/network_tests.rs` MODIFY:
  - 加 UDP slot allocator + send/recv unknown slot + loopback round trip 5 个测试

## 阶段 2: Z42-side classes

- [x] 2.1 `src/libraries/z42.net/src/UdpReceiveResult.z42` NEW:
  - public class `UdpReceiveResult` with `byte[] Buffer`, `string RemoteHost`, `int RemotePort`
  - ctor takes all three
- [x] 2.2 `src/libraries/z42.net/src/UdpNative.z42` NEW:
  - extern wrappers for 4 builtins + `UdpDecode` decoder helpers (`ToSlot`, `ToBindResult`, `ToSendResult`, `ToReceiveResult`)
  - 复用 `NetTcpDecode.Throw` 的 kind-tag → exception 映射，或镜像同 pattern
- [x] 2.3 `src/libraries/z42.net/src/UdpClient.z42` NEW:
  - fields `_slotId`, `_bound`, `_disposed`, `_localPort`, `_bindHost`
  - methods `Bind(host, port)` / `Send(data, length, remoteHost, remotePort) → int` /
    `Receive() → UdpReceiveResult` / `LocalPort()` / `BindHost()` / `Dispose()` / `Close()`
  - auto-bind in `Send` if not yet bound (binds to `("0.0.0.0", 0)`)

## 阶段 3: 测试 + 文档同步

- [x] 3.1 `src/libraries/z42.net/tests/udp_loopback.z42` NEW:
  - 双 client 同进程: B.Send → A.Receive 拿到 data + B 的 LocalPort
  - Reply: A.Send 到 result.RemoteHost:RemotePort → B.Receive
  - 空 datagram round trip
  - Auto-bind on first Send 工作
- [x] 3.2 `src/libraries/z42.net/tests/udp_disposal.z42` NEW:
  - Dispose 幂等
  - Send after Dispose 抛 SocketClosedException
  - Receive after Dispose 抛
  - LocalPort before Bind 抛 InvalidOperationException
- [x] 3.3 `src/libraries/z42.net/README.md` MODIFY — UDP section
- [x] 3.4 `docs/design/stdlib/net.md` MODIFY:
  - `net-future-udp` Deferred → ✅ landed
  - 加 UDP API + decisions section
- [x] 3.5 `docs/design/stdlib/overview.md` MODIFY — z42.net catalog 加 UDP

## 阶段 4: 验证 + commit + archive

- [x] 4.1 `cargo build --release` 无 error
- [x] 4.2 `./scripts/build-stdlib.sh` z42.net 编译通过
- [x] 4.3 `./scripts/test-stdlib.sh z42.net` 既有 13 + 新 N 全过
- [x] 4.4 `cargo test --lib corelib::network` 全过 (TCP 7 + UDP 5)
- [x] 4.5 spec scenario 逐条覆盖
- [x] 4.6 single commit + push (含本 spec dir 含实现)
- [x] 4.7 mv → `docs/spec/archive/2026-05-25-add-z42-net-udp/`

## 备注 / 实施期发现

- 沿用 K1 TCP 的 kind-tagged Array 返回 shape，z42 端 decode 模式一致
- v0 不引入 Connect(host, port) — 总是 explicit per-Send remote；follow-up spec 处理
- **z42 cross-file class-token mismatch**：原 design 在 `UdpNative.z42` 里
  `UdpDecode.ToReceiveResult` 通过 `new UdpReceiveResult(...)` 构造 + 返回，
  从 `UdpClient.Receive()` 直接 forward。z42 编译器对 cross-file `class` reference
  产生两个不同 type token，错误信息 "expected `UdpReceiveResult`, got
  `UdpReceiveResult`"。Workaround：把 `new UdpReceiveResult(...)` 调用 inline 到
  `UdpClient.Receive()`（与 declared return type 在同一文件），`UdpDecode` 只暴露
  `Throw` 错误路径。这是 `compiler-future-typed-overload-resolution` 同根的另一面
  （symbol-collection 期类型 token 跨文件不一致），独立 follow-up
- 测试：13 z42 (loopback 5 + disposal 8) + 5 Rust unit，all green
