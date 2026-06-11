# Tasks: add-reflection-inherited-static-fields

> 状态：🟢 已完成 | 创建：2026-06-11 | 完成：2026-06-11 | 类型：feat（runtime-only，无格式 bump）

**变更说明：** `Type.GetFields()` 现含继承的静态字段（沿 base 链聚合祖先类静态字段），对齐 C# 默认含继承公共静态。
**原因：** 静态字段 per-声明类存储、不走实例字段的 cross-zpkg base-merge fixup，故 MVP 只返声明类自身静态。运行期 base-walk 补齐继承。
**文档影响：** `docs/design/language/reflection.md`（`reflection-future-inherited-static-fields` Deferred → 落地）。

## 实施
- [x] 1.1 `src/runtime/src/corelib/reflection.rs` `builtin_type_fields`：静态字段循环改为沿 `TypeDesc.base_name` base-walk 聚合（most-derived 优先 + 按名 dedup；`FieldInfo.__qualified` 用声明类名；`type_registry` → `try_lookup_type` 跨 zpkg）
- [x] 1.2 golden e2e `src/tests/types/inherited_static_fields.z42`（Derived:Base，继承静态 baseStat + 继承实例 baseInst + own）
- [x] 1.3 docs/design/language/reflection.md 同步（Deferred 标记落地 + static-fields 剩余项更新）

## 验证
- [x] 2.1 dotnet GoldenTests **1559/1559**（新 inherited_static_fields + 回归 static_fields_reflect/get_properties 等全绿——Account/Config 无带静态的用户基类故计数不变）
- [x] 2.2 cargo test --lib **764 + 21**
- 无格式 bump（纯运行期 base-walk，读已加载的 TypeDesc.static_fields）。
