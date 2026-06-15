# Tasks: fix-z42c-irdump-gate-bugs

> 状态：🟢 已完成 | 创建：2026-06-15 | 完成：2026-06-15

**变更说明：** 修复 `z42c.semantics/src/IrDump.z42` 两个 bug，均在 [[fix-z42c-load-fixup-loop]]
修好 runtime 死循环、`xtask test compiler-z42` gate 真正跑通后才暴露（此前 gate 长期被
test-runner 僵尸/死循环阻塞，从未完整跑过）：

1. **重复字段声明**（line 223-224）：`CompiledModuleZ` 的 `DiagMsgs`/`DiagCount` 复制粘贴写了两遍
   → 类有 12 个 own field 声明、10 个不同名 → 触发 runtime fixup 不收敛死循环（见关联变更）。删重复行。
2. **`_buildF` 漏设 `SourceStem`**（line 209）：`--emit-zbc` 走的纯编译路径未设 `gen.SourceStem`
   → 合成的 `__static_init__` 名回落 "main"（`main.__static_init__`），与 C# 的
   `<filestem>.__static_init__`（如 `sacheck.__static_init__`）差 3 字节 → sacheck byte-compare 失败。
   镜像 `_compileCu` 补 `gen.SourceStem = _stem(cu)`。

**原因：** ① statics-arrays（2026-06-13）加诊断打印时复制粘贴留下重复行；② `_compileCu`（build 路径）
正确设了 SourceStem，但 `_buildF`（emit-zbc 路径）漏了同一行。两者都因 gate 从未真正跑通而潜伏。

**文档影响：** 无（纯源/codegen bug 修复，对齐既有约定，机制不变）。

## 任务
- [x] 1.1 删 `CompiledModuleZ` 重复的 `public string[] DiagMsgs; public int DiagCount;` 行
- [x] 1.2 `_buildF` 加 `gen.SourceStem = IrDump._stem(cu)`（镜像 `_compileCu` / C# `_cuStem`）
- [x] 2.1 z42c 重建 + `xtask test compiler-z42`：zbc byte-compare **7/7**（含 sacheck）+ zpkg **6/6** 全绿
- [x] 2.2 仅 stage `IrDump.z42`

## 备注
- ③ 根因「C# 编译器静默接受重复字段声明」入 compiler-architecture.md Deferred `reject-duplicate-field-decl`（独立 compiler 变更）。
