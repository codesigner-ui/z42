# Tasks: ImportedSymbolLoader 两阶段加载

> 状态：🟢 已完成 | 创建：2026-04-26 | 完成：2026-04-26 | 类型：refactor (typecheck 架构)

## 进度概览
- [x] 阶段 1: BuildSkeleton helpers
- [x] 阶段 2: FillMembers + ResolveTypeName 升级
- [x] 阶段 3: Load 函数主路径重构
- [x] 阶段 4: 单元测试 5/5 通过
- [x] 阶段 5: Golden test 95 + 全量回归（581/581 + 178/178 + 61/61）
- [x] 阶段 6: 文档同步 + 归档

---

## 阶段 1: BuildSkeleton helpers

- [ ] 1.1 新增 `BuildClassSkeleton(ExportedClassDef)` — 返回空成员的 Z42ClassType
- [ ] 1.2 新增 `BuildInterfaceSkeleton(ExportedInterfaceDef)` — 返回空成员的 Z42InterfaceType
- [ ] 1.3 单元测试：骨架 Name / TypeParams / BaseClassName 正确填，
  Fields / Methods 字典为空

## 阶段 2: FillMembers + ResolveTypeName 升级

- [ ] 2.1 `ResolveTypeName` 签名增加 `classes` / `interfaces` 参数（nullable）；
  逻辑加 lookup 优先级（prim > generic param > classes > interfaces > PrimType fallback）
- [ ] 2.2 `RebuildFuncType` 同步增加 `classes` / `interfaces` 参数，传递到
  `ResolveTypeName`
- [ ] 2.3 `FillClassMembers(cls, classes, interfaces, ...)` — 填充 Fields /
  Methods / StaticFields / StaticMethods / MemberVisibility，返回新的 final
  Z42ClassType
- [ ] 2.4 `FillInterfaceMembers(iface, classes, interfaces)` — 同上对接口
- [ ] 2.5 grep 所有 `ResolveTypeName(` / `RebuildFuncType(` / `RebuildClassType(`
  调用点，确认参数新增不破坏其他 caller（应该都在 ImportedSymbolLoader 内部）

## 阶段 3: Load 函数主路径重构

- [ ] 3.1 拆 `Load` 为 Phase 1 + Phase 2 两个 foreach；保留 namespace 过滤
- [ ] 3.2 Phase 1: 仅调 BuildSkeleton；存入 classes / interfaces 字典
- [ ] 3.3 Phase 2: 调 FillMembers，**替换**字典 value 为 final 实例；
  classes 的 enums / functions 等其他字段在此阶段顺便处理
- [ ] 3.4 删除旧的 `RebuildClassType` / `RebuildInterfaceType` 函数（被新函数替换）
- [ ] 3.5 验证：`git diff Z42Type.cs` 应为空（Decision 5 严格不动 IsAssignableTo）

## 阶段 4: 单元测试

新建或扩展 `src/compiler/z42.Tests/ImportedSymbolLoaderTests.cs`（如已存在
则追加）：

- [ ] 4.1 `Load_SelfReference_ProducesClassTypeNotPrimType`
  —— `class A { A f; }`，加载后 `A.Fields["f"]` is Z42ClassType
- [ ] 4.2 `Load_ForwardReference_ProducesClassTypeNotPrimType`
  —— A.field: B，A 在 B 前导入；A.Fields["field"] is Z42ClassType("B")
- [ ] 4.3 `Load_TrueUnknownType_StillPrimType`
  —— 拼写错误 / 未声明类型仍是 PrimType
- [ ] 4.4 `Load_InterfaceForwardReference`
  —— IEnumerable.GetEnumerator 返回 IEnumerator
- [ ] 4.5 `Load_GenericParamPriority`
  —— `class A<T> { T f; }` 优先识别 T 为 generic param 而非外层 class

## 阶段 5: Golden test + 回归验证

- [ ] 5.1 新增 `run/95_class_self_reference_field`：
  - 用户代码 `outer.InnerException = inner;` 编译通过 + 运行正确
  - 链式访问 `outer.InnerException.Message`
- [ ] 5.2 新增 `run/96_class_forward_reference`（可选，看 4.2 单测是否覆盖）
- [ ] 5.3 GREEN 验证：
  - [ ] `./scripts/build-stdlib.sh` 全部 success
  - [ ] `dotnet build` 0 errors
  - [ ] `cargo build` 0 errors
  - [ ] `dotnet test` 100% pass（含 5.1 新增）
  - [ ] `./scripts/test-vm.sh` 100% pass（interp + jit）
  - [ ] `cargo test` 100% pass
  - [ ] Wave 2 既有 91 / 92 仍绿（不应破坏）

## 阶段 6: 文档同步 + 归档

- [ ] 6.1 `docs/design/compiler-architecture.md` "TSIG 与跨包符号导入" 章节
  补"两阶段加载"小节，描述设计与决策
- [ ] 6.2 tasks.md 状态 → `🟢 已完成`
- [ ] 6.3 `spec/changes/fix-imported-type-two-phase-resolution/` →
  `spec/archive/2026-04-26-fix-imported-type-two-phase-resolution/`
- [ ] 6.4 commit + push（scope `refactor(typecheck)`）

## 备注

**实施变更**（design.md Decision 1 修订）：

- 原计划 Phase 2 替换 dict value（new Z42ClassType filled）。实施时发现 record
  immutability 导致**字段持有的 ClassType 引用是 Phase 1 空骨架**，下游
  TypeChecker 看到的 Exception.Fields 为空（"type Exception has no member
  Message"）。
- 改为 **in-place mutate Phase 1 骨架的 mutable Dictionary**（cast 回
  `Dictionary<>` 后 Add）。Phase 2 不替换 record，让所有 Phase 2 拿到的
  ClassType 引用与最终输出 ClassType 是同一对象 → 字段填充后立即对所有
  持有方可见。
- 这是更优的设计 — 利用 record container of references 的语义，dict 内容
  改了所有引用都看到。

**Wave 2 后插**（不在本 change scope 内）：测试 91 在 Wave 2 前移除了
inner-exception-chain 段；本变更归档不回填，留给 #2/#4 完成后的端到端
验证 step。
