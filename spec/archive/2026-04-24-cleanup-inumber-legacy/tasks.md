# Tasks: 清理 INumber 实例方法遗留代码 + 过时文档

> 状态：🟢 已完成 | 创建：2026-04-24 | 完成：2026-04-24

**变更类型**：refactor（>3 文件 → 最小化模式）
**变更说明**：静态抽象接口成员完整落地后，泛型 `a + b` on `T: INumber<T>`
已走 `StaticMembers` 路径。之前为 INumber 实例方法形式留的兼容代码和
旧设计文档小节变为冗余，本次清理。
**原因**：减少概念重复（instance vs static abstract 双路径）+ 消除
stale 文档，未来 Claude 读代码/文档时不再面对两套机制的选择困惑。

**文档影响**：
- `docs/design/generics.md` — 废弃旧 "INumber 数值约束（iter 1）" 小节
- `docs/design/static-abstract-interface.md` — 标记 §7.2 完成

## 变更清单

- [x] 1.1 `TypeChecker.Exprs.cs`：`TryLookupInstanceOperator` 的
      `Z42GenericParamType` 分支删除 Path A（1 参 `iface.Methods` 查询）；
      保留 Path B（2 参 `iface.StaticMembers`）。**保留** `Z42ClassType` /
      `Z42InstantiatedType` 分支的 1 参实例查询（非 INumber 专用，用户类
      仍可自定义实例 op_Add）
- [x] 1.2 `TypeChecker.Exprs.cs`：`TryBindOperatorCall` 方法文档注释更新
      为 "静态 + 静态抽象接口派发" 两条路径（去掉 instance 路径描述）
- [x] 1.3 `TypeChecker.Exprs.cs`：`SubstituteGenericReturnType` 注释简化
      （不再提 "instance `a.op_Add(b)` with ret T"）
- [x] 1.4 `docs/design/generics.md` §"INumber 数值约束（iter 1）" 加
      DEPRECATED 注记，指向 "静态抽象接口成员" 节；保留文字作为历史
- [x] 1.5 `docs/design/generics.md` §"Operator 重载" 的 Desugar 优先级
      说明更新（去掉 "instance op_Add 兜底"）
- [x] 1.6 `docs/design/static-abstract-interface.md` §7.2 "移除
      TryLookupInstanceOperator 里的 Z42GenericParamType 实例方法查询"
      标记 ✅ 完成
- [x] 1.7 `TypeCheckerTests.cs` 行 307-309 stale 注释删除（end-to-end
      demo 已在 golden test 89 落地）
- [x] 1.8 验证：dotnet build + dotnet test + cargo build + test-vm.sh 全绿
- [x] 1.9 归档 + commit
