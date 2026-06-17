# Tasks: clarify-launcher-command-face  🟢 已完成

**变更说明：** ① 实装 `z42 list --workloads`（runtimes + workloads 一条命令看全）；② 命令面文档把文档声明
但未实装、且为真特性（非便宜补丁）的 `z42 update` / `z42 use` / `workload update` 标 **planned**，与实现对齐。
**原因：** survey 发现命令面文档 over-promise；`update` 需 channel→latest 网络解析、`use` 需 z42.toml 项目 pin
解析，均未建 → 它们是 planned 特性；唯 `list --workloads` 便宜可立即实装。
**文档影响：** runtime-workload-distribution.md 命令面段。

- [x] 1.1 launcher_workload.z42：抽 `bool _listInstalledWorkloads()`（打印 `w (ver)` 行，返回是否有）；`_cmdWorkloadList` 改调它
- [x] 1.2 launcher.z42：`_cmdList()` → `_cmdList(ParseResult r)`；`--workloads` 时追加 workloads 段
- [x] 1.3 launcher_cli.z42：`list` ArgParser 加 `AddFlag("workloads",...)`；dispatch `_cmdList(r)`
- [x] 1.4 doc：命令面 `list [--workloads]` 标已实装；`update`/`use`/`workload update` 标 (planned — latest/pin resolution 未建)
- [x] 1.5 验证：launcher 清编；`z42 list` / `z42 list --workloads` 输出正确（装/不装 workload 两态）
- [x] 1.6 commit + 归档

## 验证结果
- launcher 清编；`z42 list --workloads`：装/不装两态正确（runtimes + workloads 段）；`workload list` 回归（抽 `_listInstalledWorkloads` 共享）；`list -h` 显示 --workloads。doc 命令面 `update`/`use`/`workload update` 标 (planned)。
