# z42.uri — RFC 3986 URI parser + percent codec

> 落地版本：2026-05-15（add-z42-uri）
> 包路径：`src/libraries/z42.uri/`
> 命名空间：`Std.Uri`（`UriException` 在 `Std`）

## 职责

将 URI / URL 字符串解析为结构化组件（scheme / userinfo / host / port / path /
query / fragment），并提供 percent-encoding / decoding helper。RFC 3986 子集，
覆盖最常用 HTTP / file / opaque URI 形态。

**对标**：C# `System.Uri`（不可变 + 组件 accessor）+ Rust `url` crate（最小 API
集合）。本期不做 WHATWG URL Standard（状态机大、行为更宽松，留给 `z42.net` /
浏览器场景）。

## API surface

```z42
// Parsing
Uri.Parse(string text) → Uri        // UriException on malformed input

// Accessors (immutable)
GetScheme()    → string
GetUserInfo()  → string             // "" if absent
GetHost()      → string             // "" if absent or empty (file:///)
GetPort()      → int                // -1 if not specified
GetPath()      → string
GetQuery()     → string             // 不含 leading '?'
GetFragment()  → string             // 不含 leading '#'

// Has-checks
HasAuthority() → bool               // true 当 URI had '//'  after scheme
HasUserInfo() / HasHost() / HasPort() / HasQuery() / HasFragment()

// Reconstruction
override string ToString() → string // canonical form, round-trip safe

// Percent codec (static, on Uri class)
Uri.EncodeComponent(string s) → string   // unreserved kept, others → %XX
Uri.DecodeComponent(string s) → string   // %XX → bytes → UTF-8 string
```

## 设计决策

| Decision | 选项 | 决定 | 理由 |
|----------|------|------|------|
| 1. 类型 | (a) 不可变 class / (b) builder + frozen | (a) | parse 入 → immutable，无 mutator API |
| 2. 解析严格度 | (a) RFC 3986 严格 / (b) WHATWG URL | (a) | RFC 简单；WHATWG 状态机大，留 z42.net |
| 3. URI vs URL 类区分 | (a) 区分 / (b) 统一 `Uri` | (b) | URN 极少；统一减歧义，C# parity |
| 4. 错误类型 | UriException 在 `Std` namespace | yes | 同 TomlException / JsonException 模式 |
| 5. 字符集 | (a) UTF-8 / (b) ASCII only | (a) | 现代 URI 必须支持 UTF-8 percent-encoding |
| 6. Default port | (a) 显式存 / (b) 推断 | (a) | `Port=-1` 表未指定，同 C# `Uri.Port == -1` |
| 7. `//` 是否存在 | (a) `_hasAuthority` 显式 / (b) 由 host 非空推断 | (a) | `file:///path` 和 `mailto:foo` 都有空 host 但语义不同；必须独立记录以保证 round-trip |
| 8. IPv6 字面量 | (a) parse 拆解 / (b) host 当字符串保留 | (b) | `[::1]` 原样保留在 host；IPv6 拆解留 future |
| 9. Percent-encode 字符集 | (a) RFC 2.3 unreserved / (b) RFC 3986 reserved-aware | (a) | `A-Za-z0-9_.~-` literal，其它全 encode；调用方自行避免 encode `/` |
| 10. Decode 失败行为 | (a) skip 坏 % / (b) 抛 UriException | (b) | fail-fast，避免静默数据损坏 |

## 实现结构

```
src/Uri.z42  (~430 行)
├── class Uri             — immutable 值对象，8 字段 + accessors + ToString
├── class UriParser       — 手写递归下降 parser，cursor 风格
└── class UriCodec        — encode / decode 静态方法

src/UriException.z42  (~13 行)
└── class UriException : Exception  in Std namespace
```

Parser 流程（RFC 3986 §3 形态）：

```
URI = scheme ":" hier-part [ "?" query ] [ "#" fragment ]

hier-part =
   "//" authority path-abempty       ← 设 _hasAuthority = true
 / path-rootless                     ← opaque (mailto:, urn:)
 / path-empty

authority = [ userinfo "@" ] host [ ":" port ]
```

