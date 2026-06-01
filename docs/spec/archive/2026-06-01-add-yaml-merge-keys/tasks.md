# Tasks: add YAML merge keys

> 状态：🟢 已完成 | 创建：2026-06-01 | 归档：2026-06-01

## 进度概览
- [x] 阶段 1: Parser plumbing (`_lastKeyQuoted` + `_ApplyMerge`)
- [x] 阶段 2: Block-mapping merge dispatch
- [x] 阶段 3: Flow-mapping merge dispatch + flow alias support
- [x] 阶段 4: Tests
- [x] 阶段 5: Docs (yaml.md / README / roadmap)
- [x] 阶段 6: Verification + archive

## 阶段 1: Parser plumbing
- [x] 1.1 Add field `private bool _lastKeyQuoted` to YamlParser (+
      `_mergedKeys` / `_mergedCount` field pair — see 备注 #2)
- [x] 1.2 Set `_lastKeyQuoted` in `_ParseMappingKey`
- [x] 1.3 Set `_lastKeyQuoted` in `_ParseFlowKey`
- [x] 1.4 Add `_ApplyMerge(dst, mergeVal)` + `_MergeOneSource(dst,
      source)` (field-based state — refactored from the `ref`-array
      design in proposal since z42 `ref` is compile-time-only at
      runtime; see 备注 #2)
- [x] 1.5 Add helpers `_IndexOfMergedKey(key)` / `_RemoveMergedKeyAt
      (index)` / `_AppendMergedKey(key)` over the `_mergedKeys` field

## 阶段 2: Block-mapping merge dispatch
- [x] 2.1 In `_ParseBlockMapping`, save/restore `_mergedKeys` +
      `_mergedCount` around new `_ParseBlockMappingBody`
- [x] 2.2 Dispatch unquoted-plain `<<` to `_ApplyMerge` instead of
      `m.Set(key, val)`
- [x] 2.3 ContainsKey branch: merged → overwrite + drop from
      `_mergedKeys`; otherwise throw duplicate
- [x] 2.4 Error position lands on the `<<` value via existing `_Err`
      (uses current `_line`/`_col` which sits at value end — clear
      enough for users; no separate capture needed)

## 阶段 3: Flow-mapping merge dispatch + flow alias support
- [x] 3.1 Extend `_ParseFlowValue` with `*alias` branch
- [x] 3.2 In `_ParseFlowMapping`, save/restore + dispatch via new
      `_ParseFlowMappingBody`
- [x] 3.3 Same duplicate / overwrite branching as block mapping

## 阶段 4: Tests
- [x] 4.1 Create `src/libraries/z42.yaml/tests/parse_merge_keys.z42`
- [x] 4.2 `test_basic_merge_from_anchor`
- [x] 4.3 `test_explicit_after_merge_overrides`
- [x] 4.4 `test_explicit_before_merge_still_wins`
- [x] 4.5 `test_merge_sequence_earlier_source_wins`
- [x] 4.6 `test_merge_in_flow_mapping`
- [x] 4.7 `test_quoted_double_lt_stays_literal_key`
- [x] 4.8 `test_quoted_single_lt_stays_literal_key`
- [x] 4.9 `test_direct_mapping_as_merge_value`
- [x] 4.10 `test_merge_alias_to_scalar_throws`
- [x] 4.11 `test_merge_sequence_with_non_mapping_throws`
- [x] 4.12 `test_merge_value_null_throws`
- [x] 4.13 `test_docker_compose_pattern`
- [x] 4.14 `test_stringify_drops_merge_token`
- [x] 4.15 `test_per_doc_merge_scope_via_parse_all`
- [x] 4.16 `test_deep_clone_independence_across_siblings`
- [x] 4.17 `test_duplicate_explicit_key_after_merge_still_errors`

## 阶段 5: Docs
- [x] 5.1 `docs/design/stdlib/yaml.md`: added merge-keys row to
      "Supported syntax" + new `yaml-future-merge-keys` ✅ entry
      under Deferred (with rationale, implementation notes, test
      coverage, out-of-scope items)
- [x] 5.2 Removed the stale `fix-yaml-parse-regression` paragraph
- [x] 5.3 `src/libraries/z42.yaml/README.md`: full Scope refresh
      (everything except `? key` complex-keys now ✅) + new
      "Composing configs with merge keys" example
- [x] 5.4 `docs/roadmap.md` line 312: replaced stale list with the
      current truth (only `yaml-future-complex-keys` remains)
- [x] 5.5 (opportunistic) `YamlParser.z42` header comment: refreshed
      Supported / Not-supported list to match current state

## 阶段 6: Verification + archive
- [x] 6.1 `./scripts/test-stdlib.sh z42.yaml` — all 14 yaml test
      files green (16/16 new merge-key tests + 13 pre-existing files)
- [x] 6.2 `./scripts/test-all.sh` — 6/6 stages GREEN, 256 stdlib
      test files in 22 libs
- [x] 6.3 Spec.md Scenario coverage cross-checked (every Scenario
      maps to a `test_*` in `parse_merge_keys.z42`)
- [x] 6.4 Moved to `docs/spec/archive/2026-06-01-add-yaml-merge-keys/`
- [x] 6.5 Commit + push (per workflow auto-archive)

## 备注

1. **YamlParser.z42 size**: now ~1545 lines (was 1450). `.z42` is not
   covered by the C# / Rust 500-line hard limit. Pre-existing tech
   debt; tracked separately if anyone wants a split.
2. **Field-based merge-key state (refactor from proposal design)**:
   the proposal had `_ApplyMerge` taking `string[] mergedKeys` + `ref
   int mergedCount`, but z42's `ref` modifier is compile-time-only at
   runtime (parameter-modifiers.md "Runtime Implementation" — runtime
   callee mutations don't propagate). Refactored to per-parser field
   `_mergedKeys` / `_mergedCount` with save/restore at each
   `_ParseBlockMapping` / `_ParseFlowMapping` entry. Recursion-safe
   because each invocation saves the outer state to a local before
   resetting. Body-extracted to `_ParseBlockMappingBody` /
   `_ParseFlowMappingBody` purely for save/restore plumbing.
3. **The stale `fix-yaml-parse-regression` note**: confirmed all 13
   pre-existing yaml test files pass on `main` before any of this
   spec's changes; the regression no longer reproduces. Removed
   opportunistically here.
