# Spec: HTTP/1.1 client (K3)

## ADDED Requirements

### Requirement: HttpClient.Get / Post — convenience methods

#### Scenario: GET returns 200 OK with body
- **WHEN** server replies `HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nHello`
- **THEN** `response.StatusCode == 200`, `response.ReasonPhrase == "OK"`, `response.IsSuccess() == true`, `response.BodyAsString() == "Hello"`

#### Scenario: 404 response decoded
- **WHEN** server replies `HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n`
- **THEN** `response.StatusCode == 404`, `response.ReasonPhrase == "Not Found"`, `response.IsSuccess() == false`, `response.Body.Length == 0`

#### Scenario: POST with JSON body echoed
- **WHEN** `client.PostString(url, "{\"hello\":\"world\"}", "application/json")` and server echoes body
- **THEN** request emits `Content-Type: application/json` + `Content-Length: 17` headers + body matches

### Requirement: Headers case-insensitive lookup

#### Scenario: GetHeader 大小写不敏感
- **WHEN** `headers.Get("content-type")` and `Get("Content-Type")` and `Get("CONTENT-TYPE")`
- **THEN** all return the same value

#### Scenario: Multi-value Add appends
- **WHEN** `headers.Add("Set-Cookie", "a=1"); Add("Set-Cookie", "b=2")`
- **THEN** `headers.Count() == 2`, `Get("Set-Cookie") == "a=1"` (first match)

#### Scenario: Set replaces all
- **WHEN** `headers.Add("X", "1"); Set("x", "2")`
- **THEN** `headers.Get("X") == "2"`, `Count() == 1`

### Requirement: Transfer-Encoding chunked incoming

#### Scenario: Multi-chunk response
- **WHEN** server replies with `Transfer-Encoding: chunked` + `"7\r\nHello, \r\n6\r\nWorld!\r\n0\r\n\r\n"`
- **THEN** `response.BodyAsString() == "Hello, World!"`, `Body.Length == 13`

#### Scenario: Chunk extensions stripped
- **WHEN** chunk size line is `"a;ext=ignored"` (a = 0xa = 10)
- **THEN** decoder reads 10 bytes for that chunk, ignoring extensions

### Requirement: URL parsing + scheme gating

#### Scenario: https:// throws NotSupportedException
- **WHEN** `client.Get("https://example.com")`
- **THEN** throws `NotSupportedException` mentioning `add-z42-net-tls`

#### Scenario: Malformed URL throws FormatException
- **WHEN** `client.Get("not-a-url")` (no scheme)
- **THEN** throws `FormatException`

#### Scenario: Unknown scheme throws FormatException
- **WHEN** `client.Get("ftp://example.com")`
- **THEN** throws `FormatException`

### Requirement: Auto-injected headers

#### Scenario: Host / User-Agent / Connection: close auto-added
- **WHEN** no explicit Host / User-Agent / Connection
- **THEN** request emits `Host: <host[:port if non-80]>`, `User-Agent: z42-http/0.1`, `Connection: close`

#### Scenario: Content-Length auto-added for body
- **WHEN** request has `Body != null`
- **THEN** request emits `Content-Length: <body.Length>`

## Pipeline Steps

- [x] Lexer — 无变化
- [x] Parser / AST — 无变化
- [x] TypeChecker — 无变化
- [x] IR Codegen — 无变化
- [x] VM interp — 无变化 (pure script)

## No new VM builtins

K3 builds entirely on `Std.Net.Sockets.TcpClient` from K1. No new
`__net_*` builtin. The entire HTTP/1.1 wire format (request serialise +
response parse + chunked decode) lives in `z42.net/src/Http/*.z42`.
