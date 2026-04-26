# Tasks: 扩展 z42.toml workspace manifest schema（C1）

> 状态：🟢 已完成 | 创建：2026-04-26 | 归档：2026-04-26 | GREEN: dotnet 643/643 + cargo OK + VM 188/188

## 进度概览
- [ ] 阶段 1: Manifest 数据模型与错误码骨架
- [ ] 阶段 2: Workspace 发现与 members 展开
- [ ] 阶段 3: 字段继承与依赖语法
- [ ] 阶段 4: Member 段限制与诊断
- [ ] 阶段 5: 测试与示例
- [ ] 阶段 6: 文档同步

---

## 阶段 1: Manifest 数据模型与错误码骨架

- [ ] 1.1 新增 `src/compiler/z42.Project/WorkspaceManifest.cs`：`[workspace]` / `[workspace.project]` / `[workspace.dependencies]` 数据模型（含 `[workspace.build]` / `[policy]` 占位字段，C1 仅解析）
- [ ] 1.2 新增 `src/compiler/z42.Project/MemberManifest.cs`：member `<name>.z42.toml` 数据模型，支持 `xxx.workspace = true` 字段类型
- [ ] 1.3 新增 `src/compiler/z42.Project/ResolvedManifest.cs`：合并后最终配置 + `FieldOrigin` 来源链
- [ ] 1.4 新增 `src/compiler/z42.Project/ManifestErrors.cs`：定义 WS003/005/007/030-039 错误码常量与异常类型 `ManifestException`
- [ ] 1.5 新增 `src/compiler/z42.Project/PathTemplateExpander.cs`：实现 4 内置变量展开（`workspace_dir` / `member_dir` / `member_name` / `profile`）+ `$$` 转义 + WS037/038 诊断
- [ ] 1.6 验证：`dotnet build src/compiler/z42.slnx` 通过

## 阶段 2: Workspace 发现与 members 展开

- [ ] 2.1 新增 `src/compiler/z42.Project/ManifestLoader.cs` 主类，提供 `LoadWorkspace(string startDir)` 入口
- [ ] 2.2 实现向上查找 `z42.workspace.toml` 的 `DiscoverWorkspaceRoot` 方法（含 WS030 检测）
- [ ] 2.3 新增 `src/compiler/z42.Project/GlobExpander.cs`：支持 `*` / `**` 目录级 glob
- [ ] 2.4 实现 `members` + `exclude` 展开（含 WS005 同目录两份 manifest 检测）
- [ ] 2.5 实现 `default-members` 校验（WS031）
- [ ] 2.6 实现 orphan member 检测（WS007，warning 级）
- [ ] 2.7 实现 virtual manifest 检查（WS036：根含 `[project]` 即报错）
- [ ] 2.8 单元测试 `WorkspaceDiscoveryTests.cs` + `MembersExpansionTests.cs` + `VirtualManifestTests.cs`，覆盖正常/边界/各错误码

## 阶段 3: 字段继承与依赖语法

- [ ] 3.1 实现 `[workspace.project]` 共享字段引用：member 写 `version.workspace = true` 时拉入根值
- [ ] 3.2 限制可共享字段集合（仅 `version` / `authors` / `license` / `description`），其他字段写 `.workspace = true` 报 `WS032`（字段不存在）/ `WS033`（类型错误）
- [ ] 3.3 实现 `[workspace.dependencies]` 引用语法：`dep.workspace = true` 与表形式 `{ workspace = true, optional = true }` 均支持
- [ ] 3.4 实现旧语法检测（WS035：`version = "workspace"` 报错）
- [ ] 3.5 引用未声明依赖（WS034）
- [ ] 3.6 单元测试 `WorkspaceProjectInheritanceTests.cs` + `WorkspaceDependencyInheritanceTests.cs`

## 阶段 4: Member 段限制与诊断

