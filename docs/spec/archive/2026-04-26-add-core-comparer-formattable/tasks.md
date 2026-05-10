# Tasks: Wave 3 — IComparer / IEqualityComparer / IFormattable

> 状态：🟢 已完成 | 创建：2026-04-26 | 完成：2026-04-26 | 类型：lang (stdlib 新接口契约)

## 进度概览
- [x] 阶段 1: 3 个接口文件
- [x] 阶段 2: golden test + stdlib build (z42.core.zpkg 56746 bytes)
- [x] 阶段 3: 文档同步 + 归档

---

## 阶段 1: 接口文件

- [x] 1.1 `src/libraries/z42.core/src/IComparer.z42`
- [x] 1.2 `src/libraries/z42.core/src/IEqualityComparer.z42`
- [x] 1.3 `src/libraries/z42.core/src/IFormattable.z42`
- [x] 1.4 stdlib build 验证通过

## 阶段 2: golden test + 全量回归

- [x] 2.1 `run/100_comparer_contract`：DescIntComparer / AscIntComparer 实现 IComparer<int> + 直接具体类调用
- [x] 2.2 全部 95 golden 重生为最新格式
- [x] 2.3 GREEN：dotnet test 585/585, test-vm.sh 186/186 (interp+jit), cargo test 61/61

## 阶段 3: 文档同步 + 归档

- [x] 3.1 `src/libraries/z42.core/README.md` 核心文件表更新（IEnumerator.Current 升级形式 + 3 个新接口）
- [x] 3.2 `docs/roadmap.md` L2 stdlib Wave 3 条目
- [x] 3.3 tasks.md 状态 → `🟢 已完成`
- [ ] 3.4 归档 + commit + push（scope `feat(stdlib)`）

## 备注

实施变更：

- **IHashable 跳过**（与 IEquatable<T> 已含 GetHashCode 重叠）
- **测试简化**：原 spec Scenario 1 用 `IComparer<int> c = ...; c.Compare(...)` 经接口引用调用，
  实施时发现 z42 现有 generic interface dispatch 在 TypeChecker 不识别 TypeArgs（连
  `MyClass : IEquatable<int> → IEquatable<int>` 赋值都不通过；method args 上不
  substitute T → int）。这是 Wave 3 scope **外**的独立缺陷，留 backlog 给独立变更
  修复。
- **测试用直接具体类调用**验证接口契约可被实现 + stdlib 接口可加载；不走接口
  引用 dispatch（接口 dispatch 等独立修复后再补端到端测试）。

**Backlog 项**（独立后续变更）:
- `fix-generic-interface-dispatch`: TypeChecker 在 Z42InterfaceType method
  binding 时按 TypeArgs substitute 内部 generic params；同时让 ImplementsInterface
  检查也识别 TypeArgs 一致性。涉及 TypeChecker.Calls.cs:183 + ClassType
  IsAssignableTo to InterfaceType + Z42InterfaceType.TypeParams 字段缺失等。
