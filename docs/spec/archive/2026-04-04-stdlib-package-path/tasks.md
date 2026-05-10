# Tasks: stdlib-package-path

> 状态：🟢 已完成 | 创建：2026-04-04 | 归档：2026-04-04

## 进度概览
- [ ] 阶段 1: VM 搜索路径探测
- [ ] 阶段 2: package.sh 打包脚本
- [ ] 阶段 3: 文档同步
- [ ] 阶段 4: 验证

## 阶段 1: VM 搜索路径探测

- [ ] 1.1 在 `src/runtime/src/main.rs` 中新增 `resolve_libs_dir()` 函数，实现三条搜索路径
- [ ] 1.2 在 `main()` 中调用 `resolve_libs_dir()`，扫描目录下 `.zpkg`/`.zbc` 文件并 `tracing::info!` 输出

## 阶段 2: package.sh 打包脚本

- [ ] 2.1 新建 `scripts/package.sh`，实现 build VM + 输出到 `artifacts/z42/bin/` + 占位 libs 到 `artifacts/z42/libs/`
- [ ] 2.2 赋予执行权限 `chmod +x scripts/package.sh`

## 阶段 3: 文档同步

- [ ] 3.1 更新 `docs/design/stdlib.md` 中 "stdlib Search Path" 一节（路径名 + 环境变量名）
- [ ] 3.2 更新 `.claude/CLAUDE.md` 构建与测试部分，新增 `package.sh` 说明

## 阶段 4: 验证

- [ ] 4.1 `dotnet build src/compiler/z42.slnx` —— 无编译错误
- [ ] 4.2 `cargo build --manifest-path src/runtime/Cargo.toml` —— 无编译错误
- [ ] 4.3 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` —— 全绿
- [ ] 4.4 `./scripts/test-vm.sh` —— 全绿
- [ ] 4.5 `./scripts/package.sh` —— artifacts/z42/ 产物正确
- [ ] 4.6 `artifacts/z42/bin/z42vm --verbose examples/hello.z42ir.json` —— log 中出现 libs dir 信息

## 备注

- stdlib `.z42` 尚无法编译，libs/ 下全部为空占位文件，M7 填充
- 搜索路径第 3 条（cwd）专为 `cargo run` 开发场景设计
