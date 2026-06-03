# Design: HTTPS for z42.net

## Architecture
```
HttpClient.Get("https://host/path")
  → HttpUrl.Parse: scheme=https, port 443
  → _sendOnce: scheme==https ? TlsClient : TcpClient
       TlsClient.Connect(host,443) → __net_tls_connect → (Rust) TcpStream::connect
            + rustls ClientConnection (SNI=host, RootCertStore=webpki-roots, verify ON)
            → StreamOwned<ClientConnection,TcpStream> in VmCore.tls_sockets slot
       TlsStream.Write/Read → __net_tls_socket_write/read (slot) → rustls encrypt/decrypt
  → existing _buildBodyStream framing (chunked / content-length / eof) unchanged
```

## Decisions
### D1: rustls (pure-Rust) over native-tls/OpenSSL
No system OpenSSL dependency; static, cross-platform; aligns with
project_supported_platforms (vendor-maintained, no extra system libs).

### D2: webpki-roots (bundled Mozilla roots) over system cert store
Deterministic + zero per-OS cert-store integration; roots ship in the binary,
refreshed via dep bump. Trade: not honoring enterprise/system-added CAs (v1).

### D3: TLS as its own builtins + slot table (mirror TCP), not retrofitting TCP
`__net_tls_*` + `VmCore.tls_sockets: Mutex<HashMap<u64, TlsConn>>`. Clean
separation from raw TCP; same slot-id discipline; `Send+Sync` like tcp sockets.

### D4: z42.net wiring — TLS-backed Stream, reuse framing
`TlsStream : Stream` (Read/Write over `__net_tls_socket_*`) so `_buildBodyStream`
+ `_HttpBodyStream` work identically. `_sendOnce` branches once on scheme.

## Implementation Notes
- `__net_tls_connect(host, port, timeoutMs)`: resolve+TCP connect (reuse the tcp
  path), build `rustls::ClientConfig` (RootCertStore from webpki-roots, no client
  auth), `ServerName` = host (DNS), `ClientConnection::new`, wrap in
  `StreamOwned`; perform handshake (complete_io) honoring timeout; store slot.
- read/write: `StreamOwned` impls `Read`/`Write`; map to byte-array builtins like
  `__net_tcp_socket_read/write`. WouldBlock/timeouts → same socket-error shape.
- non-test code: no `unwrap()`; TLS/cert errors → `socket_err`-style typed result.
- HttpUrl: default port 443 for https, 80 for http.

## Testing Strategy
- Rust unit: tls connect to a known https host (or a local rustls test server) →
  handshake ok; bad host / cert → error path.
- z42 [Test] / e2e: `HttpClient.Get("https://<stable endpoint>")` → 200 + body;
  redirect (already supported) over https.
- Integration: `z42 install nightly` over real HTTPS → runtime installed.
- GREEN: cargo build + test-vm + test-stdlib (z42.net).
