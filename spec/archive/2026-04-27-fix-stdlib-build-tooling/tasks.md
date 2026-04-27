# Tasks: fix-stdlib-build-tooling

> 状态：🟢 已完成 | 类型：fix | 创建：2026-04-27 | 完成：2026-04-27

**变更说明：** 修复 wave1 实施时发现的两个 stdlib 构建工具问题（Issue 2 + Issue 3）。

**原因：**
- **Issue 2**：`build-stdlib.sh` 静默跳过 parser 失败的源文件，复用旧 zpkg。Path.z42 的 while 单语句无大括号触发 5 个错误，但 build 报 "5 succeeded, 0 failed"。Wave 1.4 实施时被这个坑了一次（手动单文件 dotnet 编译才暴露）
- **Issue 3**：`build-stdlib.sh` 写到 `artifacts/libraries/<lib>/dist/`，但 VM 加载 `artifacts/z42/libs/`，需要手动 `cp` 或 `package.sh`。Wave 1.1-1.5 每次都要手动同步

**根因：**
- Issue 2：[PackageCompiler.cs:487-509](src/compiler/z42.Pipeline/PackageCompiler.cs#L487) Phase 1 中 `try ... catch { /* defer to Phase 2 */ continue; }` 把 parse error 文件从 `parsedCus` 移除，Phase 2 又只迭代 `parsedCus` —— 文件永远不被报告
- Issue 3：build-stdlib.sh 仅写 workspace dist 目录，分发同步外移到 package.sh

**文档影响：**
- `src/libraries/README.md` 构建小节更新（删除"还需 cp / package.sh"提示）

## Tasks

- [x] 1.1 `src/compiler/z42.Pipeline/PackageCompiler.cs`：Phase 1 parse error 立即打印 + 计数，loop 后 `if (parseErrors > 0) return null`
- [x] 1.2 验证修复：临时破坏 Path.z42（while 无大括号），run build-stdlib.sh 应该 exit 1 + 显式错误
- [x] 2.1 `scripts/build-stdlib.sh`：成功后增加 `cp dist/*.zpkg → artifacts/z42/libs/`
- [x] 2.2 验证：删 `artifacts/z42/libs/*.zpkg`，run build-stdlib.sh 应该重新填充
- [x] 3.1 `src/libraries/README.md` 构建小节更新
- [x] 4.1 dotnet build / cargo build / dotnet test / test-vm.sh 全绿
- [x] 5.1 commit + push + 归档

## 备注

- Issue 1（静态字段访问）单独 spec
- Phase 1 / Phase 2 deferred-printing 的注释说"Phase 2 will re-parse and print" 是历史遗留，从来没真的工作过 —— 因为 Phase 2 只迭代 parsedCus（不含错误文件）。修复时直接砍掉 deferred 假设，Phase 1 立即打印
