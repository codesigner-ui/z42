# Proposal: Align zbc reader-writer asymmetry (Option A)

## Why

`docs/design/runtime/zbc.md` "Deferred / Future Work" §reader-writer-asymmetry 描述了已知缺陷：

> `ZbcReader.Read(bytes) → ZbcWriter.Write(IrModule)` 对 6 个 freeze-zbc-v1 fixture 中 3 个产生不同字节
> Root cause: SIGS 段 `retType` 用 1 byte `TypeTag` 编码 + lossy（`"int"` / `"i32"` 都映 I32；`I32` → 始终 canonical `"i32"`）；TYPE 段 `IrFieldDesc.Type` 同样 lossy
> 当前（2026-05-14）选 C（"接受 asymmetry，永久不修"）；触发条件 = User 主动决定走 round-trip CI 防线

**User 2026-05-27 显式 override 该 Option C 决策**，启动 Option A：SIGS / TYPE 各加一个 u32 str_idx 字段保留原始类型名，让 Read→Write 字节对账。

理由（User 视角）：
- 即将进入 hot-reload (0.3.2) / 增量编译 阶段，round-trip 漂移会累积成调试黑洞
- 现在做（wire format 还在 1.x 早期，stdlib 不大）成本可控；越往后越贵
- ReadWriteRoundTrip CI 防线缺失是已知的 95% → 100% gap，proactive 补上

## What Changes

- **SIGS 段**：每函数在 `ret_tag: u8` 之后追加 `ret_type_str_idx: u32`。Reader 用 string 而不是 lossy tag 还原 ret_type
- **TYPE 段**：每 field 在 `type_tag: u8` 之后追加 `field_type_str_idx: u32`。Reader 用 string 而不是 lossy tag 还原 field type
- **TypeTag 字节不删除**（保留作为类型 hint / disasm 帮助 / 未来 JIT 快路径）；string 是新增、权威 source
- **zbc minor bump**：1.6 → 1.7
- **zpkg minor bump**：0.7 → 0.8（联动）
- **Fixture regen**：6 个 zbc fixture + 4 个 zpkg fixture
- **Stdlib regen**：`./scripts/regen-golden-tests.sh`（所有 stdlib zpkg 重生）
- **新增 ReadWriteRoundTrip 测试**：freeze-zbc-v1 当年 drop 的 fixture round-trip，加回来作为 CI 防线
- **Design doc 同步**：删除 zbc.md / zpkg.md Deferred 里的 reader-writer-asymmetry 条目；roadmap.md Deferred Backlog Index 删除对应行；zbc.md changelog 加 1.7 行；zpkg.md changelog 加 0.8 行

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs` | MODIFY | `VersionMinor` 6→7；SIGS 写 ret_type str_idx；TYPE 写 field_type str_idx |
| `src/compiler/z42.IR/BinaryFormat/ZbcReader.cs` | MODIFY | SIGS 读 ret_type str_idx；TYPE 读 field_type str_idx |
| `src/compiler/z42.Project/ZpkgWriter.cs` | MODIFY | `VersionMinor` 7→8 |
| `src/runtime/src/metadata/zbc_reader.rs` | MODIFY | `ZBC_VERSION_MINOR` 6→7；`ZPKG_VERSION_MINOR` 7→8；`read_sigs` / `read_type` 消费 str_idx |
| `docs/design/runtime/zbc.md` | MODIFY | changelog 加 1.7 行；Deferred 删 reader-writer-asymmetry；header `version_minor 当前 6` → `7` |
| `docs/design/runtime/zpkg.md` | MODIFY | changelog 加 0.8 行；Deferred 删 reader-writer-asymmetry |
| `docs/roadmap.md` | MODIFY | Deferred Backlog Index 删 reader-writer-asymmetry 行 |
| `src/tests/zbc-format/generate-fixtures.sh` | EXEC | regen 6 fixture source.zbc + expected.json |
| `src/tests/zpkg-format/generate-fixtures.sh` | EXEC | regen 4 fixture source.zpkg + expected.json |
| `src/tests/zbc-format/*/source.zbc` | MODIFY | regen 产物（6 fixture） |
| `src/tests/zbc-format/*/expected.json` | MODIFY | regen 产物 |
| `src/tests/zpkg-format/*/source.zpkg` | MODIFY | regen 产物（4 fixture） |
| `src/tests/zpkg-format/*/expected.json` | MODIFY | regen 产物 |
| `src/compiler/z42.Tests/Zbc/ReadWriteRoundTripTests.cs` | NEW | freeze-zbc-v1 当年 drop 的 round-trip CI 防线 |

**只读引用**：
- `src/runtime/src/metadata/zbc_reader.rs:1000` — zbc minor 检查处
- `.claude/rules/version-bumping.md` — bump checklist
- `docs/design/runtime/zbc.md` Deferred §reader-writer-asymmetry — 决策上下文

## Out of Scope

- TypeTag 字节完全删除（保留作 hint；可单独 future spec 评估）
- 别的 lossy 字段（如 `ConstraintBundle` 里的 type 标签）—— 不在已知 asymmetry root cause 列表
- 跨 minor 兼容性（pre-1.0 strict-pin，旧 zbc/zpkg 全 regen）

## Open Questions

无（User 已批量授权 a→b→c 序列，且 design doc Option A 描述已完整）。
