# Tasks: fix-generic-interface-dispatch

> 状态：🟢 已完成 | 创建：2026-04-26 | 完成：2026-04-26 | 类型：fix (typecheck 架构)

## 进度概览
- [x] 阶段 1: Z42InterfaceType 加 TypeParams 字段 + 全量构造点更新
- [x] 阶段 2: BuildInterfaceSubstitutionMap helper + TypeChecker.Calls / Exprs 加 substitute
- [x] 阶段 3: RequireAssignable ClassType/InstantiatedType → InterfaceType 路径加 TypeArgs 比较
- [x] 阶段 4: golden test 101 + 全量回归
- [x] 阶段 5: 文档同步 + 归档

---

## 阶段 1: Z42InterfaceType TypeParams 字段

- [x] 1.1 `Z42Type.cs` 加 TypeParams 字段（nullable，末位）
- [x] 1.2 全部构造点补 TypeParams 实参：
  - `ImportedSymbolLoader.BuildInterfaceSkeleton` — 从 ExportedInterfaceDef.TypeParams
  - `ImportedSymbolLoader.FillInterfaceMembersInPlace` — 保持 skeleton 的 TypeParams
  - `SymbolCollector.CollectInterfaces` — 从 InterfaceDecl.TypeParams（local）
  - `SymbolTable.ResolveGenericType` — 实例化时继承 def.TypeParams
  - `ExportedTypeExtractor.ExtractInterfaces` — 反向写入 TSIG 用 it.TypeParams（之前
    cu lookup fallback 在跨包路径不可靠）

## 阶段 2: 方法 dispatch substitute

- [x] 2.1 `TypeChecker.cs` 新增 `BuildInterfaceSubstitutionMap(Z42InterfaceType)` 私有 helper
- [x] 2.2 `TypeChecker.Calls.cs` Z42InterfaceType 分支：`imtSub = SubstituteTypeParams(imt, subMap)`
- [x] 2.3 `TypeChecker.Exprs.cs` BindMemberExpr Z42InterfaceType 分支同步替换
  property getter return type + auto-property setter

## 阶段 3: 赋值兼容性 TypeArgs-aware

- [x] 3.1 `TypeChecker.cs RequireAssignable` Z42ClassType → Z42InterfaceType
  改用 `ClassImplementsInterfaceWithArgs` + `InterfacesEqual`（按 TypeArgs 全量比对）
- [x] 3.2 同步处理 `Z42InstantiatedType → Z42InterfaceType`

## 阶段 4: golden test + 全量回归

- [x] 4.1 新增 `run/101_generic_interface_dispatch`：MyInt 实现 IEquatable<int>，
  IntDescComparer 实现 IComparer<int>，CheckEq / CallCmp 接口形参调用
- [x] 4.2 `./scripts/build-stdlib.sh` 5/5
- [x] 4.3 `./scripts/regen-golden-tests.sh` 重生
- [x] 4.4 GREEN：
  - dotnet test 586/586
  - test-vm 188/188（94 interp + 94 jit）
  - cargo test 61/61
- [x] 4.5 100_comparer_contract 等既有测试未破

## 阶段 5: 文档同步 + 归档

- [x] 5.1 `docs/design/compiler-architecture.md` 新增"泛型接口 dispatch — Z42InterfaceType.TypeParams"小节
- [x] 5.2 tasks.md 状态 → `🟢 已完成`
- [x] 5.3 归档 + commit + push（scope `fix(typecheck)`）

## 备注

- 本变更曾因 stdlib bootstrap 失败 stash 暂存（zpkg cache 清空后多 CU 包内
  cross-file symbol 不可见）。先完成 `fix-package-compiler-cross-file`（commit
  ff23aac）解决根因，再恢复推进。
- 实施过程中加的 `Console.Error.WriteLine` debug 日志在归档前清理完毕。
