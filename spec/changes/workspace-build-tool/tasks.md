# Tasks: z42c workspace 构建工具链（C4）

> 状态：🟡 待实施 | 创建：2026-04-26 | 依赖：C1 + C2 + C3 落地

## 进度概览
- [ ] 阶段 1: 命令路由骨架与 CommandContext
- [ ] 阶段 2: WorkspaceBuildOrchestrator 与依赖图
- [ ] 阶段 3: BuildCommand / CheckCommand / RunCommand
- [ ] 阶段 4: InfoCommand / MetadataCommand / TreeCommand / LintManifestCommand
- [ ] 阶段 5: CleanCommand / NewCommand / FmtCommand
- [ ] 阶段 6: IncrementalReusePolicy
- [ ] 阶段 7: CliOutputFormatter（错误友好输出）
- [ ] 阶段 8: 测试与集成示例
- [ ] 阶段 9: 文档同步与 WS004 清理

---

## 阶段 1: 命令路由骨架

- [ ] 1.1 新增 `src/compiler/z42.Driver/Commands/CommandContext.cs`：workspace 根 / profile / verbosity / cancellation
- [ ] 1.2 新增 `IZ42Command` 接口（暂放在 CommandContext.cs 同文件，超 200 行再拆）
- [ ] 1.3 修改 `src/compiler/z42.Driver/Program.cs`：subcommand 路由表，调用 ManifestLoader 发现 workspace 根 + 单工程 fallback
- [ ] 1.4 为单文件模式（`.z42` 路径）保留 fallback 逻辑

## 阶段 2: WorkspaceBuildOrchestrator 与依赖图

- [ ] 2.1 新增 `src/compiler/z42.Compiler/MemberDependencyGraph.cs`：节点 + 三色 DFS 环检测
- [ ] 2.2 新增 `src/compiler/z42.Compiler/WorkspaceBuildOrchestrator.cs`：BuildOptions / BuildReport / 拓扑分层 + 并行
- [ ] 2.3 实现 SemaphoreSlim 限流 + fail-fast 取消传播
- [ ] 2.4 上游失败 → 下游标记 blocked
- [ ] 2.5 修改 `src/compiler/z42.Compiler/PackageCompiler.cs`：增加 `CompileMemberAsync(member, ctx)` 入口

## 阶段 3: BuildCommand / CheckCommand / RunCommand

- [ ] 3.1 新增 `src/compiler/z42.Driver/Commands/BuildCommand.cs`：
  - 参数解析：`-p` / `--workspace` / `--exclude` / `--release` / `--profile` / `--no-incremental` / `--jobs N` / `--fail-fast`
  - 单文件 / 单工程 / workspace 三模式分派
  - 调用 WorkspaceBuildOrchestrator
- [ ] 3.2 `check` 复用 BuildCommand：仅类型检查 + IR 生成，不写产物
- [ ] 3.3 新增 `src/compiler/z42.Driver/Commands/RunCommand.cs`：编译 + 启动 VM 进程
- [ ] 3.4 RunCommand 拒绝 lib member（"no entry, cannot run"）
- [ ] 3.5 错误码 WS001-007 在 CLI 层友好输出（暂占位 `Console.Error.WriteLine`，阶段 7 用 CliOutputFormatter 替换）

## 阶段 4: 查询命令

- [ ] 4.1 新增 `src/compiler/z42.Driver/Commands/InfoCommand.cs`：
  - 无参数 → workspace 概览
  - `--resolved -p <name>` → 字段表 + Origins 链
  - `--include-graph -p <name>` → ASCII 树
- [ ] 4.2 新增 `src/compiler/z42.Driver/Commands/MetadataCommand.cs`：
  - `--format json` 输出
  - `schema_version: 1` + `members` + `dependency_graph`
- [ ] 4.3 新增 `src/compiler/z42.Driver/Commands/TreeCommand.cs`：ASCII 依赖图
- [ ] 4.4 新增 `src/compiler/z42.Driver/Commands/LintManifestCommand.cs`：
  - 调用 ManifestLoader 仅做静态校验（不编译）
  - 输出 WS001-007 / WS010-039 的全部潜在问题

## 阶段 5: 清理与脚手架

- [ ] 5.1 新增 `src/compiler/z42.Driver/Commands/CleanCommand.cs`：
  - 全 workspace clean → 删 `EffectiveOutDir` / `EffectiveCacheDir`
  - `-p <name>` → 删单 member 产物 + cache 子目录
  - 单工程模式 → 删 member-local
