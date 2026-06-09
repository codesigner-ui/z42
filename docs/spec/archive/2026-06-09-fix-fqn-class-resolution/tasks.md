# Tasks: 修限定名→类解析（fix-fqn-class-resolution）

**变更说明：** 限定名（`Std.Type`）作为类型注解被 `SymbolTable.ResolveType` 误落到 `Z42PrimType` fallback（`Classes` 字典按短名键控，FQN 查不到）；其上的成员访问命中 `TypeChecker.Exprs.Members.cs:174` 的静默 `BoundMember(Unknown)` → 运行期 null 字段读，属性 getter 永不派发。
**原因：** C2（typeof）+ C3b（MethodInfo.GetAttribute）共因；两处都用「短名 / 委托到定义文件」绕过。根因在核心 `ResolveType`。
**文档影响：** `attributes.md` Deferred `attribute-future-fqn-class-resolution` → 标记已修；C2/C3b 归档备注可加「根因已修」交叉引用（可选）。

> 状态：🟢 已完成。类型：`fix` → minimal mode。占用 `compiler` + `stdlib`。

## 实施

- [x] 1.1 **修正定位**：`Std.Type` 解析为 `MemberType`（非 NamedType），走 `ResolveMemberType` → 原只查嵌套 delegate → 返 Unknown。修：`ResolveMemberType` 先 `FlattenMemberType` 拍平 dotted-path + `ResolveQualifiedType`（namespace-aware：短名是已知类 **且** `ImportedClassNamespaces[short] == FQN 前缀`；命中返真类型，否则保持原行为，零回归）
- [~] 1.2 **延后**（follow-up）：未解析 FQN 现为 `Z42Type.Unknown`（非 dotted-prim），在 Members.cs:174 报错有 error-recovery 级联风险；本次不改，记 attributes.md 残留小项
- [x] 1.3 验证用例：`methods.z42` golden 即精确复现（MethodInfo.GetAttribute 用 `Std.Type` FQN）—— 无修复则失败，有修复则过
- [x] 1.4 移除 workaround（自然代码验证）：`MethodInfo.GetAttribute` 复原自然 inline；`Type.GetAttribute` 复原 inline + 删 `Type.FindByType`；`MethodInfo.z42` 去 `using Std`。**C2 binder 短名 `Type` 无需改**（本就能解析，非 bug workaround，保持低风险）
- [x] 1.5 `dotnet build` 0 error
- [x] 1.6 stdlib workspace 重建 + flat view 同步
- [x] 1.7 `dotnet test` GoldenTests **1545/1545**（含 methods.z42 自然 FQN 路径）
- [x] 1.8 docs（attributes.md Deferred 标记已修）+ 归档 + ACTIVE.md 释放 + commit + push

## 备注
- **零回归**：1.1 只在「dotted FQN + 短名是已知类 + namespace 匹配」时改变行为；其余不变。1545 全绿确认。
- **港口协调**：触及核心 `ResolveMemberType`（自举 port 镜像区），归档备注提醒 port 同步。