- [ ] 4.1 检测 member `<name>.z42.toml` 含禁用段（`[profile.*]` / `[workspace.*]`）→ WS003
- [ ] 4.2 字段来源链记录：`ResolvedManifest.Origins` 填充 `MemberDirect` / `WorkspaceProject` / `WorkspaceDependency`
- [ ] 4.3 在 `ManifestLoader` 路径字段消费点接入 `PathTemplateExpander`：`include` 路径、`[workspace.dependencies] xxx.path`、`[dependencies] xxx.path`、`[sources] include / exclude`、`[workspace.build] out_dir / cache_dir`（C1 仅展开存值，C3 实施集中布局时再使用）
- [ ] 4.4 字段白名单守卫：在禁止字段（`name` / `version` / `kind` / `entry` / `members` 等）扫到 `${` 一律报 `WS039`
- [ ] 4.5 单元测试 `MemberForbiddenSectionsTests.cs`

## 阶段 5: 测试与示例

- [ ] 5.1 在 `examples/workspace-basic/` 创建样例 workspace（`z42.workspace.toml` + `libs/greeter/` + `apps/hello/`），含最小可解析的 .z42 源文件
- [ ] 5.2 在样例 workspace 中至少使用一处 `${workspace_dir}` 模板（如 `[workspace.dependencies] xxx.path`）以验证模板展开端到端工作
- [ ] 5.3 增加 manifest 解析 golden test：把 `examples/workspace-basic/` 解析结果序列化为 JSON，对照 `expected_resolved.json`
- [ ] 5.4 单元测试 `PathTemplateExpanderTests.cs`：4 变量正常展开、`$$` 转义、嵌套报 WS038、未闭合报 WS038、`${env:...}` 报 WS037、未知变量报 WS037、字段白名单 WS039
- [ ] 5.5 验证：`dotnet test src/compiler/z42.Tests/` 全绿
- [ ] 5.6 验证：`./scripts/test-vm.sh` 全绿（不应被 C1 影响）

## 阶段 6: 文档同步

- [ ] 6.1 重写 `docs/design/project.md` L6 段：固定文件名、新字段、新依赖语法、virtual manifest 说明、路径模板变量章节
- [ ] 6.2 在 [docs/design/project.md](../../../docs/design/project.md) 末尾"完整字段速查"区追加 `[workspace.project]` 字段表、`xxx.workspace = true` 引用语法、`${var}` 模板变量与允许字段白名单
- [ ] 6.3 同步 [docs/design/compiler-architecture.md](../../../docs/design/compiler-architecture.md)：新增 ManifestLoader 模块说明（依据 CLAUDE.md "实现原理文档规则"）
- [ ] 6.4 错误码索引追加到 [docs/design/error-codes.md](../../../docs/design/error-codes.md)（如尚不存在则新建占位段）
- [ ] 6.5 spec scenarios 逐条覆盖确认（与 `specs/workspace/spec.md` 对照）
- [ ] 6.6 [docs/roadmap.md](../../../docs/roadmap.md) 更新（如本变更触及 pipeline 进度则同步；C1 不触及 pipeline，可只补一行"工程文件 schema 演进"）

---

## 验证清单（阶段 8 GREEN 标准）

- [ ] `dotnet build src/compiler/z42.slnx` 无错误
- [ ] `cargo build --manifest-path src/runtime/Cargo.toml` 无错误（C1 不应影响 Rust 端）
- [ ] `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 全绿
- [ ] `./scripts/test-vm.sh` 全绿
- [ ] 所有 spec scenario 在测试中能找到对应 case
- [ ] 文档同步完成（阶段 6 全部勾选）

## 备注

- C1 不触及 z42c CLI 行为；`z42c build` / `z42c run` 等命令保持原状（待 C4 扩展）
- C1 的 ManifestLoader 已为 C2 (include) / C3 (policy) 留扩展点；后续无需重构数据模型
- 错误码 WS010-019（policy 冲突）与 WS020-029（include 相关）的段位仅占位，本变更不实现
- 现行单工程 `<name>.z42.toml` 解析路径**完全不变**，C1 仅在编译器入口路由出 workspace 模式