- [ ] 5.2 新增 `src/compiler/z42.Driver/Commands/NewCommand.cs`：
  - `--workspace <dir>` → 生成完整初始布局
  - `-p <name> --kind lib|exe` → 在当前 workspace 内新增 member
  - `init` → 升级现有单 manifest 为 workspace
- [ ] 5.3 新增 `src/compiler/z42.Driver/Commands/FmtCommand.cs`：
  - 解析 manifest → 重新序列化（字段排序 + 缩进）
  - 不破坏注释（用 Tomlyn 的 Round-trip API）

## 阶段 6: IncrementalReusePolicy

- [ ] 6.1 新增 `src/compiler/z42.Compiler/IncrementalReusePolicy.cs`：三层判定
- [ ] 6.2 manifest_hash 存储：`<cache_dir>/<member>/.manifest_hash`
- [ ] 6.3 upstream_zpkg_hash：扩展 ZpkgWriter 写入 dependencies hash 字段
- [ ] 6.4 接入 WorkspaceBuildOrchestrator：每个 member 编译前调用 Decide

## 阶段 7: CliOutputFormatter

- [ ] 7.1 新增 `src/compiler/z42.Driver/CliOutputFormatter.cs`：
  - WS00x / WS010-039 错误信息友好化（含上下文行 / help / note）
  - `--no-pretty` 输出纯文本
- [ ] 7.2 替换阶段 3-5 中的 `Console.Error.WriteLine` 占位

## 阶段 8: 测试与集成示例

- [ ] 8.1 新增 `examples/workspace-full/` 完整样例（见 proposal）
- [ ] 8.2 新增各 Command 单测：
  - `CommandRoutingTests.cs`
  - `WorkspaceBuildOrchestratorTests.cs`
  - `MemberDependencyGraphTests.cs`
  - `IncrementalReusePolicyTests.cs`
  - `InfoCommandTests.cs`
  - `MetadataCommandTests.cs`
  - `TreeCommandTests.cs`
  - `CleanCommandTests.cs`
  - `NewCommandTests.cs`
  - `FmtCommandTests.cs`
  - `LintManifestCommandTests.cs`
  - `RunCommandTests.cs`
- [ ] 8.3 新增 `WorkspaceBuildIntegrationTests.cs`：端到端跑 `examples/workspace-full/` build / clean / info / metadata
- [ ] 8.4 验证：`dotnet test src/compiler/z42.Tests/` 全绿
- [ ] 8.5 验证：`./scripts/test-vm.sh` 全绿（C4 不动 VM）

## 阶段 9: 文档与 WS004 清理

- [ ] 9.1 修改 `docs/design/project.md`：新增 L7 章节"z42c CLI 命令矩阵 + 发现机制"
- [ ] 9.2 修改 `docs/design/compiler-architecture.md`：WorkspaceBuildOrchestrator / MemberDependencyGraph / IncrementalReusePolicy 设计原理
- [ ] 9.3 修改 `docs/design/error-codes.md`：WS001-007 完整说明；删除 WS004 残留
- [ ] 9.4 修改 `docs/dev.md`：构建命令更新（workspace 模式常用命令）
- [ ] 9.5 修改 `docs/roadmap.md`：M6 阶段进度推进（workspace 工具链 ✅）
- [ ] 9.6 删除 `src/compiler/z42.Project/ManifestErrors.cs` 中 WS004 常量
- [ ] 9.7 `grep -r "WS004"` 确认无残留引用
- [ ] 9.8 spec scenarios 逐条覆盖确认

---

## 验证清单（GREEN）

- [ ] `dotnet build src/compiler/z42.slnx` 无错误
- [ ] `cargo build --manifest-path src/runtime/Cargo.toml` 无错误
- [ ] `dotnet test src/compiler/z42.Tests/` 全绿
- [ ] `./scripts/test-vm.sh` 全绿
- [ ] 所有 spec scenario 在测试中能找到对应 case
- [ ] 文档同步完成
- [ ] WS004 完全清除

## 备注

- C4 是工作量最大的 spec（13 个新 Command 文件 + 3 个新核心模块 + 12 个新测试类）
- 实施时建议按阶段顺序，每阶段独立 GREEN 后再进下一阶段；批量授权下若某阶段超出预期工作量，立即停下汇报（中断条件 7）
- examples/workspace-full/ 必须能真正跑通 build → run，作为 C1+C2+C3+C4 整体验收
