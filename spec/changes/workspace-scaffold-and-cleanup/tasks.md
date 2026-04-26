# Tasks: workspace 脚手架 + 清理 + WS004 移除（C4c）

> 状态：🟡 待实施 | 创建：2026-04-26 | 依赖：C4a + C4b 落地

## 进度概览
- [ ] 阶段 1: CleanCommand
- [ ] 阶段 2: NewCommand（含 init）
- [ ] 阶段 3: FmtCommand
- [ ] 阶段 4: WS004 移除
- [ ] 阶段 5: Program.cs 路由整合
- [ ] 阶段 6: 测试 + 文档

---

## 阶段 1: CleanCommand

- [ ] 1.1 新增 `src/compiler/z42.Driver/Commands/CleanCommand.cs`
- [ ] 1.2 集中清理（workspace 模式）
- [ ] 1.3 per-member 清理（-p）
- [ ] 1.4 单工程模式 fallback
- [ ] 1.5 单元测试 `CleanCommandTests.cs`

## 阶段 2: NewCommand

- [ ] 2.1 新增 `src/compiler/z42.Driver/Commands/NewCommand.cs`
- [ ] 2.2 模板字符串常量（WorkspaceTemplate / LibPresetTemplate / ExePresetTemplate / GitignoreTemplate）
- [ ] 2.3 实现 `--workspace <dir>` 模式
- [ ] 2.4 实现 `-p <name> --kind <lib|exe> --entry?` 模式
- [ ] 2.5 实现 `init` 模式（升级现有单 manifest）
- [ ] 2.6 错误处理：DirectoryNotEmpty / NotInWorkspace / AlreadyInWorkspace
- [ ] 2.7 单元测试 `NewCommandTests.cs`

## 阶段 3: FmtCommand

- [ ] 3.1 新增 `src/compiler/z42.Driver/Commands/FmtCommand.cs`
- [ ] 3.2 字段顺序规范常量
- [ ] 3.3 Tomlyn round-trip 实现（保留注释）
- [ ] 3.4 单元测试 `FmtCommandTests.cs`：字段排序 / 注释保留 / 多文件

## 阶段 4: WS004 移除

- [ ] 4.1 删除 `src/compiler/z42.Project/ManifestErrors.cs` 中 WS004 常量
- [ ] 4.2 删除 `docs/design/error-codes.md` 中 WS004 占位
- [ ] 4.3 `grep -r "WS004"` 在 src/ 与 docs/ 中检查残留（仅 spec/archive 保留历史）
- [ ] 4.4 `dotnet build` 验证无引用残留

## 阶段 5: Program.cs 路由整合

- [ ] 5.1 修改 `src/compiler/z42.Driver/Program.cs`：路由 clean / new / init / fmt
- [ ] 5.2 更新 `--help` 输出包含完整命令列表
- [ ] 5.3 编译验证

## 阶段 6: 测试 + 文档

- [ ] 6.1 验证 dotnet test 全绿
- [ ] 6.2 验证 cargo build / VM 全绿
- [ ] 6.3 修改 `docs/design/project.md`：L7 章节追加脚手架 / clean / fmt 子节
- [ ] 6.4 修改 `docs/design/compiler-architecture.md`：标注 C4 系列完成
- [ ] 6.5 修改 `docs/design/error-codes.md`：删除 WS004
- [ ] 6.6 修改 `docs/dev.md`：脚手架 + clean 使用示例
- [ ] 6.7 修改 `docs/roadmap.md`：C4c 完成 + workspace 工具链全 ✅

---

## 验证清单（GREEN）

- [ ] `dotnet build src/compiler/z42.slnx` 无错
- [ ] `cargo build --manifest-path src/runtime/Cargo.toml` 无错
- [ ] `dotnet test src/compiler/z42.Tests/` 全绿
- [ ] `./scripts/test-vm.sh` 全绿
- [ ] `grep -r "WS004" src/ docs/` 无残留（除 spec/archive）

## 备注

- C4c 是 workspace 演进规划（C1+C2+C3+C4a+C4b+C4c）的最后一段
- 完成后，z42 monorepo 工具链达到 Cargo 同等基础能力（除 lockfile / publish 等 future 功能）
- `z42c run` 与 `z42c test` 占位 future（依赖 VM 元数据 + 测试框架，分别在 M5+ / M7 阶段）
