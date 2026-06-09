# Tasks: add-toml-key-order

> 状态：🟢 完成 | 创建：2026-06-09 | 类型：feat (stdlib, 纯 z42)
> 模式：minimal —— 改 Stringify 的 key 排序策略，无 lang/ir/vm。
> 子系统锁：`stdlib`（与 add-reflection-mvp 例外共存，仅动 z42.toml，文件零重叠）。

## 背景

`toml-future-key-order-preservation`（dev-workflow / git-diff 友好）：`Stringify`
v0 按**字母序**重排 key，round-trip 改变文件结构。调研发现底层数据结构（parallel
arrays `_tableKeys`/`_tableValues`，`Set` 追加新键 / 原地更新）**本就保留插入顺序**，
`TomlValue.Keys()` 返回插入序 —— 此前只是 `TomlWriter.SortedKeys` 主动 insertion-sort
把它丢弃了。条目原"用 Dictionary 不保留顺序"的前提已过时。

## 改动（一处）

`TomlWriter`：`SortedKeys` → `OrderedKeys`，body 从字母序 insertion-sort 改为直接
返回 `table.Keys()`（插入序，确定性）。更新 TomlValue.Stringify doc 注释（key-sorted
→ insertion order）。

## 任务

- [x] 1. TomlWriter.OrderedKeys 返回 Keys()（插入序），删字母序 sort；2 调用点重命名
- [x] 2. TomlValue.Stringify doc 注释更新（insertion order）
- [x] 3. 测试 stringify.z42 加 2 个：非字母序插入序保留（zebra<apple<mango）+ parse→Stringify
      round-trip 保序。现有 6 测试用 Contains/round-trip-value 检查（不依赖顺序），不受影响。
- [x] 4. 文档：toml.md `toml-future-key-order-preservation` → ✅ landed
- [x] 5. GREEN：canonical `test lib z42.toml`（build-stdlib 22/22 + 8/8 文件全过含 stringify 8/8）
- [x] 6. commit + push + 释锁归档

## 备注

确定性不破：插入序对 parsed tree = parse 顺序（top-to-bottom，确定），对程序构建的
tree = 代码 Set 顺序（确定）—— 非 common-pitfalls §1 的 hash 迭代非确定源。无 golden /
脚本依赖 TOML 输出顺序（Stringify 仅 z42.toml 内部 + 测试用）。
