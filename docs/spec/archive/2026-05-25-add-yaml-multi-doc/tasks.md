# Tasks: add YAML multi-document parsing

> 状态：🟢 已完成 | 创建：2026-05-25 | 归档：2026-05-25
> 类型：feat (extend parser) | Spec 类型：minimal mode

**变更说明**：在 `Std.Yaml` 上加 `YamlValue.ParseAll(string) → YamlValue[]`
和 `ParseAllStream(Stream) → YamlValue[]`。处理 `---` 分隔的多文档 YAML
流（kubectl 风格的 manifest 堆叠是典型用例）。`Parse(string)` 保持单文档
strict 行为（多文档输入仍抛 YamlException + 改进 hint 提示用 ParseAll）。

**原因**：
- kubectl / helm / ansible manifest 都用 `---` 分隔多文档；目前 z42.yaml v0
  只解析第一个文档就抛错
- 与 `Std.Json` / `Std.Toml` 的"单值入口 + 多值入口"对称设计一致

**算法**：
- 新私有 `_ParseOneDocBody()` 提取原 `ParseDocument` 中"解析一个文档体"逻辑
- `ParseDocument()`（单文档 strict 入口）= `_ParseOneDocBody()` + 严格的尾部
  内容检查（trailing `...` 允许；任何其他内容抛错并提示 ParseAll）
- `ParseAllDocuments()`（新多文档入口）= 循环 `_ParseOneDocBody()` + 跳过
  doc boundary marker (`---` / `...`) 直到 EOF
- block parser 中的三个 yield 点增加 `_IsDocBoundary()` 检查，这样 root-level
  `---` 不会被误解析为 sequence/scalar：
  1. `_ParseBlockValue` 入口（dispatch 前）
  2. `_ParseBlockMapping` 循环顶
  3. `_ParseBlockSequence` 循环顶

**Out of scope（follow-up）**：
- YAML stream-of-docs incremental parsing (`ParseStreamLazy` returning iterator)
  — z42 没有原生迭代器；用户取整个数组目前可接受
- `---` 与 directive (`%YAML`, `%TAG`) 之间的多文档 directive 重用 — v0 完全
  不支持 directives，留 `yaml-future-directives`

**文档影响**：
- `yaml.md`：flip `yaml-future-multi-doc` Deferred → ✅ landed + API matrix
  增加 `ParseAll` / `ParseAllStream` 行
- `roadmap.md` Deferred Index：剥离 multi-doc 标注

## Tasks

- [x] 1.1 `YamlParser.z42`: extract `_ParseOneDocBody()` helper
- [x] 1.2 `YamlParser.z42`: add `_IsDocBoundary()` helper + insert at three block yield points (`_ParseBlockValue` / `_ParseBlockMapping` / `_ParseBlockSequence`)
- [x] 1.3 `YamlParser.z42`: add `ParseAllDocuments() → YamlValue[]`，refactor `ParseDocument` to use helper（包括 strict-trailing 检查 + 改进错误信息）
- [x] 1.4 `YamlValue.z42`: add `ParseAll(string)` + `ParseAllStream(Stream)` static factories
- [x] 2.1 NEW `tests/parse_multi_doc.z42` — 17 tests (2-doc / 3-doc / leading `---` / trailing `...` / `... + ---` between / empty + whitespace / bare `---` → null doc / two `---` → two null docs / single-doc via ParseAll / mixed types per doc / nested mapping / sequence-then-mapping / kubectl manifest stack / single-doc `Parse` rejects + accepts trailing `...` / comments between docs)
- [x] 3.1 `yaml.md`: flip `yaml-future-multi-doc` Deferred → ✅ landed + API matrix update (Parse + ParseAll + ParseAllStream)
- [x] 3.2 `roadmap.md` Deferred Index：strike multi-doc 标注
- [x] 4.1 GREEN (z42.yaml: all 8 test files pass, 84 total tests including 17 new multi-doc) + archive + commit + push

## 备注

GREEN scope: z42.yaml lib via manual z42c + z42-test-runner（test-stdlib.sh
仍被并行 session z42.net HttpServer WIP 阻塞）。所有 8 个 yaml 测试文件 100%
通过，包含 17 个新增 multi-doc 测试 + 67 个 pre-existing 测试无回归。
