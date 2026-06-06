# Tasks: 标准库自动可用

> 状态：🟡 待 6.5 gate | 创建：2026-06-06 | 类型：lang/compiler

## 进度概览
- [x] 阶段 1: WS013 lint（冗余 stdlib 声明）—— 实现 + 4 单测 + 2 对齐测试，dotnet 1523/1524（唯一红=分离 crypto WIP）
- [ ] 阶段 2: Std.* 命名空间保留（E0918）
- [ ] 阶段 3: 文档 + 示例清理
- [ ] 阶段 4: 测试与 GREEN

## 阶段 1: WS013 lint ✅
- [x] 1.1 `ManifestErrors.cs` WS013 常量 + `RedundantStdlibDep` 工厂；`WorkspaceCatalog.cs` 描述条目
- [x] 1.2 `ProjectManifest.cs` `ScanForRedundantStdlibDeps`：扫 `[dependencies]`/`[tests.dependencies]`/`[bench.dependencies]`，非 `z42.*` 声明方 + `z42.*` entry → WS013；z42.* declarer 豁免
- [x] 1.3 `TestBenchManifestTests.cs`：WS013 触发(deps/tests.deps) / inter-dep 豁免 / 第三方无警告 4 测；对齐 WS012_NormalDeps + ProjectManifest NoFalsePositives

## 阶段 2: Std.* 保留
- [ ] 2.1 定位 namespace 校验注入点（SingleFileCompiler / BuildTarget / semantic）
- [ ] 2.2 非 `z42.*` 包声明 `namespace Std*` → 报错 E0918（取下一空号）
- [ ] 2.3 `z42.Tests`：第三方 Std.* 报错 / stdlib 内 Std.* 通过

## 阶段 3: 文档 + 清理
- [ ] 3.1 `docs/design/compiler/project.md`：stdlib 自动可用约定 + Std.* 保留 + 第三方才声明
- [ ] 3.2 `docs/design/stdlib/overview.md`（或用户向 README）：无需声明 stdlib
- [ ] 3.3 `examples/*.z42.toml`：删用户示例里冗余的 stdlib `[dependencies]`
- [ ] 3.4 （对齐后，可选）清理 `add-tests-bench-manifest-config` 那 21 处 `[tests.dependencies] z42.test`

## 阶段 4: 验证
- [ ] 4.1 回归：空依赖 + `using Std.IO/Std.Math/Bencher` 编译通过
- [ ] 4.2 dotnet test 全绿（含新 WS013 / E0918 单测）
- [ ] 4.3 cargo test + vm/lib（确认 stdlib 加载不受影响）
- [ ] 4.4 spec scenarios 逐条覆盖

## 备注
- D5 关键：WS013 只对**非 z42.* 声明方**触发，保 stdlib 自身 inter-dep（build 排序）不误伤。
- 3.4 需与 `add-tests-bench-manifest-config` 作者对齐（那 21 处 z42.test 声明是否删）。
- 当前树有分离的 crypto 测试 dir-mode 重构 WIP（未跟踪 secp256k1/），与本变更无关。
