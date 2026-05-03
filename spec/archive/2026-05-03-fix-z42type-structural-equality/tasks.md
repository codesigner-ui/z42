# Tasks: Z42Type record 结构 equality

> 状态：🟢 已完成 | 创建：2026-05-03 | 完成：2026-05-03 | 类型：lang/typechecker

## 进度概览
- [x] 阶段 1: 三个 record (Z42InstantiatedType / Z42InterfaceType / Z42FuncType) 加 Equals/GetHashCode
- [x] 阶段 2: 测试
- [x] 阶段 3: 验证 + 文档同步 + 归档

## 阶段 1: 实施
- [x] 1.1 在 Z42Type 抽象基类加 `internal static bool ListEquals<T>(IReadOnlyList<T>?, IReadOnlyList<T>?)` 静态助手
- [x] 1.2 `Z42InstantiatedType` override Equals + GetHashCode（Definition by Name + TypeArgs element-wise）
- [x] 1.3 `Z42InterfaceType` override（Name + TypeArgs + TypeParams 深比；Methods/StaticMembers 走默认引用比较）
- [x] 1.4 `Z42FuncType` override（Params + Ret + RequiredCount 深比）

## 阶段 2: 测试
- [x] 2.1 NEW `src/compiler/z42.Tests/Z42TypeEqualityTests.cs`：15 个测试
  - Instantiated × 5（Same / Nested / DiffDef / DiffArgs / DiffArity）
  - Interface × 4（SameNameSameArgs / NonGeneric / DiffArgs / DiffTypeParams）
  - FuncType × 4（Same / DiffArity / DiffParam / DiffRet）
  - HashSet 跨三 record 一组
  - D2b 集成 IsAssignableTo（interface 同结构应可赋值）

## 阶段 3: 验证 + 文档 + 归档
- [x] 3.1 `dotnet build src/compiler/z42.slnx` ✅
- [x] 3.2 `cargo build`（隐式通过 test-vm.sh）
- [x] 3.3 `dotnet test`：944/944 ✅（基线 +15）
- [x] 3.4 `./scripts/test-vm.sh`：230/230 ✅
- [x] 3.5 `./scripts/build-stdlib.sh` 6/6 ✅
- [x] 3.6 spec scenarios 逐条核对：12 scenarios 全部由测试覆盖
- [x] 3.7 文档同步：
  - `docs/design/compiler-architecture.md` "Pratt 表达式解析" 后加 §Z42Type record 结构 equality
  - `docs/roadmap.md` 历史表加 2026-05-03 fix-z42type-structural-equality 行
- [x] 3.8 移动 `spec/changes/fix-z42type-structural-equality/` → `spec/archive/2026-05-03-fix-z42type-structural-equality/`
- [x] 3.9 commit + push

## 备注
- Spec 实施前 Scope 扩展：原仅 Z42InstantiatedType；调研发现 D2b 实际触发的是 Z42InterfaceType
  （`interface ISubscription<TD>` 实例化为 Z42InterfaceType），根因是 record IReadOnlyList 引用比较的共性 bug，扩展到 3 个 record 一次修齐
- 不引入 intern table（Decision 1）
- 不动 TypeChecker.Generics.cs `TypeArgEquals` / IsAssignableTo workaround 分支（Decision 4 / 5）—— 留独立 cleanup spec 评估 dead-code
- 不深比字典字段 Methods/StaticMembers（Decision 3）—— 同名 interface 实践共享字典对象
- D2b 解封需要 Spec 3 (member-substitution) 也 GREEN
