# Tasks: add HTTP cookies (Set-Cookie + cookie jar)

> 状态：🟢 已完成 | 创建：2026-05-26 | 类型：feat (stdlib extension)
> Spec 类型：minimal mode

## 备注 (实施期发现)

- z42 method resolver — `CookieJar.Count(long)` collided with
  `HttpHeaders.Count()` (no-arg). Calling `headers.Count()` somewhere triggered
  "missing argument 1 for `Std.Net.Http.CookieJar.Count` and no bound default"
  compiler crash. Renamed `Count` → `LiveCount(long nowUnixSec)` to disambiguate.
  Same class of bug as `compiler-future-typed-overload-resolution` Deferred —
  method-name-only resolution missing arity/parameter-type discrimination.
- All 3 test files green (20 tests).

**变更说明**：新增 `Std.Net.Http.{Cookie, CookieJar}` 类。客户端解析
Set-Cookie 响应头到 `CookieJar`；后续 request 自动从 jar 选适用 cookies
并 emit `Cookie:` header。Set-Cookie parsing + scope matching subset of
RFC 6265.

**Why**: HTTP without cookies can't speak to login-based / session APIs.
Cookies 在 web 通信中无处不在，jar pattern 是 .NET `CookieContainer` /
Python `requests.Session` 对标。

## API

```z42
namespace Std.Net.Http;

public class Cookie {
    public string Name;
    public string Value;
    public string Domain;
    public string Path;
    public bool   Secure;
    public bool   HttpOnly;
    public long   ExpiresUnixSec;   // 0 = session cookie (no expiry)

    public Cookie(string name, string value);

    /// True if `expiresUnixSec > 0 AND expiresUnixSec < nowUnixSec`.
    public bool IsExpired(long nowUnixSec);

    /// True if this cookie should be sent on a request to `(host, path, scheme)`.
    public bool Matches(string host, string path, bool isSecure);
}

public class CookieJar {
    public CookieJar();

    /// Parse all `Set-Cookie:` headers in a response and add to jar. Uses
    /// `responseHost`/`responsePath` as defaults for cookies missing Domain/Path.
    public void IngestFromResponse(HttpResponse response, string responseHost, string responsePath);

    /// Get cookies applicable to `(host, path, scheme)`. Returns wire-format
    /// `name1=value1; name2=value2` string, or empty string if none.
    public string CookieHeaderFor(string host, string path, bool isSecure);

    /// Add or replace a cookie matching (Name, Domain, Path) tuple.
    public void Add(Cookie cookie);

    /// Cookies currently stored (excluding expired). Cleanup expired on read.
    public int Count(long nowUnixSec);
}
```

**Auto-cookie path** (HttpClient extension): not in v0. Users must
manually call `jar.IngestFromResponse(...)` and `req.SetHeader("Cookie",
jar.CookieHeaderFor(...))`. Auto-jar is `add-z42-net-http-cookies-auto`
follow-up — needs `HttpClient` to carry a jar field. Keeps v0 surface small.

## Set-Cookie parsing (RFC 6265 §5.2 subset)

`Set-Cookie: NAME=VALUE; attr1=val1; attr2; attr3=val3`

Attributes (case-insensitive):
- `Expires=<HTTP-date>` — absolute expiry (parsed best-effort; if parse fails, treat as session)
- `Max-Age=<n>` — relative expiry in seconds (takes precedence over Expires per §4.1.2.2)
- `Domain=<domain>` — defaults to response host
- `Path=<path>` — defaults to response path's directory
- `Secure` (flag) — only send over HTTPS
- `HttpOnly` (flag) — informational (no DOM in z42; just preserves the bit)
- `SameSite=<lax|strict|none>` — v0 parses but doesn't enforce (no cross-origin model in HttpClient)

Multiple `Set-Cookie` headers in one response — handle each (HttpHeaders
already supports multi-value via Add).

## Matching (RFC 6265 §5.4)

- Domain match: cookie.Domain matches request host as suffix (case-insensitive)
- Path match: request path starts with cookie.Path + "/" OR equals cookie.Path
- Secure: if cookie.Secure → only send over HTTPS

v0 doesn't enforce SameSite (we don't have an origin concept).

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.net/src/Http/Cookie.z42` | NEW | cookie value record |
| `src/libraries/z42.net/src/Http/CookieJar.z42` | NEW | parse + storage + lookup |
| `src/libraries/z42.net/tests/http_cookie_parse.z42` | NEW | Set-Cookie parsing edge cases |
| `src/libraries/z42.net/tests/http_cookie_matching.z42` | NEW | path/domain/secure matching |
| `src/libraries/z42.net/tests/http_cookie_jar_e2e.z42` | NEW | end-to-end against HttpServer |
| `src/libraries/z42.net/README.md` | MODIFY | cookies section |
| `docs/design/stdlib/net.md` | MODIFY | flip Deferred → ✅ |
| `docs/design/stdlib/roadmap.md` | MODIFY | same |

## Out of scope

- **Auto-jar on HttpClient** — `add-z42-net-http-cookies-auto` follow-up
- **Persistent jar** (save/load to file) — `add-z42-net-http-cookies-persist`
- **SameSite enforcement** — no origin model v0
- **Public Suffix List** (eTLD+1 boundary protection) — `add-z42-net-http-cookies-psl`

## Tasks

- [x] 1.1 `Cookie.z42` NEW
- [x] 1.2 `CookieJar.z42` NEW — parse + storage + matching
- [x] 2.1 tests/http_cookie_parse.z42 — Set-Cookie variants (Max-Age, Expires, attrs, multi-value)
- [x] 2.2 tests/http_cookie_matching.z42 — domain suffix, path prefix, secure flag
- [x] 2.3 tests/http_cookie_jar_e2e.z42 — server sets cookie → jar ingests → client re-sends
- [x] 3.1 docs update
- [x] 4.1 build + test
- [x] 5.1 commit + push + archive
