# Tasks: include 机制（C2）

> 状态：🟡 待实施 | 创建：2026-04-26 | 依赖：C1 落地

## 进度概览
- [ ] 阶段 1: IncludeResolver 模块
- [ ] 阶段 2: ManifestMerger 模块
- [ ] 阶段 3: 接入 ManifestLoader
- [ ] 阶段 4: 错误码与诊断
- [ ] 阶段 5: 测试
- [ ] 阶段 6: 示例与文档同步

---

## 阶段 1: IncludeResolver 模块

- [ ] 1.1 新增 `src/compiler/z42.Project/IncludeResolver.cs`：DFS 实现，支持 `visiting` / `visited` 双集合
- [ ] 1.2 实现路径合法性校验（绝对路径 / URL / glob → WS024）
- [ ] 1.3 实现路径不存在检测（WS023）
- [ ] 1.4 实现循环检测（WS020），错误信息含完整环路径
- [ ] 1.5 实现深度限制（WS022），错误信息含完整链
- [ ] 1.6 接入 `PathTemplateExpander`：include 路径展开 `${workspace_dir}` / `${member_dir}`
- [ ] 1.7 路径规范化（统一 `/`、相对路径解析）

## 阶段 2: ManifestMerger 模块

- [ ] 2.1 新增 `src/compiler/z42.Project/ManifestMerger.cs`：合并入口
- [ ] 2.2 实现标量字段后者覆盖（`null/missing` → 保留前者；非空 → 覆盖）
- [ ] 2.3 实现表字段递归合并
- [ ] 2.4 实现数组字段整体覆盖语义
- [ ] 2.5 输出 Origins 字典：每个字段记录来源文件 + IncludePreset 链路
- [ ] 2.6 新增 `PresetValidator` 静态类：preset 段限制校验（WS021）
- [ ] 2.7 在 `ResolvedManifest.cs` 中扩展 `OriginKind`：增加 `IncludePreset`、`PolicyLocked`（占位）；`FieldOrigin` 增加 `IncludeChain` 字段

## 阶段 3: 接入 ManifestLoader

- [ ] 3.1 修改 `src/compiler/z42.Project/ManifestLoader.cs`：在 workspace 共享继承之后、字段验证之前调用 IncludeResolver + ManifestMerger
- [ ] 3.2 在 `MemberManifest.cs` 增加 `Include` 字段（字符串数组）
- [ ] 3.3 单工程模式（无 workspace）也支持 include —— 同一份 IncludeResolver 调用，preset 段限制一致

## 阶段 4: 错误码与诊断

- [ ] 4.1 修改 `src/compiler/z42.Project/ManifestErrors.cs`：追加 WS020-024 常量与异常工厂方法
- [ ] 4.2 错误信息含：被引用文件路径、声明 include 的文件、完整链/环路径
- [ ] 4.3 在 `ManifestException` 中携带 include chain（用于 C4 的 `--include-graph` 显示）

## 阶段 5: 测试

- [ ] 5.1 新增 `src/compiler/z42.Tests/IncludeResolverTests.cs`：
  - 路径解析正常 / 含模板变量
  - WS020 直接环 / 间接环
  - WS022 深度
  - WS023 路径不存在
  - WS024 绝对路径 / URL / glob
  - 菱形去重
- [ ] 5.2 新增 `src/compiler/z42.Tests/ManifestMergerTests.cs`：
  - 标量覆盖
  - 表字段级合并
  - 数组整体覆盖（不连接）
  - preset 含禁用段 → WS021（每个禁用段一个 case）
  - Origins 来源准确性
- [ ] 5.3 新增 `src/compiler/z42.Tests/IncludeIntegrationTests.cs`：端到端用 `examples/workspace-with-presets/`，对比 `expected_resolved.json`
- [ ] 5.4 验证：`dotnet test src/compiler/z42.Tests/` 全绿
- [ ] 5.5 验证：`./scripts/test-vm.sh` 全绿（C2 不应影响 VM）

## 阶段 6: 示例与文档同步

- [ ] 6.1 新增 `examples/workspace-with-presets/` 完整样例（见 proposal Scope 表 13 个文件）
- [ ] 6.2 修改 `docs/design/project.md`：在 L6 后新增"L6.5 include 机制"章节，含合并语义、路径规则、preset 段限制、嵌套与循环、与 workspace 共享的优先级关系
- [ ] 6.3 修改 `docs/design/compiler-architecture.md`：ManifestLoader 流程图增加 include 解析阶段，并说明 IncludeResolver / ManifestMerger 的数据结构与算法
- [ ] 6.4 修改 `docs/design/error-codes.md`：追加 WS020-024
- [ ] 6.5 spec scenarios 逐条覆盖确认

---

## 验证清单（GREEN）

- [ ] `dotnet build src/compiler/z42.slnx` 无错误
- [ ] `cargo build --manifest-path src/runtime/Cargo.toml` 无错误
- [ ] `dotnet test src/compiler/z42.Tests/` 全绿
- [ ] `./scripts/test-vm.sh` 全绿
- [ ] 所有 spec scenario 在测试中能找到对应 case
- [ ] 文档同步完成

## 备注

- C2 不影响 z42c CLI 行为；include 机制完全在 manifest 解析层
- C3 起将依赖 C2 的 ResolvedManifest.Origins 数据结构（PolicyLocked 来源类型）
- C2 不允许 workspace 根写 include（D2.6）；future 评估
