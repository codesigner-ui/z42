# Tasks: add-multi-exe-target

> 状态：🟡 进行中 | 创建：2026-04-04

## 进度概览
- [ ] 阶段 1: 数据结构
- [ ] 阶段 2: 核心实现
- [ ] 阶段 3: 验证

## 阶段 1: 数据结构
- [ ] 1.1 `ProjectManifest.cs` 新增 `ExeTarget` record
- [ ] 1.2 `ProjectManifest` 新增 `ExeTargets` 属性，解析 `[[exe]]`
- [ ] 1.3 `ProjectManifest.Load` 中校验 `[[exe]]` 与 `kind="exe"` 不共存
- [ ] 1.4 `ResolveSourceFiles` 新增 `ExeTarget?` 重载

## 阶段 2: 核心实现
- [ ] 2.1 `BuildCommand.Run` 新增多目标构建路径
- [ ] 2.2 解析 `--exe <name>` flag，过滤目标
- [ ] 2.3 更新 `examples/hello.z42.toml` 演示多 exe

## 阶段 3: 验证
- [ ] 3.1 `dotnet build` —— 无编译错误
- [ ] 3.2 新增单元测试：多 exe 解析、name/entry 缺失报错、src 覆盖、兼容性
- [ ] 3.3 `dotnet test` —— 全绿
- [ ] 3.4 `./scripts/test-vm.sh` —— 全绿
- [ ] 3.5 更新 `docs/design/project.md`
