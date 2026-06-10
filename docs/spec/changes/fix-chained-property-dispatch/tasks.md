# Tasks: fix-chained-property-dispatch

> 状态：🟡 进行中 | 创建：2026-06-10 | 类型：fix（最小化模式）

**变更说明：** 链式 getter 派发 `obj.GetType().BaseType.Name` 运行期 `FieldGet on Null` 崩溃 → 修复。
**原因：** `SymbolCollector` 预注册的 Object stub 的 `GetType()` 返回类型写死 `Z42Type.Unknown`，使 `GetType()` 结果是 Unknown，链式后续成员访问（`.BaseType` / `.Name`）命中 `TypeChecker.Exprs.Members.cs:174` 静默 fallback → 当字段读 emit → 运行期崩。
**文档影响：** `docs/design/language/reflection.md`（`reflection-future-chained-property-dispatch` Deferred 条目 → 标记已落地 + 缩窄剩余）；`docs/spec/changes/ACTIVE.md`（释放 compiler 锁）。

## 根因定位（插桩验证，2026-06-10）

文档化的 "two-part 根因（P1 + P2）+ 4th layer" 经插桩**证伪一半**：

- **P2（ResolveTypeName FQN→PrimType 降级）已不存在**：`fix-fqn-class-resolution`（归档 2026-06-09，namespace-aware ResolveMemberType）已修。插桩确认 `t.BaseType.Name`（typeof 形式）**零 fallback、直接通过**；ResolveTypeName 对 `Type` 名**不**降级（仅无关的 `Object` 名降级，与本链无关）。
- **唯一剩余 = P1**：插桩确认崩溃链是 `d.GetType().BaseType.Name`，两处 fallback 的 targetType 均为 `Z42UnknownType`（源头 = GetType 返回 Unknown）。
- **"4th layer"（attempt 3 称 `_classes` 无 Type key）证伪**：插桩在 stub 构建点确认 `_classes["Type"]` **存在**（短名 key，name=Type）。attempt 3 的前提判断有误（可能查错 key / 错时机）。MergeImported（Collect L68）在 CollectClasses（L73）**之前**跑，故导入的 Type 此刻已在 `_classes`。

## 修复（root-cause, cross-phase downgrade fixup）

- [x] 1.1 `SymbolCollector.Classes.cs`：Object stub 的 `GetType()` 返回类型从写死 `Z42Type.Unknown` 改为 `_classes.TryGetValue("Type", ...)` 取真实 Type 类（仅当 Type 未导入时回落 Unknown）。符合 philosophy.md「在源头产出正确类型，不在消费端打补丁」。
- [x] 1.2 新增回归测试 `src/tests/types/chained_property.z42`：覆盖 local-receiver 控制组 + `t.BaseType.Name`（typeof 链）+ `d.GetType().BaseType.Name`（GetType 链，本 fix 目标）。
- [x] 1.3 docs：reflection.md Deferred 条目更新（已落地 + 剩余缩窄）。
- [x] 1.4 docs：ACTIVE.md 释放 compiler 锁。

## 验证

- [x] 2.1 dotnet build —— 0 error
- [x] 2.2 dotnet test（含 golden VM e2e 子进程 + 字节比对 fixture）—— 1555/1555（含新 chained_property 测试；无字节漂移，with-tidx / cross-import-token 全过 → 不撞 port byte-identical）
- [ ] 2.3 完整 GREEN gate（cargo build + vm + cross-zpkg + lib，含用新编译器重建 stdlib 验证 GetType 收紧不引入 stdlib 编译新错）
- [ ] 2.4 归档

## 备注

- byte-identical 风险评估：本 fix 仅改 typecheck 阶段 GetType 的**静态返回类型**；现有程序的 `GetType().__fullName`（字段访问）emit 路径不变（同一 FieldGet 指令，无论静态类型 Unknown vs Type）→ 字节比对 fixture 全过印证。仅链式 getter 形式（此前崩溃）行为改变。
- 副作用：`var x = obj.GetType()` 现推断 x:Type（此前 Unknown）—— 顺带改善 `reflection-future-gettype-var-inference`（待 2.3 确认 + 文档）。
