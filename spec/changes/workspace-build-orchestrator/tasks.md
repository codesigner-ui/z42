# Tasks: workspace 编译核心运行时（C4a）

> 状态：🟡 待实施 | 创建：2026-04-26 | 依赖：C1+C2+C3 落地

## 进度概览
- [ ] 阶段 1: CommandContext + IZ42Command 接口
- [ ] 阶段 2: MemberDependencyGraph
- [ ] 阶段 3: WorkspaceBuildOrchestrator（串行 + WS001/002/006）
- [ ] 阶段 4: PackageCompiler 新入口 CompileFromResolved
- [ ] 阶段 5: BuildCommand + Program.cs 路由
- [ ] 阶段 6: 测试 + examples/workspace-full/
- [ ] 阶段 7: 文档同步

---

## 阶段 1: CommandContext + IZ42Command

- [ ] 1.1 新增 `src/compiler/z42.Driver/Commands/CommandContext.cs`：CommandContext record + IZ42Command 接口
- [ ] 1.2 验证编译

## 阶段 2: MemberDependencyGraph

- [ ] 2.1 新增 `src/compiler/z42.Compiler/MemberDependencyGraph.cs`：DFS 三色检测环
- [ ] 2.2 实现 TopologicalLayers / FindCycle 方法
- [ ] 2.3 单元测试 `MemberDependencyGraphTests.cs`：单链 / 多分支 / 自环 / 间接环

## 阶段 3: WorkspaceBuildOrchestrator

- [ ] 3.1 新增 `src/compiler/z42.Compiler/WorkspaceBuildOrchestrator.cs`
- [ ] 3.2 BuildOptions / BuildReport record
- [ ] 3.3 计算待编译 set（Selected ∪ default ∪ all） \ Excluded
- [ ] 3.4 闭包计算（加入传递依赖）
- [ ] 3.5 WS001 重复 member name 检测
- [ ] 3.6 WS002 -p / --exclude 冲突检测
- [ ] 3.7 调用 MemberDependencyGraph，WS006 环检测
- [ ] 3.8 串行遍历拓扑层，调用 PackageCompiler.CompileFromResolved
- [ ] 3.9 失败传播 blocked 标记
- [ ] 3.10 单元测试 `WorkspaceBuildOrchestratorTests.cs`：拓扑 / blocked / WS001/002/006

## 阶段 4: PackageCompiler 新入口

- [ ] 4.1 修改 `src/compiler/z42.Pipeline/PackageCompiler.cs`：增加 `CompileFromResolved(ResolvedManifest, ...)` 入口
- [ ] 4.2 复用 PipelineCore 的 TypeCheck / Codegen；产物路径走 rm.EffectiveProductPath
- [ ] 4.3 单工程入口（现有 `Compile(ProjectManifest, ...)`）保持不变

## 阶段 5: BuildCommand + Program.cs 路由

- [ ] 5.1 新增 `src/compiler/z42.Driver/Commands/BuildCommand.cs`：参数解析 + 三模式分派
- [ ] 5.2 修改 `src/compiler/z42.Driver/Program.cs`：在 path 解析前接 workspace 发现
- [ ] 5.3 支持 flag：`--workspace` / `-p` / `--exclude` / `--release` / `--profile X` / `--check-only` / `--no-workspace`
- [ ] 5.4 单元测试 `BuildCommandTests.cs`：三模式分派 / flag 解析

## 阶段 6: 测试 + examples

- [ ] 6.1 新增 `examples/workspace-full/` 完整跨 member 依赖样例（core ← utils ← hello）
- [ ] 6.2 集成测试：从 examples/workspace-full/ 加载 + orchestrator 执行（不实际写产物，用 --check-only）
- [ ] 6.3 验证：dotnet test 全绿
- [ ] 6.4 验证：cargo build + ./scripts/test-vm.sh 全绿

## 阶段 7: 文档同步

- [ ] 7.1 修改 `docs/design/project.md`：新增 L7 章节 "z42c CLI 命令矩阵（基础）" 含 build/check + workspace 发现
- [ ] 7.2 修改 `docs/design/compiler-architecture.md`：WorkspaceBuildOrchestrator + MemberDependencyGraph 设计原理
- [ ] 7.3 修改 `docs/design/error-codes.md`：WS001/WS002/WS006 启用
- [ ] 7.4 修改 `docs/dev.md`：workspace 模式构建命令示例
- [ ] 7.5 修改 `docs/roadmap.md`：C4a 进度

---

## 验证清单（GREEN）

- [ ] `dotnet build src/compiler/z42.slnx` 无错
- [ ] `cargo build --manifest-path src/runtime/Cargo.toml` 无错
- [ ] `dotnet test src/compiler/z42.Tests/` 全绿
- [ ] `./scripts/test-vm.sh` 全绿

## 备注

- 并行编译 / 跨 member 增量 / RunCommand 推迟到 C4b/C4c 之后或 future
- WS004 在 C4c 阶段彻底移除（C4a/b 不动它）
