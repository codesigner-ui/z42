# Tasks: Policy 与集中产物布局（C3）

> 状态：🟡 待实施 | 创建：2026-04-26 | 依赖：C1 + C2 落地

## 进度概览
- [ ] 阶段 1: PolicyFieldPath + WorkspaceManifest 数据模型扩展
- [ ] 阶段 2: PolicyEnforcer 模块
- [ ] 阶段 3: CentralizedBuildLayout 模块
- [ ] 阶段 4: 接入 ManifestLoader + PackageCompiler
- [ ] 阶段 5: 错误码与诊断
- [ ] 阶段 6: 测试与示例
- [ ] 阶段 7: 文档同步

---

## 阶段 1: PolicyFieldPath + 数据模型扩展

- [ ] 1.1 新增 `src/compiler/z42.Project/PolicyFieldPath.cs`：点路径 tokenizer + ResolvedManifest 字段查找
- [ ] 1.2 修改 `src/compiler/z42.Project/WorkspaceManifest.cs`：`[policy]` 段从占位变为 `IReadOnlyDictionary<string, object>`
- [ ] 1.3 修改 `src/compiler/z42.Project/ResolvedManifest.cs`：`BuildConfig` 增加 `IsCentralized` / `EffectiveOutDir` / `EffectiveCacheDir` / `EffectiveProductPath` 字段
- [ ] 1.4 验证：`dotnet build src/compiler/z42.slnx` 通过

## 阶段 2: PolicyEnforcer 模块

- [ ] 2.1 新增 `src/compiler/z42.Project/PolicyEnforcer.cs`：
  - 计算最终 policy 字典（默认 ∪ 显式）
  - 遍历检查每个锁定字段
  - 收集 PolicyViolation
- [ ] 2.2 实现 fuzzy 建议（编辑距离 ≤3 的有效字段路径）
- [ ] 2.3 在 Origins 字典中标记 PolicyLocked 来源（来自 C2 已扩展的 OriginKind）

## 阶段 3: CentralizedBuildLayout 模块

- [ ] 3.1 新增 `src/compiler/z42.Project/CentralizedBuildLayout.cs`：路径派生
- [ ] 3.2 接入 `PathTemplateExpander`：`${profile}` / `${workspace_dir}` / `${member_name}` 展开
- [ ] 3.3 cache 路径按 member 分目录
- [ ] 3.4 单工程模式 fallback：未在 workspace 模式时用 member-local 布局

## 阶段 4: 接入 ManifestLoader + PackageCompiler

- [ ] 4.1 修改 `src/compiler/z42.Project/ManifestLoader.cs`：在合并后调用 PolicyEnforcer + CentralizedBuildLayout
- [ ] 4.2 修改 `src/compiler/z42.Compiler/PackageCompiler.cs`：产物路径改为 `manifest.Build.EffectiveProductPath`
- [ ] 4.3 修改 IncrementalCompiler（如存在）：cache 路径走 EffectiveCacheDir
- [ ] 4.4 单工程模式不动：保留原 member-local 行为

## 阶段 5: 错误码与诊断

- [ ] 5.1 修改 `src/compiler/z42.Project/ManifestErrors.cs`：
  - 追加 WS010 / WS011 常量与异常工厂
  - 标记 WS004 废弃（保留常量但加 `[Obsolete]`，C4 归档时彻底移除）
- [ ] 5.2 错误信息含锁定值、试图设的值、两端文件位置

## 阶段 6: 测试与示例

- [ ] 6.1 新增 `src/compiler/z42.Tests/PolicyEnforcerTests.cs`：
  - 默认锁定生效（member 改 `out_dir` → WS010）
  - 显式锁定生效
  - 锁定字段值与原值相同 → 不报错
  - WS011 字段路径不存在 + fuzzy 建议
  - preset 违规 → WS010 来源标注
- [ ] 6.2 新增 `src/compiler/z42.Tests/CentralizedBuildLayoutTests.cs`：
  - 默认 `dist` / `.cache` 路径
  - `${profile}` 模板展开
  - cache 按 member 分目录
  - 单工程模式 fallback
- [ ] 6.3 新增 `src/compiler/z42.Tests/PolicyIntegrationTests.cs`：端到端 `examples/workspace-with-policy/`
- [ ] 6.4 新增 `examples/workspace-with-policy/` 完整样例（见 proposal Scope 表）
- [ ] 6.5 修改既有测试（如 ZpkgWriter 测试）：产物路径期望值改用 `EffectiveProductPath`，不写死
- [ ] 6.6 验证：`dotnet test src/compiler/z42.Tests/` 全绿
- [ ] 6.7 验证：`./scripts/test-vm.sh` 全绿

## 阶段 7: 文档同步

- [ ] 7.1 修改 `docs/design/project.md`：
  - L3 段补充 workspace 模式下 `[build]` 行为差异
  - 新增 L6.6 章节"policy 与集中产物布局"
  - 字段速查区补 `[policy]` 段语法
- [ ] 7.2 修改 `docs/design/compiler-architecture.md`：PolicyEnforcer / CentralizedBuildLayout 设计原理
- [ ] 7.3 修改 `docs/design/error-codes.md`：追加 WS010 / WS011；删除 WS004 旧定义
- [ ] 7.4 spec scenarios 逐条覆盖确认

---

## 验证清单（GREEN）

- [ ] `dotnet build src/compiler/z42.slnx` 无错误
- [ ] `cargo build --manifest-path src/runtime/Cargo.toml` 无错误
- [ ] `dotnet test src/compiler/z42.Tests/` 全绿
- [ ] `./scripts/test-vm.sh` 全绿
- [ ] 所有 spec scenario 在测试中能找到对应 case
- [ ] 文档同步完成

## 备注

- C3 改变了产物输出位置（从 member-local 到 workspace 集中），可能导致 ZpkgWriter / IncrementalCompiler 等既有测试需要更新期望路径 → **不算 Scope 越界**，因为这些测试都是消费 ResolvedManifest 的下游测试，必然受影响；只要不修改 [ZpkgWriter.cs](../../../src/compiler/z42.Project/ZpkgWriter.cs) 自身（仅改其调用方与测试期望）即可
- C4 起将依赖 C3 的 EffectiveProductPath / EffectiveCacheDir 给 build orchestrator 使用
- WS004 在 C3 阶段标记废弃但暂不移除常量，C4 归档时清除（避免一次变更触及太多）
