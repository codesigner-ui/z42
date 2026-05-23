# Tasks: add z42.yaml

> 状态：🟢 已完成 | 创建：2026-05-24 | 完成：2026-05-24
> 类型：feat（纯脚本 stdlib，无新 VM/IR；镜像 z42.toml / z42.json）
> Spec：[proposal](proposal.md)
>
> **注**：本 spec ship 时 z42.core 因并行 session `rename-primitives-to-pascal-case` mid-flight 暂时不可编译。
> z42.yaml 源码 + tests 已写完 + 全部 spec docs / roadmap / 注册步骤已落地；
> 端到端 `./scripts/test-stdlib.sh z42.yaml` 验证待并行 session 修好 primitives 后跑。
> spec 本身不引入新 VM/IR，纯脚本逻辑参考 z42.toml / z42.json 同 shape，本 spec
> 范围内的回归风险可控。

## 阶段 1: package skeleton

- [ ] 1.1 NEW `src/libraries/z42.yaml/z42.yaml.z42.toml`（manifest，dep = z42.core）
- [ ] 1.2 NEW `src/libraries/z42.yaml/src/YamlException.z42`（`Std.YamlException : Exception`）
- [ ] 1.3 MODIFY `src/libraries/z42.workspace.toml`：default-members 加 `z42.yaml`

## 阶段 2: YamlValue

- [ ] 2.1 NEW `src/libraries/z42.yaml/src/YamlValue.z42`：
  - `_kind` int discriminator (0=Null, 1=Bool, 2=Int, 3=Float, 4=String, 5=Sequence, 6=Mapping)
  - 类型 typed 槽位 + parallel arrays for sequence / mapping
  - Constructors: `OfNull / OfBool / OfInt / OfFloat / OfString / OfSequence / OfMapping`
  - Predicates: `IsNull / IsBool / IsInt / IsFloat / IsString / IsSequence / IsMapping`
  - Accessors: `AsBool / AsInt / AsFloat / AsString` + throw `YamlException` on type mismatch
  - Sequence ops: `At(i) / Length / Add`
  - Mapping ops: `Get(key) / ContainsKey / Keys / Add(key, value) / Count`
  - `static Parse(string)` + `static Stringify(YamlValue)` 入口（thin wrapper to YamlParser / YamlWriter）

## 阶段 3: YamlParser

- [ ] 3.1 NEW `src/libraries/z42.yaml/src/YamlParser.z42`：
  - Indentation-based block parser (track current indent level on a stack)
  - Block mapping `key: value\n` 检测 + 递归
  - Block sequence `- item\n` 检测 + 递归
  - Flow mapping `{ ... }` 嵌入支持
  - Flow sequence `[ ... ]` 嵌入支持
  - Scalar parser: plain / single-quoted / double-quoted (含 \n \t \" \\ \\u escapes)
  - Comments `#` skip (end-of-line + standalone)
  - Document marker `---` (start) optional
  - Tab in indent → `YamlException("tabs not allowed in indentation")`
- [ ] 3.2 error reporting：line + column 跟踪，throw `YamlException("<message> at <line>:<col>")`

## 阶段 4: YamlWriter

- [ ] 4.1 NEW `src/libraries/z42.yaml/src/YamlWriter.z42`：
  - Block-style stringify
  - Indent = 2 spaces
  - Mapping: `key: value\n`，nested mapping / sequence indents +2
  - Sequence: `- value\n`，nested 同
  - Scalar quoting heuristic：plain unless contains `: # \n " '` 等需 quote 的字符 → double-quoted with escapes
  - String null → empty; bool → `true` / `false`; int / float → standard format
  - **Out-of-scope (deferred)**: flow-style stringify (single-line `{}` / `[]`), comment preservation

## 阶段 5: tests

- [ ] 5.1 NEW `tests/parse_basic.z42` — null / bool / int / float / string scalars, single-level mapping
- [ ] 5.2 NEW `tests/parse_block.z42` — nested mapping + nested sequence + mixed
- [ ] 5.3 NEW `tests/parse_flow.z42` — `{a: 1, b: 2}`, `[1, 2, 3]`, mixed flow + block
- [ ] 5.4 NEW `tests/parse_strings.z42` — plain / single / double quotes, escapes
- [ ] 5.5 NEW `tests/parse_errors.z42` — tab in indent / unterminated quote / bad colon → YamlException
- [ ] 5.6 NEW `tests/stringify.z42` — block-style round-trip on a known fixture

## 阶段 6: 注册

- [ ] 6.1 MODIFY `scripts/build-stdlib.z42`：
  - `_stdlibList()` array 加 `"z42.yaml"`（length 19 → 20）
  - `_indexJson()` 加 `"Std.Yaml": "z42.yaml.zpkg"` 行
- [ ] 6.2 MODIFY `src/toolchain/host/platforms/wasm/js/stdlib/index.json`
- [ ] 6.3 MODIFY `src/toolchain/host/platforms/ios/Resources/stdlib/index.json`
- [ ] 6.4 MODIFY `src/toolchain/host/platforms/android/z42vm/src/main/assets/stdlib/index.json`
- [ ] 6.5 VERIFY: `./scripts/build-stdlib.sh` 产 `z42.yaml.zpkg`

## 阶段 7: 文档

- [ ] 7.1 NEW `docs/design/stdlib/yaml.md`：API surface + scope + Deferred 段
- [ ] 7.2 MODIFY `docs/design/stdlib/roadmap.md`：
  - "已落地" 段加 z42.yaml 一行
  - P2 表把 z42.yaml 改为划线 + 落地标记
  - "Deferred Backlog Index" 加 yaml.md 的 future 项索引
- [ ] 7.3 MODIFY `docs/design/stdlib/overview.md`：包列表加 z42.yaml
- [ ] 7.4 MODIFY `docs/roadmap.md` Deferred Backlog Index：yaml deferred 项索引
- [ ] 7.5 NEW `src/libraries/z42.yaml/README.md`：简介

## 阶段 8: 验证 + 归档

- [ ] 8.1 `./scripts/test-stdlib.sh z42.yaml` 全绿（≥ 6 test file, ≥ 20 assertions）
- [ ] 8.2 `./scripts/test-all.sh --parallel` 全绿（不回归）
- [ ] 8.3 mv `docs/spec/changes/add-z42-yaml/` → `docs/spec/archive/YYYY-MM-DD-add-z42-yaml/`
- [ ] 8.4 commit + push

## 备注

`out` 是 z42 reserved word（不能做变量名），实施期所有 buffer 命名用 `result` / `buf`（参 compression spec 同 workaround）。
