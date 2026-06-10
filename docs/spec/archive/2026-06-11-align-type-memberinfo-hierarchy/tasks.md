# Tasks: 对齐 Std.Type : Std.Reflection.MemberInfo

> 状态：🟢 已完成 | 创建：2026-06-11 | 完成：2026-06-11 | 类型：lang（层级/语义变更，完整流程）

## 验证报告（2026-06-11）

- ✅ **可行性插桩通过**：`sealed class Type : MemberInfo`（短名基类）被 parser/typecheck 接受；`_classes["MemberInfo"]` 命中、base 链建立；运行期 `typeof(C) is MemberInfo` 为真。三个 Open Question 全部证实 → 无需编译器改动、无格式 bump。
- ✅ **dotnet GoldenTests：1557/1557**（含新 `src/tests/types/type_is_memberinfo.z42`：is MemberInfo + Name via base + `MemberInfo m = t` + FullName + `__name` 兼容 + GetType 路径；回归：array_get_type / object_get_type / get_properties / 各反射读 Name 全绿——所有 Name 读取已改走继承字段）
- ✅ **cargo test --lib：759 + 21**（移除 `builtin_type_name` 后）
- ⚠️ **reflection.z42 [Test]（lib stage）** 受 pre-existing 多文件 project-build merge bug 阻塞不可跑 → 等价断言已由 type_is_memberinfo.z42 golden 经 full VM e2e 覆盖（dotnet 权威门）。
- 无 zbc/zpkg 格式 bump；无 z42c 影响（reflection 类不在 z42c 自举镜像内）。

## 进度概览
- [ ] 阶段 0: 6.5 gate 审批
- [ ] 阶段 1: 可行性插桩（sealed+base / 跨命名空间短名 base / Name 继承）
- [ ] 阶段 2: stdlib + runtime 实施
- [ ] 阶段 3: 测试 + 文档 + 验证

## 阶段 1: 可行性插桩（先于正式实施，确认 Open Questions）
- [ ] 1.1 临时令 `Type : MemberInfo` + 移 Name getter，重编 z42.core，确认：① parser/typecheck 接受 sealed+base ② `_classes["MemberInfo"]` 命中、base 链建立 ③ 无新编译错误
- [ ] 1.2 确认 `typeof(C) is MemberInfo` 运行期为真（base 链 + isinst）；若假 → 排查 TypeDesc base_name 是否 = MemberInfo

## 阶段 2: 实施
- [ ] 2.1 `Type.z42`：`sealed class Type : MemberInfo`；移除 `[Native("__type_name")] Name { get; }`；更新类注释
- [ ] 2.2 `reflection.rs`：`build_type()` 加写 `field_index["Name"]` = simple；移除 `builtin_type_name`（grep 确认无他引用）
- [ ] 2.3 `corelib/mod.rs`：注销 `__type_name`
- [ ] 2.4 重编 z42.core（regen dist 0.17；无格式 bump 故 stdlib 版本不变，仅内容重编）

## 阶段 3: 验证
- [ ] 3.1 `src/tests/types/type_is_memberinfo.z42` golden e2e（is MemberInfo + Name via base + __name 兼容）
- [ ] 3.2 `reflection.z42` 加 [Test] 断言
- [ ] 3.3 dotnet GoldenTests 全绿（回归：array_get_type / object_get_type / get_properties / 各反射读 Name）
- [ ] 3.4 cargo test --lib 全绿
- [ ] 3.5 docs/design/language/reflection.md 同步（API 表 Type:MemberInfo + Deferred 标记落地 + 层级图）
- [ ] 3.6 spec scenarios 逐条覆盖

## 备注
- 无 zbc/zpkg 格式 bump（TypeDesc 已支持 base_name + 继承字段）→ 不触发 merge-bug regen 风险面。
- xtask GREEN gate 受 pre-existing 多文件 project-build 命名空间双重限定 bug 阻塞 → 用 dotnet GoldenTests 权威门验证（见 memory）。
- 若阶段 1.1 插桩揭示 sealed+base 或跨命名空间短名 base 不被支持 → 停，回 design 评估编译器改动（扩 Scope，重新 gate）。
