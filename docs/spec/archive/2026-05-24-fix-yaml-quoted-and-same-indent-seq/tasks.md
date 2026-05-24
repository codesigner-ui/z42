# Tasks: fix yaml block-context quoted scalars + same-indent sequence-under-mapping

> 状态：🟢 已完成 | 创建：2026-05-24 | 归档：2026-05-24
> 类型：fix (parser bugs in z42.yaml)

**变更说明**：The yaml parser had two bugs that survived the char-comparison
VM fix:

1. **Block-context quoted scalars not dispatched**:
   `YamlValue.Parse("\"hello\"")` returned the literal string `"hello"`
   (with quotes) instead of `hello`. `_ParseBlockValue` /
   `_ParseBlockMapping` value path / `_ParseBlockSequence` item path
   read the line via `_ConsumeLinePlainScalar` + `_ParseScalarLiteral`
   without checking for `"` / `'` opening — the quoted-string parsers
   (`_ParseDoubleQuotedString` / `_ParseSingleQuotedString`) existed
   but were only called from `_ParseMappingKey` / `_ParseFlowKey` /
   `_ParseFlowValue`. Affected 13 quoted-string tests + 3 unterminated-
   quote error tests (which couldn't fire their error because the
   quote-string parser never ran).

2. **Sequence-under-mapping at same indent rejected**: YAML allows
   `key:\n- item\n- item` where the sequence starts at the SAME indent
   as the mapping key. The YamlWriter emits sequences this way (see
   `YamlWriter.z42:110` "sequence keeps mapping's indent for `-`"),
   but the parser's empty-inline-then-next-line branch required
   `nextIndent > indent`, so a same-indent sequence got treated as
   `key: null` followed by orphan `-` lines → "unexpected trailing
   content after document" error. Affected `test_stringify_sequence_round_trip`.

**根因 #1**：missing dispatch — quoted-string parsers existed but only
the flow / key code paths called them.

**根因 #2**：parser overly strict — required deeper indent for block
value, but YAML spec permits sibling-indent for sequence-under-mapping.

**修复**：
1. New helper `_ParseBlockScalarValue` checks first char for `"` / `'`,
   dispatches to existing quoted parsers, else falls through to
   `_ConsumeLinePlainScalar` + `_ParseScalarLiteral`. Called from 4
   block-context sites (top-level value, mapping value, sequence
   inline-mapping value, sequence item).
2. In `_ParseBlockMapping`'s empty-inline-then-next-line branch, add
   a check: if `nextIndent == indent` AND the line starts with `-`
   followed by WS / EOL, dispatch to `_ParseBlockSequence(nextIndent)`
   instead of returning `OfNull`.

**文档影响**：none beyond the spec archive — z42.yaml's public API is
unchanged, just less buggy.

## Tasks

- [x] 1.1 MODIFY `src/libraries/z42.yaml/src/YamlParser.z42`:
      - NEW private helper `_ParseBlockScalarValue` (dispatches on
        `"` / `'` then falls through to plain)
      - 4 call sites updated to use it
      - `_ParseBlockMapping` empty-inline-value branch: same-indent
        sequence dispatch
- [x] 2.1 `./scripts/test-stdlib.sh z42.yaml` — all 64 tests now pass
      (was 16 failing before this fix)
- [x] 2.2 `./scripts/test-stdlib.sh z42.json` / `z42.toml` — still all
      green (no regression)
- [x] 3.1 Archive + commit + push
