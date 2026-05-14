# Proposal: Freeze `.zbc` v1 wire format（roadmap 0.2.0）

## Why

`.zbc` 格式从 1.0 一路 bump 到 1.5（最近一次 2026-05-13 `fix-numeric-cast-lowering` 加 `Convert` opcode），但：

1. **文档落后两个 minor**：`docs/design/runtime/zbc.md` line 251 仍写 "当前 1.3"，writer 实际是 1.5
2. **兼容策略 doc-code 冲突**：zbc.md 描述 "minor 变化 → 新增 opcode，旧 VM 遇未知 opcode `UnsupportedOpcode`"（前向兼容暗示），但 `ZbcReader` 强制 `minor < 5 → bail`（严格 pin）—— 文档与实现描述的不是同一回事
3. **没有 CI invariant 防线**：任何人改 `ZbcWriter.cs` 的 section layout 不会触发任何测试失败 —— 全靠 review

不冻结的代价：format 漂移会无声累积；自举完成后想引入 SemVer 时无法回溯"哪个 1.x minor 引入了什么"。

**0.2.0 退出标准**（roadmap）："`.zbc` v1.x 格式冻结（magic + section layout 锁定）"。

## What Changes

- **MODIFY** `docs/design/runtime/zbc.md`：
  - 当前版本号 1.3 → 1.5
  - 加 changelog 表（1.0 → 1.1 → 1.2 → 1.3 → 1.4 → 1.5 各次 bump 的具体内容）
  - 加 "v1 strict-pin 契约" 章节：明确说明 minor 不跨版本兼容；每次 bump 必须 regen 所有 zbc artifact；什么改动算 minor / 什么算 major
- **NEW** `src/tests/zbc-format/`：字节级 golden fixture + harness（覆盖代表性 layout：empty / STRP+FUNC / DBUG+BLID / TIDX / FRCS / cross-import token）
- **NEW** `src/compiler/z42.Tests/Zbc/FormatInvariantTests.cs` + `src/runtime/src/metadata/zbc_format_invariant_tests.rs`：unit test 覆盖
  - Writer 当前 minor 能被自身 reader 读
  - 未识别 section id 被 reader skip（dict-lookup 已默认行为，测试固化）
  - `major != 1` 或 `minor < 5` 触发 reject
  - 同一 IR module → 同一 byte 输出（writer 确定性）
- **NEW** Disasm round-trip test：选 3-5 个代表性 zbc → `disasm` → `assemble` → 字节级一致
- **MODIFY** `.claude/rules/workflow.md`：加 "`.zbc` minor bump 流程"（writer + reader + zbc.md changelog 三处必须同步，加 invariant test）
- **MODIFY** `docs/roadmap.md`：0.2.0 标记完成；指向本 spec archive
- **NEW** spec archive

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `docs/design/runtime/zbc.md`                                | MODIFY | 同步 1.5 + changelog + strict-pin 契约 |
| `src/tests/zbc-format/README.md`                            | NEW    | golden fixture 目录 README（按 code-organization.md 规则）|
| `src/tests/zbc-format/<name>/source.zbc`                    | NEW    | 字节级 golden 输入（5-7 个 fixture）|
| `src/tests/zbc-format/<name>/expected.json`                 | NEW    | 归一化 parse 结果 |
| `src/tests/zbc-format/generate-fixtures.sh`                 | NEW    | 用 `dotnet ZbcWriter` 跑 z42 源码 → zbc → 拷贝到 fixture 目录（一键 regen）|
| `src/compiler/z42.Tests/Zbc/FormatInvariantTests.cs`        | NEW    | C# 端 invariant + round-trip test |
| `src/compiler/z42.Tests/Zbc/FormatGoldenTests.cs`           | NEW    | C# 端 golden fixture harness |
| `src/runtime/src/metadata/zbc_format_invariant_tests.rs`    | NEW    | Rust 端 invariant test（unknown section skip / 版本 reject / round-trip）|
| `.claude/rules/workflow.md`                                 | MODIFY | 加 "`.zbc` minor bump 流程" 子节 |
| `docs/roadmap.md`                                           | MODIFY | 0.2.0 完成标记 + spec archive 引用 |

**只读引用**（理解上下文必须读，不修改）：

- `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs` — 当前 minor 来源 + section 写入顺序
- `src/compiler/z42.IR/BinaryFormat/ZbcReader.cs` — 版本检查 + section dispatch
- `src/runtime/src/metadata/zbc_reader.rs` — Rust 端对应
- `src/runtime/src/metadata/zbc_reader_tests.rs` — 现有 reader test 风格参考

## Out of Scope

- **改动 wire format 本身** —— 本 spec 只冻结现状，不调整 section / opcode / header 任何字段
- **`.zpkg` 格式冻结** —— 0.2.1 单独 spec（结构沿用本 spec 模板）
- **真正实现前向 / 后向兼容**（option B）—— User 已裁决走 strict-pin（option A）
- **Token 编码扩展**（IMPORT_BASE 之外的新 range）—— 留 v2 或独立 spec
- **zsym sidecar 格式重组**（DBUG / BLID 已稳定）—— 不动
- **`z42c disasm` 输出格式改善** —— 0.2.1 收尾
- **ELF 风格 section flags / dependencies**（"required" vs "optional"）—— v2 才考虑

## Open Questions

无（探索阶段 Q-A 到 Q-E 已默认采纳推荐 + Option A 已裁决；新增冲突 1+2 在 design.md 中作为已裁决决策记录）。
