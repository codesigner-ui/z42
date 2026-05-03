# Tasks: 修复嵌套 generic `>>` 解析

> 状态：🟢 已完成 | 创建：2026-05-03 | 完成：2026-05-03 | 类型：lang/parser

## 进度概览
- [x] 阶段 1: TypeParser ParseInternal extraClose flag 线程穿透 + 5 个 depth-scan 站点
- [x] 阶段 2: 测试
- [x] 阶段 3: 验证 + 文档同步 + 归档

## 阶段 1: 实施
- [x] 1.1 改造 `TypeParser.cs`：内部 `ParseInternal()` 返回 `(Ok, Value, Remainder, Error, ExtraClose)` record struct；公开 `Parse(cursor)` 包装丢弃 ExtraClose
- [x] 1.2 generic 分支 loop 同时识别 Gt / GtGt / extraGtFromInner 作为退出条件
- [x] 1.3 close 检查处理三路：extraGtFromInner / Gt / GtGt（按 design Decision 2 算法）
- [x] 1.4 `TopLevelParser.Helpers.cs` `SkipGenericParams` 加 `case GtGt: depth -= 2`
- [x] 1.5 `TopLevelParser.Helpers.cs` `IsFieldDecl` 扫描 switch 加 `case GtGt: depth -= 2`
- [x] 1.6 `StmtParser.cs` 3 处 depth-scan 加 GtGt 处理
- [x] 1.7 `TopLevelParser.Members.cs` 1 处 depth-scan 加 GtGt 处理
- [x] 1.8 删除 `TopLevelParser.Helpers.cs:494-497` 的 known-limitation 注释
- [x] 1.9 实施中发现 bug：extraClose=true 时 `[]` `?` 后缀错误附着到 inner，加 `!extraClose` 守卫（`Foo<Bar<int>>[]` 的 `[]` 应附 outer）
- [x] 1.10 实施中发现 pre-existing IncrementalBuildIntegrationTests 失败（D2b 阶段 1 wrapper 文件保留导致 z42.core 36 → 38），Scope 扩展确认后修复测试计数

## 阶段 2: 测试
- [x] 2.1 `src/compiler/z42.Tests/ParserTests.cs` 加 7 个 nested generic 测试：
  - `FieldDecl_NestedGeneric_TwoLevels`
  - `FieldDecl_NestedGeneric_ThreeLevels`
  - `MethodParam_NestedGeneric`
  - `MethodReturn_NestedGeneric`
  - `LocalVar_NestedGeneric`
  - `FieldDecl_NestedGenericArray`（generic + array suffix 组合）
  - `FieldDecl_SingleGeneric_Regression`

## 阶段 3: 验证 + 文档 + 归档
- [x] 3.1 `dotnet build src/compiler/z42.slnx` ✅
- [x] 3.2 `cargo build` 不受影响（隐式通过 test-vm.sh）
- [x] 3.3 `dotnet test`：929/929 ✅（基线 +7 + IncrementalBuildIntegrationTests 修复）
- [x] 3.4 `./scripts/test-vm.sh`：230/230 ✅（interp 115 + jit 115）
- [x] 3.5 `./scripts/build-stdlib.sh` 6/6 ✅
- [x] 3.6 spec scenarios 逐条核对：8 scenario 全部由测试覆盖
- [x] 3.7 文档同步：
  - `docs/design/compiler-architecture.md` "Pratt 表达式解析" 后加 §嵌套 generic `>>` 拆分（算法 + Decision 来源）
  - `docs/roadmap.md` 历史表加 2026-05-03 fix-nested-generic-parsing 行
- [x] 3.8 移动 `spec/changes/fix-nested-generic-parsing/` → `spec/archive/2026-05-03-fix-nested-generic-parsing/`
- [x] 3.9 commit + push

## 备注
- 不在 lexer 层做 mode 切换（design Decision 1）
- TokenCursor 是 readonly struct + IReadOnlyList<Token>，原方案"原地 rewrite"违反 immutability；改为 `extraClose` flag 线程穿透（Decision 2）
- 修复后 D2b 仍阻塞于 Bug 2 (Z42InstantiatedType equality) + Bug 3 (member substitution)，独立 spec 推进
- Scope 实施中扩展 +2 文件（StmtParser.cs / TopLevelParser.Members.cs depth-scan 站点 + IncrementalBuildIntegrationTests 文件计数）
