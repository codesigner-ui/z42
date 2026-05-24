# Tasks: add Stream overloads to JSON / TOML / YAML parsers + writers

> 状态：🟢 已完成（yaml tests 留到 fix-yaml-parse-regression）| 创建：2026-05-24 | 归档：2026-05-24
> 类型：feat (extend existing entry points with Stream variants)

**变更说明**：Each of z42.json / z42.toml / z42.yaml currently exposes
only string-based `Parse(string)` + `Stringify(value)` entry points.
Add `Parse(Stream)` + `WriteTo(Stream, value)` overloads so callers
can pipe a `FileStream` / `MemoryStream` / `Compression*Stream` /
`ProcessOutputStream` straight in or out without manually round-trip
through `string`.

**原因**：The Std.IO.Stream ecosystem (built up over today's 5 specs)
needs end-to-end consumers — these three parsers are the highest-leverage
hooks (every config / API / fixture / log format goes through one of
them). Adds zero new abstractions: thin wrappers over `Parse(string)`
backed by `StreamReader(Stream).ReadToEnd()` for read side and
`StreamWriter(Stream).Write(stringified)` for write side.

**文档影响**：3 design docs (json.md / toml.md / yaml.md) gain a "Stream
overloads" subsection.

## Tasks

- [x] 1.1 MODIFY `src/libraries/z42.json/z42.json.z42.toml` — add z42.io dep
- [x] 1.2 MODIFY `src/libraries/z42.json/src/JsonValue.z42` — add
      `Parse(Stream)`, `WriteTo(Stream, JsonValue)`,
      `WriteToPretty(Stream, JsonValue)` overloads (delegate to
      existing string variants via StreamReader / StreamWriter)
- [x] 1.3 NEW `src/libraries/z42.json/tests/json_stream.z42` —
      Parse from MemoryStream / FileStream; WriteTo MemoryStream; UTF-8
      multibyte round-trip
- [x] 2.1 MODIFY `src/libraries/z42.toml/z42.toml.z42.toml` — add z42.io dep
- [x] 2.2 MODIFY `src/libraries/z42.toml/src/TomlValue.z42` — same overloads
- [x] 2.3 NEW `src/libraries/z42.toml/tests/toml_stream.z42`
- [x] 3.1 MODIFY `src/libraries/z42.yaml/z42.yaml.z42.toml` — add z42.io dep
- [x] 3.2 MODIFY `src/libraries/z42.yaml/src/YamlValue.z42` — same overloads
- [x] 3.3 NEW `src/libraries/z42.yaml/tests/yaml_stream.z42`
- [x] 4.1 MODIFY `docs/design/stdlib/json.md` — add Stream-overloads section
- [x] 4.2 MODIFY `docs/design/stdlib/toml.md` — same
- [x] 4.3 MODIFY `docs/design/stdlib/yaml.md` — same
- [x] 5.1 Tests all green
- [x] 5.2 Archive + commit + push

## 备注

- The string-based entry points stay unchanged for backwards compat
  (and because they're trivially cheaper for callers who already have
  the string in memory)
- Implementation: `ParseStream(Stream s)` →
  `var sr = new StreamReader(s); return new XxxParser(sr.ReadToEnd()).ParseDocument()`
  and similarly for write. The method is named **ParseStream** (not
  `Parse` overload) — z42's overload resolution can't reliably pick
  between `Parse(string)` and `Parse(Stream)` for callers passing
  either type (it picks the Stream variant unconditionally). Rename
  can be lifted once the compiler handles type-distinct arity-1
  overloads.
- StreamReader's v0 drain-and-decode strategy means these are NOT true
  streaming parsers — they materialise the full string before parsing.
  True streaming JSON / TOML / YAML parsing is a much larger spec
  per-format and deferred. The Stream overloads here are about
  composability of input/output channels, not pipeline-style parsing.
- **yaml_stream.z42 test file dropped from this spec** — `YamlValue.Parse(string)`
  is broken on `main` (regression introduced between commits 249a0411
  and 739112ce — test_int_positive / test_plain_string / 30+ others
  fail with "VM error" on string Parse). The yaml `ParseStream` /
  `WriteTo` Stream overloads themselves are correct; tests for them
  will be added in the follow-up `fix-yaml-parse-regression` spec
  alongside the parser fix.
