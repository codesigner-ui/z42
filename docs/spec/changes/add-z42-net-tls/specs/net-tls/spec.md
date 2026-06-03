# Spec: z42.net HTTPS (client)

## ADDED Requirements
### Requirement: HttpUrl accepts https
#### Scenario: https URL parses
- WHEN `HttpUrl.Parse("https://host/p")`
- THEN scheme=https, port defaults 443, no exception
#### Scenario: http unchanged
- WHEN `HttpUrl.Parse("http://host/p")` → port 80

### Requirement: HttpClient GET over TLS
#### Scenario: https GET succeeds with cert verification
- WHEN `new HttpClient().Get("https://<valid-cert host>/...")`
- THEN handshake verifies cert against bundled roots; returns StatusCode + Body
#### Scenario: invalid certificate rejected
- WHEN GET an https host with an untrusted/invalid cert
- THEN throws/returns a TLS error (no plaintext fallback)
#### Scenario: redirect over https
- WHEN an https GET returns 301/302 to another https URL
- THEN the existing redirect follower fetches the target over TLS

### Requirement: native TLS builtins
#### Scenario: connect → read/write → drop by slot
- WHEN `__net_tls_connect(host,443,timeout)` → slot; write request; read response; drop
- THEN bytes are TLS-encrypted on the wire; slot freed on drop

## Pipeline / 组件
- Runtime: corelib/tls.rs (+ mod.rs register) + vm_context tls slot table + Cargo deps
- stdlib: HttpUrl / TlsClient / TlsStream / HttpClient (z42.net)
