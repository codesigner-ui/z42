# Tasks: add-directory-copy

> 状态：🟢 完成 | 创建：2026-06-08 | 类型：feat (stdlib, 纯 z42)
> 模式：minimal —— 库级 API 新增，不涉及 lang / ir / vm 规范，无需 spec 先行。
> 子系统锁：`stdlib`（与 add-reflection-mvp 例外共存，文件零重叠，见 ACTIVE.md）。

## 背景

`migrate-scripts-to-z42` 的 package 子任务（ios/android/wasm + dSYM 递归拷贝）
标注："dSYM 递归拷贝 → 需 `Directory.Copy` recursive — **唯一 stdlib gap**，暂跳过"。

`Std.IO.Directory` 已有全部 primitive（`Exists` / `Create` / `Enumerate` 返回
basename / `File.Copy`），递归目录拷贝可**纯 z42** 组合实现，无需新 native extern。

## 任务

- [x] 1. `Directory.Copy(string src, string dst, bool recursive)` —— 纯 z42，落
      `src/libraries/z42.io/src/Directory.z42`
  - dst 不存在则 `Create`（mkdir -p 语义）
  - `Enumerate(src)` 拿 basename；`Directory.Exists(子项全路径)` 区分目录/文件
  - 文件 → `File.Copy`；子目录 → recursive 时递归，否则跳过
- [x] 2. 测试 `src/libraries/z42.io/tests/directory_copy.z42`：3 个 [Test]
      （recursive 全树 + 内容一致 / 非递归只拷顶层 / dst 自动 mkdir -p）
- [x] 3. 文档同步：`docs/design/stdlib/overview.md` 文件列表加 `Copy` + 一段
      语义/原理注（递归、dst 自动创建、纯 z42 无新 native、dSYM 需求驱动）
- [x] 4. GREEN：build stdlib --workspace 22/22 · z42.io 测试 44/44（含新 3）·
      全 stdlib 261/261 无回归。compiler/VM/cross-zpkg 不在本变更 blast radius
      （零 C#/Rust/格式改动）且重建撞并发 reflection-MVP 的 in-flight Rust，故跳
- [x] 5. commit + push + 释放 stdlib 锁 + 归档到 `docs/spec/archive/2026-06-08-add-directory-copy/`

## 设计要点

- recursive 参数显式（无重载、无默认值）—— 调用方意图清晰
- 不去重 dst-in-src 自指环（与 BCL `Directory` 一致，调用方责任）；dSYM 用例
  拷到独立位置，不触发
- 顺序不保证（沿用 Directory 模块约定）；如需稳定顺序调用方自行 sort
