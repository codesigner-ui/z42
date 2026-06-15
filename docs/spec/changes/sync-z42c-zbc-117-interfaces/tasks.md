# Tasks: sync-z42c-zbc-117-interfaces

> 状态：🟡 进行中 | 创建：2026-06-15

**变更说明：** 把自举 z42c writer 同步到 zbc 1.17 / zpkg 0.19——TYPE section 每类
追加接口块（u16 count + interface_name_idx[] u32，静态字段块之后），镜像 C#
`add-reflection-get-interfaces`（2026-06-14 已归档）。

**原因：** get-interfaces 格式 bump 当时 z42c 锁被占，writer 同步延后（见该 change
proposal Out-of-Scope + memory `project_z42c_selfhosting`）。延后期 `xtask test
compiler-z42` byte-identical gate 暂红，本变更清欠债。

**文档影响：** 无新增长期规范（zbc.md / zpkg.md 已由 C# get-interfaces 落地 1.17/0.19
changelog）。version-bumping.md 第 5 步（z42c writer 同步）即本变更执行项。

## 任务

- [x] 1.1 `ZbcFormat.z42` — `ZbcVersion.Minor` 16→17（+注释）
- [x] 1.2 `IrModule.z42` — `IrClassDesc` 加 `Interfaces`/`InterfaceCount`（默认空）
- [x] 1.3 `ZbcWriter.z42` — InternPoolStrings 接口名预扫（静态字段后）+ BuildType 写接口块（类记录末，无条件 u16 count）
- [x] 1.4 `IrGen.z42` — `_classDesc` 从 `Z42ClassType.InterfaceNames` 填充
- [x] 1.5 `ZpkgWriter.z42` — `Minor` 18→19（zbc 1.17 联动）
- [x] 1.6 `zbc_tests.z42` — empty/f5/selfcheck golden 版本字节 10→11
- [x] 1.7 `zpkg_tests.z42` — header golden 版本字节 12→13
- [x] 2.1 `xtask test compiler-z42` 全绿：zbc 单元 golden（empty/f5/selfcheck 版本字节）+ **ifacecheck byte-compare 通过** + zpkg byte-compare **6/6** + zbc byte-compare **7/7**
- [x] 2.2 仅 stage 7 writer 文件（ZbcFormat/ZbcWriter/IrModule/ZpkgWriter/IrGen + zpkg_tests/zbc_tests）；不带入并行 reflection-generic-predicates 的 runtime/stdlib 改动；ACTIVE.md z42c 锁条目已在 HEAD（并行会话提交时带入）

## 备注
- 代码改动为上个会话 WIP，本会话验证 + 归档。
- z42c reader 按 section 寻址，不解码 zbc TYPE 段 → 接口块新增字节不影响 DepIndex/import。
- C# get-interfaces 未动 TSIG → z42c TSIG writer/reader 无需改。
