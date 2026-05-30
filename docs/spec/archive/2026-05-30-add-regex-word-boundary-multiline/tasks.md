# Tasks: Regex `\b` / `\B` + `(?m)` multiline

> 状态：🟢 已完成 | 创建：2026-05-29 | 归档：2026-05-30 | 类型：feat（新 stdlib 行为）

## 进度概览

- [x] 阶段 1: RegexNode + Parser
- [x] 阶段 2: Regex inline-flag + engine
- [x] 阶段 3: 测试 + 文档同步
- [x] 阶段 4: GREEN 验证 + commit

## 阶段 1: RegexNode + Parser

- [x] 1.1 MODIFY `src/libraries/z42.regex/src/RegexNode.z42`
  - 顶部 Kinds 注释加 `9 = WORD_BOUNDARY  \b`、`10 = WORD_NON_BOUNDARY  \B`
  - 添加 static factory `WordBoundary()` / `WordNonBoundary()`
- [x] 1.2 MODIFY `src/libraries/z42.regex/src/RegexParser.z42`
  - `ParseEscape` 在 `\s/\S` 之后、字面 fallback 之前加 `e == 'b'` / `e == 'B'`
  - 注释引用本 spec 名

## 阶段 2: Regex inline-flag + engine

- [x] 2.1 MODIFY `src/libraries/z42.regex/src/Regex.z42` — 字段
  - 加 `private bool _multiline;`
  - 构造器签名加 `bool multiline` 参数；初始化 `_multiline = multiline;`
- [x] 2.2 MODIFY `src/libraries/z42.regex/src/Regex.z42` — Compile inline-flag 解析
  - 旧的 4-char `(?i)` strip 改为通用 `(?[im]+)` strip
  - 不匹配格式时不剥离，原样交给 parser
  - `new Regex(..., caseInsensitive, multiline)` 一并传
- [x] 2.3 MODIFY `src/libraries/z42.regex/src/Regex.z42` — engine match
  - k=4 ANCHOR_START 加 multiline 分支：`_multiline && input[pos-1]=='\n'`
  - k=5 ANCHOR_END 加 multiline 分支：`_multiline && input[pos]=='\n'`
  - 新增 k=9 / k=10 分支调 `_atWordBoundary`
- [x] 2.4 MODIFY `src/libraries/z42.regex/src/Regex.z42` — helper
  - `private bool _atWordBoundary(int pos)`
  - `private static bool _isWordChar(int ch)`

## 阶段 3: 测试 + 文档同步

- [x] 3.1 NEW `src/libraries/z42.regex/tests/regex_word_boundary.z42`
  - `\b cat \b` 命中/失配 / 首末位 / `\b\w+\b` FindAll / `\B` 反向 / `(?i)\bCat\b`
- [x] 3.2 NEW `src/libraries/z42.regex/tests/regex_multiline.z42`
  - 默认 `^`/`$` 锁首末（回归）
  - `(?m)^foo` / `(?m)foo$` / FindAll 跨行 / 空串 / `(?im)`
- [x] 3.3 MODIFY `docs/design/stdlib/regex.md`
  - Decision 6 / 7 表行：`m flag 留 Deferred` → `(?m) 已落地 → 见
    add-regex-word-boundary-multiline`
  - Deferred 段：移除 `\b` / `\B` / `(?m)` 条目（如有；当前 doc 未单列，
    则在"已支持 escape"段补 `\b \B`，"已支持 anchor"段补 multiline 行）
  - syntax 表 / API surface 段补 `\b` `\B` `(?m)`

## 阶段 4: GREEN 验证 + commit

- [x] 4.1 `./scripts/build-stdlib.sh` — z42.regex.zpkg 重建无错
- [x] 4.2 `./scripts/test-stdlib.sh z42.regex` — 新增 + 既有全过
- [x] 4.3 `./scripts/regen-golden-tests.sh --release` — golden 重生（regex 测试不在 golden 集，但确保链路通）
- [x] 4.4 `./scripts/test-all.sh` 全绿
- [x] 4.5 归档：移到 `docs/spec/archive/2026-05-29-add-regex-word-boundary-multiline/`
- [x] 4.6 commit + push（含 `.claude/` + `docs/`）

## 备注

- z42 字符串 `\n` 转义在 source-level 已通过 lexer 处理为 0x0A；test 文件
  里写 `"foo\nbar"` 即正常多行串。
- 不要顺手改 `\x` / `\u` 等 escape；out of scope。
- 若实施中发现 charset `\b` 行为意外变了（如 ParseCharsetChar 改动），停下
  汇报 — 本 spec 显式保留字面 `b` 行为。

## 实施备注（2026-05-30）

- A4 `regex_word_boundary.z42` 用了 `\u0008` 测 charset 内 `\b` 保持字面
  `b`，z42 lexer 不支持 `\u` 转义，改成 `String.FromChars(new char[]{(char)8})`
- A3 实施时漏掉 `FindFrom` 的 `^`-anchored 快通道 bail；导致 `(?m)^` 在
  非 0 位置永远命中不上。修正：anchoredStart 优化在 `_multiline=true` 时
  禁用（让 `^` 在每个 from 都重新尝试）。落地在 Regex.z42 第一次 build
  后的 test 失败 4/16 → 修复 → 全过 16/16
- 全 GREEN：237 stdlib 文件 + 6 stages（test-all.sh scope=full）
