# Proposal: Freeze `.zpkg` v0 wire format（roadmap 0.2.1）

## Why

`.zpkg` 是 z42 的包格式（`name.zpkg` = META + STRS + NSPC + EXPT + DEPS + SIGS + MODS / FILE + TSIG + IMPL），承载多模块封装。当前状态：

1. **没有专用 doc**：格式描述只在 `ZpkgWriter.cs:7-29` 的 C# XML 注释里；没有 `docs/design/runtime/zpkg.md`
2. **错过一次 minor bump**：zbc minor 1.4 → 1.5（2026-05-13 `fix-numeric-cast-lowering`）时，**zpkg minor 漏 bump**（仍是 0.5）；现在的 zpkg 0.5 内嵌 zbc 1.5 modules，但 ZpkgWriter 注释还写 "inner zbc 1.4"
3. **reader 用 `minor < N` 宽容匹配**（与 zbc 修复前同模式）：`ZpkgReader.cs:279` `if (major == 0 && minor < 5)`；Rust 端 `zbc_reader.rs:1191` 同
4. **没有 CI invariant 防线**：和 freeze-zbc-v1 之前一样靠 review

不冻结的代价：和 zbc 同样的格式静默漂移；包格式更上层，崩了影响 stdlib 加载、跨 zpkg metadata、Tier 3 嵌入。

**0.2.1 退出标准**（roadmap）："`.zpkg` indexed/packed 格式冻结 + `z42c disasm` 完整"。本 spec 处理前半部分（格式冻结）；disasm 完整化属独立轻量改进，归并到后续 spec 或本 spec Phase 4 顺手做（视实施时复杂度决定）。

## What Changes

- **NEW** `docs/design/runtime/zpkg.md`：专用 wire format 文档（layout / section 列表 / packed vs indexed mode / sym-only sidecar / strict-pin 契约 / minor changelog 0.1 → 0.6）
- **MODIFY** `src/compiler/z42.Project/ZpkgWriter.cs`：minor `5` → `6`（追 zbc 1.4 → 1.5 的漏 bump）；注释同步 "inner zbc 1.5"
- **MODIFY** `src/compiler/z42.Project/ZpkgReader.cs` + Rust `zbc_reader.rs` zpkg 路径：精确匹配 + 引用 writer 常量 + 错误信息含 regen 提示
- **MODIFY** `docs/design/runtime/zbc.md`："如何 bump minor" 引用更新（zbc minor bump 触发 zpkg minor bump 的耦合规则）
- **NEW** `src/tests/zpkg-format/`：5-6 个代表性 fixture（minimal packed / indexed / has-sigs / has-tsig / sym-only sidecar / multi-namespace）+ `generate-fixtures.sh` + ZpkgGoldenJsonFormatter
- **NEW** `src/compiler/z42.Tests/Zpkg/FormatGoldenTests.cs` + `FormatInvariantTests.cs`（byte / json / writer-deterministic / version-reject / unknown-section-skip）
- **MODIFY** `.claude/rules/workflow.md` "Bumping `.zbc` minor" 子节扩展：加 zpkg 子规则（zbc minor 必随 zpkg minor）
- **MODIFY** `docs/roadmap.md`：0.2.1 标完成 + archive 引用

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `docs/design/runtime/zpkg.md`                                       | NEW    | 专用 wire format 文档（约 200-250 行）|
| `docs/design/runtime/zbc.md`                                        | MODIFY | "如何 bump minor" 子节加 zpkg 联动规则；交叉引用 zpkg.md |
| `src/compiler/z42.Project/ZpkgWriter.cs`                            | MODIFY | `VersionMinor = 5` → `6` + 注释更新 |
| `src/compiler/z42.Project/ZpkgReader.cs`                            | MODIFY | 版本检查精确匹配 + 引用 `ZpkgWriter.VersionMinor` 常量 |
| `src/runtime/src/metadata/zbc_reader.rs`                            | MODIFY | 加 `ZPKG_VERSION_MAJOR/MINOR` 常量；zpkg 主读 + sidecar 读改精确匹配 |
| `src/tests/zpkg-format/README.md`                                   | NEW    | 目录职责 + 维护流程 |
| `src/tests/zpkg-format/<name>/source.z42.toml`                      | NEW    | 5-6 个 fixture 源（workspace.toml 或最小 manifest）|
| `src/tests/zpkg-format/<name>/source.zpkg`                          | NEW    | check-in 字节 |
| `src/tests/zpkg-format/<name>/expected.json`                        | NEW    | check-in 归一化 JSON |
| `src/tests/zpkg-format/generate-fixtures.sh`                        | NEW    | 一键 regen 脚本 |
| `src/compiler/z42.IR/BinaryFormat/ZpkgGoldenJsonFormatter.cs`       | NEW    | 类比 ZbcGoldenJsonFormatter（zpkg 版本）|
| `src/compiler/z42.Driver/Program.cs`                                | MODIFY | `golden-json` 子命令扩展为同时支持 zpkg（按扩展名分发）|
| `src/compiler/z42.Tests/Zpkg/FormatGoldenTests.cs`                  | NEW    | byte + JSON + writer determinism × 6 fixture |
| `src/compiler/z42.Tests/Zpkg/FormatInvariantTests.cs`               | NEW    | 版本 reject + 未识别 section skip + writer const exposed |
| `.claude/rules/workflow.md`                                         | MODIFY | "Bumping `.zbc` minor version" 子节加 zpkg 联动规则 |
| `docs/roadmap.md`                                                   | MODIFY | 0.2.1 标完成 + archive 链接 |

**只读引用**（必须读，不修改）：

- `src/compiler/z42.Project/ZpkgWriter.Sections.cs` / `ZpkgWriter.Tsig.cs` — section emit 细节
- `src/compiler/z42.Project/ZpkgReader.Sections.cs` / `ZpkgReader.Tsig.cs` — section parse 细节
- `src/runtime/src/metadata/zbc_reader.rs` line 1140-1220 — Rust zpkg 主读路径
- `docs/spec/archive/2026-05-14-freeze-zbc-v1/` — 上一个 freeze spec（模板）

## Out of Scope

- **`z42c disasm` 完整化**（0.2.1 退出标准的另一半）—— 视实施复杂度决定是否本 spec 顺手做；超出则独立 spec
- **改动 zpkg wire format 本身** —— 本 spec 只冻结现状 + 补漏的 0.5 → 0.6 catch-up bump；不调整任何 section / 字段
- **zsym sidecar 格式深入改动** —— `FlagSymOnly` 等仅在 invariant test 覆盖，不重组 layout
- **packed vs indexed mode 决策权衡**（什么时候用哪个）—— 已稳定，文档化即可
- **TSIG / IMPL 跨包元数据形态扩展** —— 当前形态在 stdlib 跑通，不重审
- **Read-Write 字节对账**（同 zbc）—— 不在 strict-pin scenarios，留独立 follow-up

## Open Questions

无（探索阶段对齐了 zpkg 与 zbc 同套 strict-pin 模板；catch-up bump 0.5 → 0.6 是 mechanical 决定）。
