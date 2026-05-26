# Tasks: cleanup stale roadmap deferred entries

> 状态：🟢 已完成 | 创建：2026-05-26 | 归档：2026-05-26 | 类型：docs（最小化模式）

**变更说明**：清理 `docs/roadmap.md` Deferred Backlog Index 里 stale 的条目（已落地的 stdlib P0 包），同步 `docs/design/runtime/zbc.md` 头文件描述里 stale 的 minor 版本号。

**原因**：
- roadmap.md line 293 "stdlib P0–P3 缺失包: fs / os / threading / net / async / crypto" 但其中 fs / os / threading / net / crypto 均已 ✅ 落地（详 `docs/design/stdlib/roadmap.md`）；只有 async 真延后。索引不更新会误导新接手者
- zbc.md line 251 "version_minor 当前 5" 但 ZbcWriter.cs::VersionMinor = 6（fix-array-default-init 2026-05-19 落地）。changelog 表已加 1.6 行，但头部描述漏改

**文档影响**：本身就是文档清理；无代码 / 测试 / 行为变化

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `docs/roadmap.md` | MODIFY | 改 line 293 "stdlib P0–P3 缺失包" 行 → 仅保留 async 真延后；其他 ✅ |
| `docs/design/runtime/zbc.md` | MODIFY | 改 line 251 "当前 5" → "当前 6" |

## Tasks

- [x] 1.1 改 `docs/roadmap.md` line 293：把"fs / os / threading / net / async / crypto"列表 → 只保留 async；其它项加 ✅ 标记 + spec archive 引用
- [x] 1.2 改 `docs/design/runtime/zbc.md` line 251：`当前 5` → `当前 6`
- [x] 1.3 验证：grep 通过
- [x] 1.4 归档 `docs/spec/changes/cleanup-stale-roadmap-deferred/` → `docs/spec/archive/2026-05-26-cleanup-stale-roadmap-deferred/`
- [x] 1.5 commit + push

## 备注

无验证脚本需求（纯文档）；不触 GREEN 测试。
