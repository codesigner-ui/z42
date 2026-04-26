# Tasks: workspace 查询命令 + CliOutputFormatter（C4b）

> 状态：🟢 已完成 | 创建：2026-04-26 | 归档：2026-04-26 | GREEN: dotnet 703/703 + cargo OK + VM 188/188

## Scope 调整说明

C4b 4 个查询命令实现合并到单文件 `src/compiler/z42.Driver/QueryCommands.cs`
（static class 风格，与 BuildCommand.cs 一致），而非 spec 中提到的 4 个独立
Commands/<Name>Command.cs 文件。理由：保持与现有代码风格一致，避免引入
IZ42Command 接口。

z42.Tests 项目新增 ProjectReference 到 z42.Driver（用于测 CliOutputFormatter）。

## 进度概览
- [ ] 阶段 1: CliOutputFormatter
- [ ] 阶段 2: InfoCommand
- [ ] 阶段 3: MetadataCommand
- [ ] 阶段 4: TreeCommand
- [ ] 阶段 5: LintManifestCommand
- [ ] 阶段 6: Program.cs 路由 + BuildCommand 接入 formatter
- [ ] 阶段 7: 测试 + 文档

---

## 阶段 1: CliOutputFormatter

- [ ] 1.1 新增 `src/compiler/z42.Driver/CliOutputFormatter.cs`：解析 WSxxx 前缀 → 友好格式
- [ ] 1.2 单元测试 `CliOutputFormatterTests.cs`：WS010 / WS024 / WS037 各错误格式
- [ ] 1.3 `--no-pretty` 标志支持

## 阶段 2: InfoCommand

- [ ] 2.1 新增 `src/compiler/z42.Driver/Commands/InfoCommand.cs`
- [ ] 2.2 实现 RenderOverview / RenderResolved / RenderIncludeGraph
- [ ] 2.3 Origins 来源链格式化（含 🔒 标记）
- [ ] 2.4 单元测试 `InfoCommandTests.cs`

## 阶段 3: MetadataCommand

- [ ] 3.1 新增 `src/compiler/z42.Driver/Commands/MetadataCommand.cs`
- [ ] 3.2 MetadataDto / MemberDto / EdgeDto record
- [ ] 3.3 `--format json` 输出 + schema_version: "1"
- [ ] 3.4 单元测试 `MetadataCommandTests.cs`

## 阶段 4: TreeCommand

- [ ] 4.1 新增 `src/compiler/z42.Driver/Commands/TreeCommand.cs`
- [ ] 4.2 ASCII 渲染器（DFS 缩进 / 树形字符）
- [ ] 4.3 单元测试 `TreeCommandTests.cs`

## 阶段 5: LintManifestCommand

- [ ] 5.1 新增 `src/compiler/z42.Driver/Commands/LintManifestCommand.cs`
- [ ] 5.2 调用 LoadWorkspace 触发全部 WSxxx 校验，聚合结果
- [ ] 5.3 单元测试 `LintManifestCommandTests.cs`

## 阶段 6: 路由整合

- [ ] 6.1 修改 `src/compiler/z42.Driver/Program.cs`：路由 info / metadata / tree / lint-manifest
- [ ] 6.2 修改 `src/compiler/z42.Driver/Commands/BuildCommand.cs`：错误输出走 CliOutputFormatter
- [ ] 6.3 编译验证

## 阶段 7: 测试 + 文档

- [ ] 7.1 验证 dotnet test / cargo / VM 全绿
- [ ] 7.2 修改 `docs/design/project.md`：L7 章节扩展为完整命令矩阵
- [ ] 7.3 修改 `docs/design/compiler-architecture.md`：MetadataDto schema 说明
- [ ] 7.4 修改 `docs/dev.md`：查询命令使用示例
- [ ] 7.5 修改 `docs/roadmap.md`：C4b 进度

---

## 验证清单（GREEN）

- [ ] `dotnet build src/compiler/z42.slnx` 无错
- [ ] `cargo build --manifest-path src/runtime/Cargo.toml` 无错
- [ ] `dotnet test src/compiler/z42.Tests/` 全绿
- [ ] `./scripts/test-vm.sh` 全绿
