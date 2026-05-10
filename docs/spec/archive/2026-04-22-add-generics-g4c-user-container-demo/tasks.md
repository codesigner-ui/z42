# Tasks: L3-G4c User-level 泛型容器源码实现 demo

> 状态：🟢 已完成 | 创建：2026-04-22 | 完成：2026-04-22 | 类型：test+docs

**变更说明**：验证 z42 语言层面已具备完整写泛型容器的能力，纯用户代码即可
实现 `MyList<T>` 含 `T[]` 动态数组 + Add/Get/Set/RemoveAt/Count + 扩容。

**原因**：L3-G1/G2/G2.5/G3a/G4a/G4b 累积后，正面验证语言足够支撑 stdlib-级
容器（ArrayList/Stack/Queue/Hashset）的源码实现。确认 L3-G4 的剩余工作只
剩 **stdlib 导出**（L3-G4d，独立迭代）。

## 完成项

- [x] Golden `run/76_generic_list`：MyList<T> 完整实现 + int/string 实例化
- [x] stdlib `Stack.z42` / `Queue.z42` 保留参考实现为注释（待 L3-G4d 启用导出）
- [x] docs/design/generics.md：L3-G4c 小节
- [x] docs/roadmap.md：L3-G4c ✅；新增 L3-G4d（stdlib 导出）占位
- [x] dotnet test 520/520 ✅; test-vm.sh 146/146 ✅（73 interp + 73 jit）

## 备注

- **探索结论**：stdlib 导出泛型类需要 3 项配套改造（留给 L3-G4d）：
  1. SymbolCollector 对 imported generic class 的 TypeParams 传播
  2. EmitBoundNew → QualifyClassName（需区分本地/导入同名，避免冲突）
  3. VM 对 imported class instance 的 VCall 用命名空间限定名分派
- 尝试过一次激进版 QualifyClassName 改动，引发 38_self_ref_class / 74_generic_stack 名称冲突，已回滚
- 本次纯文档+测试交付，零代码改动（之前探索的 stdlib Stack/Queue 都已还原为注释）

## Scope 外发现

- 无
