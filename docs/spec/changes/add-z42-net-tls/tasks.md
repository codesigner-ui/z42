# Tasks: HTTPS for z42.net
> 状态：🟡 进行中（代码完成，GREEN 待并行 string-interning 重构修好后跑）| 创建：2026-06-03 | 类型：vm + stdlib（完整流程，安全敏感）

## 阶段 1: 运行时 TLS
- [x] 1.1 Cargo.toml: rustls + webpki-roots（默认 ring 后端）
- [x] 1.2 vm_context: tls_sockets slot table + alloc/get/drop（tls_socket_slot_count）
- [x] 1.3 corelib/tls.rs: __net_tls_connect / read / write / set_*_timeout / drop（SNI + webpki-roots 验证 + 握手超时）
- [x] 1.4 mod.rs 注册 __net_tls_*（6 个 builtin）
- [x] 1.5 Rust 单测：6 个（连接拒绝 / 坏 SNI / 未知 slot / drop 幂等）全绿 + 1 个 `#[ignore]` 真实握手已手动验证（example.com:443 完整 GET 往返）

## 阶段 2: z42.net 接线
- [x] 2.1 HttpUrl: 接受 https + 443 默认（http 仍 80）
- [x] 2.2 TlsClient + TlsStream（over __net_tls_*，镜像 TcpClient/NetworkStream；NetTlsNative externs 内联在 TlsClient.z42 复用 NetTcpDecode）
- [x] 2.3 HttpClient._sendOnce: scheme → TCP/TLS 选择（_sendOverTls 缓冲整 body、无 pool）；提取 _postProcessResponse 共享 gzip/brotli + cookie；_hostHeaderFor 处理 https 默认端口；SendStreaming over https 抛 NotSupported（_HttpBodyStream 绑 TcpClient，延后）
- [x] 2.4 z42.net [Test]: http_https_url.z42（https URL parse + 默认端口 + streaming-throws，确定性无网络）+ http_url_scheme_errors.z42（重命名自 http_https_throws，删除已过时的 https-throws 断言）。真实端点 GET 由 Rust ignored 测试 + launcher install 集成覆盖

## 阶段 3: 收尾
- [ ] 3.1 集成验证：`z42 install` 经 HTTPS 拉取 GitHub release（**无需改 launcher.z42** —
      `_cmdInstall` 已调用 `HttpClient.Get`，原阻塞正是 HttpClient 抛 NotSupported；
      launcher.z42 属并行 `add-z42-install` spec 的 Scope，本 spec 不动它）
- [ ] 3.2 GREEN：cargo build + test-vm + test-stdlib(z42.net)
- [x] 3.3 docs/design/stdlib/net.md TLS 段（usage + architecture + rationale + 4 项 known-limitation 延后）+ roadmap Deferred 索引行

## 备注
- **GREEN 阻塞（外部）**：并行 session 的 `add-string-literal-interning-phase1` 重构给
  `bytecode::Module` 加了 `interned_strings` 字段但尚未接线全部构造点
  （zbc_reader.rs / merge.rs），共享工作树 cargo build 失败。非本 spec 文件，
  属中断条件（外部回归）。待其修好后跑 GREEN。我的 Rust TLS 层在干净树上已验证通过。
- **z42 子类→基类 upcast 不对称**：`NetworkStream→Stream` 在实参位自动 upcast 通过，
  但 `TlsStream→Stream` 报 E0402。临时用 `(Stream)tstream` 显式 cast 绕过；
  build 恢复后需确认 cast 是否真修复，若否则属编译器 bug（出 Scope，停下汇报）。
