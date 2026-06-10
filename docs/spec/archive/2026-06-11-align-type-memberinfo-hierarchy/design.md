# Design: 对齐 Std.Type : Std.Reflection.MemberInfo

## Architecture

纯 stdlib + runtime 改动，零格式 bump。`Std.Type` 获得 `MemberInfo` 基类 → 继承 `Name` 字段；runtime 构造 Type 实例时填充该字段。

```
Type.z42         sealed class Type : MemberInfo   (短名基类，全局 _classes 解析)
  │ 移除 [Native] Name getter（与继承字段冲突）
  ▼
z42.core.zpkg    Std.Type 的 TypeDesc.base_name = "Std.Reflection.MemberInfo"
  │              fields = [Name(继承), __name, __fullName]（cross-zpkg fixup 自动合并）
  ▼
reflection.rs    build_type(): 写 field_index["Name"] = simple（新增）+ __name/__fullName（保留）
  │              移除 builtin_type_name（__type_name 死）
  ▼
用户             typeof(C).Name → 继承字段；typeof(C) is MemberInfo → true
```

## Decisions

### Decision 1: Name 机制调和 —— 移 getter、继承字段、保留 __name
**问题**：Type 现有 `[Native] Name { get; }`（读 `__name`）；MemberInfo 有 `Name` 字段。`Type : MemberInfo` 同时拥有同名 getter + 继承字段 → 冲突。
**选项**：A 移除 getter、用继承字段（build_type 填 Name 槽）；B 保留 getter、override 字段（z42 无字段 override 概念）。
**决定**：**A**。移除 Type 的 `Name` getter；`Name` 解析到继承字段；`build_type` 新增写 `Name` 槽。`__name`/`__fullName` 字段**保留**（低层 golden `array_get_type.z42` / z42.test 直接读 `t.__name`，删除会破坏——OOS）。`__type_name` builtin 死 → 移除（含 mod.rs 注销）。`FullName` 保留 native getter（MemberInfo 无 FullName）。

### Decision 2: 短名基类，不引入跨命名空间限定基类
**问题**：`Type`(在 `Std`) 继承 `MemberInfo`(在 `Std.Reflection`)。dotted 形式 `: Std.Reflection.MemberInfo` 不被 parser 支持（`ExtractTypeName` 不处理 `MemberType`）。
**决定**：用**短名** `: MemberInfo`。base 解析是全局短名 `_classes.TryGetValue`（SymbolCollector.Classes.cs:370，已验证），`MemberInfo` 作 z42.core 同包类在 `_classes` 中 → 直接命中。**无需编译器改动**，亦不需 `using Std.Reflection`（base 解析不经 using）。若 Type.z42 其他位置需引用 Std.Reflection 类型再按需加 using。

### Decision 3: `Type.BaseType` 语义不变
`Type.BaseType`（`[Native] BaseType { get; }`）反射的是**被反射用户类型**的基类（经 native handle），与 `Type`-类自身的 `MemberInfo` 基类**无关**。本 change 不改 `BaseType` 语义：`typeof(Circle).BaseType` 仍返 Circle 的基类（Object）。Type-类自身 `: MemberInfo` 只影响 `typeof(Circle) is MemberInfo`。

## Implementation Notes

- **字段顺序 / slot 偏移**：`Name`(继承) 入 slot 0，`__name`/`__fullName` 后移。`build_type` 用 `field_index.get(name)` 按名查 slot（已是名查找，偏移无关）→ 仅需加一行写 `Name`。
- **sealed + 基类**：`sealed class Type : MemberInfo` —— sealed 限制 Type 不能被继承，不影响 Type 有基类。实施期插桩确认 parser/typecheck 接受（Open Question）。
- **builtin 移除**：`builtin_type_name` 删除前 grep 确认无其他调用方（仅 `__type_name` getter 用）。
- **强制重编 z42.core**：层级改动后 stdlib 必须重编（regen dist）。⚠️ 注意 pre-existing 多文件 project-build 命名空间双重限定 bug（见 memory）——但本 change 不引入新跨文件限定调用，z42.core 重编现已验证可用（param-attr change 的 0.17 regen 22/22 + dotnet 1556）。

## Testing Strategy

- **golden e2e** `src/tests/types/type_is_memberinfo.z42`：`typeof(C) is MemberInfo` 为真；`MemberInfo m = typeof(C); m.Name` == 简单名；`typeof(C).Name` 仍正确（经继承字段）。
- **stdlib [Test]** reflection.z42：加 `is MemberInfo` + 经基类读 Name 断言。
- **回归**：所有现有 Type.Name / __name 读取（array_get_type / object_get_type / get_properties / 各反射测试）dotnet GoldenTests 全绿。
- **GREEN**：dotnet GoldenTests（权威门）+ cargo --lib（含 reflection builtin）。xtask gate 受 pre-existing merge bug 阻塞 → dotnet 替代。
