# Tasks: fix-incremental-cache-invalidation

> 状态：🟢 已完成 | 类型：fix | 创建：2026-04-27 | 完成：2026-04-27

**变更说明：** 修复 Wave 1.x / 3a 实施时反复踩到的增量缓存问题：改一个 stdlib 文件后 dotnet test / VM tests 出现"undefined symbol"等不可预期错误，需手动 `rm -rf artifacts/libraries/*/cache` 才能恢复。

**根因（两处）：**

1. **per-file cache 与 per-namespace ExportedModule 不一致**：
   `IncrementalBuild.Probe` 按 file 给每个 cached 文件挂上同 namespace 的"上次 zpkg" ExportedModule。当同 namespace 内有 fresh 文件 + cached 文件混合时，fresh CU 产生新 ExportedModule，cached CU 用旧 ExportedModule，合并写入 zpkg 时 TSIG section 会丢失/混淆元数据。

2. **all-cached path 重写 zpkg 时 lossy**：
   即使 100% cached（无 fresh 文件），代码仍走 cached units → ZpkgBuilder.BuildPacked 的路径，输出与原 zpkg 不一致（含义相同但 EXPT/TSIG 区少几十字节）。下游用新 zpkg 时部分类元数据缺失。表现：clean build 720/720 通过 → 紧接着 no-change rebuild 6/720 失败。

**修复：**

- **Probe**：当 fresh + cached 混合时，整包失效（`AllFresh`）。消除前者 bug。
- **PackageCompiler**：100% cached 且 lastZpkg 存在 → 完全跳过重写，保留现有 zpkg 不动（"preserved existing zpkg"）。消除后者 bug。

## Tasks

- [x] 1.1 `IncrementalBuild.Probe`：fresh + cached 混合时返回 AllFresh
- [x] 2.1 `PackageCompiler.BuildTarget`：100% cached → 跳过重写
- [x] 3.1 验证 3 场景：clean build / touch String + incr / no-change incr，dotnet 720 + VM 194 全绿
- [x] 3.2 既有 `StdlibBuild_SecondRun_AllCached` / `IncrementalBuild` 测试仍通过
- [ ] 4.1 commit + push + 归档

## 备注

- 副作用：per-file 增量混合优化彻底废除（发生改动的包整体重建），换取正确性。每个 stdlib 包小（~32 文件），全包重建仍很快（<1s）
- 留 backlog：让 `BuildPacked` cached path 输出与 fresh path 字节一致（确定性序列化）—— 那时可重新放开 fresh+cached 混合
