# Tasks: add z42.uri

> 状态：🟢 已完成 | 创建：2026-05-15 | 完成：2026-05-15 | 类型：feat（纯脚本 stdlib，无新 VM/IR）
> Spec 类型：minimal mode

## 背景

stdlib roadmap P2 表里的 `z42.uri`：URL / URI 解析、构造、编码 / 解码。先于
`z42.net`、与 `z42.json`（URI 字段）并用。RFC 3986 子集，覆盖最常用 HTTP / file URI。

C# `System.Uri` + Rust `url` 对标，但本期最小 API：parse + encode/decode +
component accessors。Builder 模式（mutator）留 follow-up，rare needs。

## 设计决策

| Decision | 选项 | 决定 | 理由 |
|----------|------|------|------|
| 1. 类型 | (a) 不可变 Uri class / (b) builder + frozen | (a) | parse 入 -> immutable struct-like class，C# parity |
| 2. 解析严格度 | (a) RFC 3986 严格 / (b) WHATWG URL Standard | (a) | RFC 简单；WHATWG 更宽松但状态机大。L1 选 RFC |
| 3. URI vs URL | (a) 区分 / (b) 统一为 `Uri` | (b) | 用户场景几乎都是 URL；URN 极少；统一类减歧义 |
| 4. 错误 | UriException | yes | 同 TomlException / JsonException 模式（在 `Std` namespace） |
| 5. 字符集 | (a) UTF-8 encoding/decoding / (b) ASCII only | (a) | 现代 URI 必须支持 UTF-8 percent-encoding；codepoint by codepoint |
| 6. Default port | (a) 显式存 / (b) 推断 | (a) | `Port=-1` 表示未指定；同 C# `Uri.Port == -1` |

## 阶段 1: 包骨架

- [x] 1.1 NEW `src/libraries/z42.uri/z42.uri.z42.toml` — manifest（dep on z42.core + z42.text for StringBuilder）
- [x] 1.2 NEW `src/libraries/z42.uri/src/Uri.z42`
  - `namespace Std.Uri;`
  - `class Uri` 字段：`Scheme / Authority / UserInfo / Host / Port / Path / Query / Fragment`
  - `static Uri.Parse(string text)`
  - `static string EncodeComponent(string s)` — percent-encode unreserved 外字符
  - `static string DecodeComponent(string s)` — `%xx` → bytes → UTF-8 string
  - 访问器: `GetScheme() / GetHost() / GetPath()` ...
  - `override string ToString()` — 重组 canonical 形式
- [x] 1.3 NEW `src/libraries/z42.uri/src/UriException.z42` — `class UriException : Exception` in `Std` namespace

## 阶段 2: 测试

- [x] 2.1 NEW `tests/parse_basic.z42` — common HTTP URLs / file URI / opaque (mailto:) / relative (留 deferred)
- [x] 2.2 NEW `tests/encoding.z42` — percent-encode + decode round-trip, UTF-8, reserved chars
- [x] 2.3 NEW `tests/parse_errors.z42` — malformed input → UriException

## 阶段 3: Wiring + docs

- [x] 3.1 MODIFY `src/libraries/z42.workspace.toml` 加 `"z42.uri"`
- [x] 3.2 MODIFY `scripts/build-stdlib.sh` 加 LIBS + index.json `Std.Uri`
- [x] 3.3 NEW `src/libraries/z42.uri/README.md`
- [x] 3.4 NEW `docs/design/stdlib/uri.md`
- [x] 3.5 MODIFY `docs/design/stdlib/roadmap.md` + `organization.md` + `src/libraries/README.md`

## 阶段 4: GREEN + 归档

- [x] 4.1 `./scripts/build-stdlib.sh` 全绿
- [x] 4.2 `./scripts/test-stdlib.sh z42.uri` 全绿
- [x] 4.3 `./scripts/test-stdlib.sh` 整体不回归
- [x] 4.4 mv → `docs/spec/archive/2026-05-15-add-z42-uri/`
- [x] 4.5 commit + push

## 实施期发现

1. **`file:///path` 需独立 `_hasAuthority` 标志**：最初用 "host 非空 → 有 authority"
   推断，`file:///tmp/foo.txt` 解析后 host 是空字符串，重组时丢了 `//` 变成
   `file:/tmp/foo.txt`。修正：parser 显式记 `bool _hasAuthority`，ToString 据此
   决定是否 emit `//`。所有 9 个 Uri 字段相关 accessor 同步增加 `HasAuthority()`。
2. **RFC 3986 §3.2.2 允许空 host**：测试初版 `test_empty_host_throws` 实为反规范行为；
   改为 `test_empty_host_allowed`，断言 `Parse("https:///")` 返回 host="" + hasAuthority=true
   的合法实例。
3. **`@` 在 authority 段内才算 userinfo 分隔**：`https://host/path?ref=a@b` 中的
   `@` 不能误识别。`UriParser._findInAuthority` 限定 scan 到 `/`/`?`/`#` 终止。
4. **`UriException` 必须在 `Std` namespace**：在 `Std.Uri` 写 `: Exception` 时
   TypeChecker 找不到基类（同 TomlException / JsonException 模式）。所有 stdlib
   exception 类一律 `namespace Std;`。