`UriParser._findInAuthority('@')` 限定在 authority 段内（`/`, `?`, `#` 之前）
寻找 `@`，避免 `https://host/path?ref=a@b` 误判 `userinfo`。

## 不支持（Deferred）

### ~~uri-future-resolve~~ — ✅ 已落地

`Std.Uri.Uri.Resolve(string base, string ref) → string` — RFC 3986 §5
relative-reference resolution. Handles `../`, absolute paths,
network-relative `//host`, scheme-relative refs, fragments. Pure
script.

### uri-future-ipv6-parse

- **来源**：`https://[::1]:8443/` host 部分拆为 8×16-bit 组件
- **触发原因**：v0 host 当 opaque 字符串保留即可满足重组 / 比较
- **触发条件**：z42.net socket API 需要 IPv6 字面量验证
- **当前 workaround**：调用方自行调 `parseIpv6(uri.GetHost())`

### uri-future-iri-idn

- **来源**：IRI（RFC 3987 国际化）/ IDN punycode（`xn--` 域名）
- **触发原因**：punycode 算法 (~150 行) + Unicode 归一化（NFC）依赖 z42 还没有
  完整 Unicode database
- **触发条件**：用户出现非 ASCII 域名场景

### uri-future-builder

- **来源**：可变 builder pattern（`UriBuilder.WithHost("x").WithPort(443)`）
- **触发原因**：rare needs；不可变 + Parse 已覆盖 95% 场景；增加 API 表面积
- **当前 workaround**：拼接字符串再 Parse

### ~~uri-future-default-port~~ — **✅ 已落地 2026-05-26 (add-uri-default-port)**

Shipped: two helpers covering the explicit-vs-default port question:

- `uri.EffectivePort() → int` — explicit port if `HasPort()`, else
  the scheme's default; `-1` only when both are absent
- `Uri.DefaultPortFor(scheme) → int` — case-insensitive scheme →
  IANA default lookup; `-1` for unknown schemes. Static so callers
  can query without constructing a Uri

Schemes covered (~25): http(s), ws(s), ftp(s), ssh, sftp, telnet,
smtp(s), dns, tftp, gopher, pop3(s), ntp, imap(s), snmp, ldap(s),
redis, mongodb, postgresql, mysql. Curated for "what a realistic z42
app actually reaches for" — full IANA table (~400 entries) isn't
worth shipping in stdlib.

22 tests cover: explicit port overrides default; default ports for
common HTTP/WebSocket/SSH/FTP/DB schemes; mail-protocol sweep
(smtp/smtps/imap/imaps/pop3/pop3s); unknown-scheme returns -1;
explicit port on unknown scheme works; case-insensitive scheme
lookup; empty scheme returns -1.

## 跨 stdlib 交互

- 依赖 `z42.core`（基础类型 + Exception 基类）
- 依赖 `z42.text`（StringBuilder 用于 `ToString` 重组 + Encode 累加输出）
- 与 `z42.json` 互补：URI 作 JSON 字段时调用方手动 encode/decode；未来 z42.net
  收到 URL 时调用 `Uri.Parse`

## 实施期发现

1. **`file:///path` 需独立 `_hasAuthority` 标志**：最初用 "host 非空 → 有 authority"
   推断，`file:///tmp/foo.txt` 解析后 host 是空字符串，重组时丢了 `//` 变成 `file:/tmp/foo.txt`。
   修正：parser 显式记 `bool _hasAuthority`，ToString 据此决定是否 emit `//`。
2. **RFC 3986 §3.2.2 允许空 host**：测试初版 `test_empty_host_throws` 实为反规
   范行为；改为 `test_empty_host_allowed`，断言 `Parse("https:///")` 返回 host=""
   + hasAuthority=true 的合法实例。
3. **`@` 在 authority 段内才算 userinfo 分隔**：`https://host/path?ref=a@b`
   中的 `@` 不能误识别。`UriParser._findInAuthority` 限定 scan 到 `/`/`?`/`#` 终止。
4. **z42 quirk 复用**：z42.random 实施期已记录的几条（hex literal、`u32` 关键字、
   primitive array 不零初始化、`(int)long` cast 不截断）在本 spec 不再触发——
   URI parser 全程用 `string.Substring` + char 比较，避开了这些路径。
