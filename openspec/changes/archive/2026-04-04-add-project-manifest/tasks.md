# Tasks: add-project-manifest

> 状态：🟢 已完成 | 创建：2026-04-03

## 进度概览
- [ ] 阶段 1: 依赖与数据结构
- [ ] 阶段 2: 核心实现
- [ ] 阶段 3: 验证

---

## 阶段 1: 依赖与数据结构

- [ ] 1.1 `z42.Driver.csproj` 添加 `Tomlyn` 和 `Microsoft.Extensions.FileSystemGlobbing`
- [ ] 1.2 新增 `ProjectManifest.cs`：定义 TOML 反序列化模型（ProjectSection / SourcesSection / BuildSection / ProfileSection）
- [ ] 1.3 实现 `ProjectManifest.Discover()`：在当前目录查找 `*.z42.toml`，处理 0 个 / 1 个 / 多个三种情况
- [ ] 1.4 实现 `ProjectManifest.Load(path)`：解析 TOML，推断缺失的 `name` 和 `emit`，校验 exe 必须有 `entry`

## 阶段 2: 核心实现

- [ ] 2.1 `Program.cs` 重构：将现有单文件编译流程提取为 `CompileFile(sourceFile, emitMode, outDir)` 方法
- [ ] 2.2 新增 `BuildCommand.cs`：实现 `z42c build` 子命令
  - 解析 `--release` / `--profile <name>` / `--emit <mode>` flags
  - 调用 `ProjectManifest.Discover/Load`
  - 用 `FileSystemGlobbing` 展开 source glob
  - 按 profile 选择 emit / mode / out_dir
  - 循环调用 `CompileFile`，产物写入 `out_dir`
- [ ] 2.3 `Program.cs` 路由：`arg[0] == "build"` 时转发到 `BuildCommand.Run()`
- [ ] 2.4 更新 help 文本：明确分 **Project mode** 和 **Single-file mode** 两块
- [ ] 2.5 更新 `examples/hello.z42.toml`（原 `examples/z42.toml` 重命名并对齐最终 schema）

## 阶段 3: 验证

- [ ] 3.1 `dotnet build && cargo build` —— 无编译错误
- [ ] 3.2 新增单元测试：`ProjectManifest` 解析（最小 exe、最小 lib、name 推断、缺 entry 报错）
- [ ] 3.3 `dotnet test` —— 全绿
- [ ] 3.4 手动验证：`cd examples && z42c build` 能正确输出产物到 `dist/`
- [ ] 3.5 手动验证：`z42c hello.z42` 单文件模式不受影响
- [ ] 3.6 更新 `docs/design/project.md`：文件名改为 `<name>.z42.toml`，补充 CLI 分工说明
- [ ] 3.7 `./scripts/test-vm.sh` —— 全绿

## 备注
