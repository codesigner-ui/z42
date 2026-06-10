# Design: 字段级用户 attribute 反射（zbc 1.14）

## Architecture

```
[Attr] field (parser 累积 pendingUserAttrs → FieldDecl.Attributes)
        │ AttributeFactorySynthesizer.ProcessClass（遍历字段合成工厂）
        ▼
IrFieldDesc.Attributes  ──ZbcWriter──►  TYPE section 字段记录 + attr refs
        │ read_type
        ▼
FieldDesc.attributes  ──build_type_registry──►  TypeDescCold.field_attributes
                                                  (field_name → attr refs)
        │                                              ▲
  FieldInfo.__qualified ──► __field_custom_attributes ─┘ → call factories → 活实例
```

## Decisions

### Decision 1: 复用 factory-thunk（同 C3a/C3b）
合成无参工厂 `() => new Foo(args)`，wire 记 (attr-type 名, factory 名)。运行期 `call_attribute_factories`（C3a/C3b 已有）调工厂 + 缓存。key `fld$<Class>$<Field>`。零新机制。

### Decision 2: 字段 attr refs 进字段记录（实例块 + 静态块都加）
每个字段记录在 `type_str: u32` 后追加 `attr_count: u16` + (type_name, factory) 对，与 class/method attr 同形。实例字段块 + 静态字段块（1.13）都加——两种字段都可标 attribute。

### Decision 3: 运行期字段 attr 存 `TypeDescCold.field_attributes`（按字段名索引）
**问题**：实例字段在热路径 `TypeDesc.fields` 是 `FieldSlot`（仅 name+type_tag，无 attr）；静态字段在 `TypeDescCold.static_fields` 是 `FieldDesc`（有 attr）。如何统一让反射拿到字段 attr？
**决定**：load 时把**所有有 attr 的字段**（实例 + 静态）收进 `TypeDescCold.field_attributes: Box<[(Box<str>, Box<[AttributeRef]>)]>`（field_name → refs，保序线性，字段数少）。热路径 `fields`(FieldSlot) 不动（不污染实例布局）。无字段 attr 的类不分配（空）。
**为什么不放 FieldSlot**：FieldSlot 是热路径实例布局，加 attr 列表会膨胀每对象字段访问的 cache footprint；反射是冷路径，放 cold 一致。

### Decision 4: FieldInfo 携 `__qualified` 供 builtin 解析（镜像 MethodInfo）
`build_field_info` 给每个 FieldInfo 写隐藏 `__qualified = "<Class>.<Field>"`。`FieldInfo.GetCustomAttributes()` 调 `__field_custom_attributes(__qualified)`：builtin 按最后一个 `.` 拆出 class + field，`try_lookup_type(class)` 拿 TypeDesc，在 `cold.field_attributes` 找 field → 调工厂。z42 层缓存到 `__attrCache`。

## Implementation Notes

- **build_field_info**：`builtin_type_fields` 现写 (Name, FieldType, IsStatic)；加 (`__qualified`)。需知道 owning class 名 —— `builtin_type_fields` 已有 `td.name`，拼 `<td.name>.<field.name>`。
- **field_attributes 填充**：`build_type_registry` 从 `desc.fields` + `desc.static_fields` 收集 `attributes` 非空者 → cold.field_attributes。cold 判定加 `!field_attributes.is_empty()`。
- **builtin_field_custom_attributes(qualified)**：拆 class/field → `ctx.module().type_registry` 或 `try_lookup_type` 拿 TypeDesc → 线性找 field → `call_attribute_factories`（已有）。空/未找到 → 空数组（lenient）。
- **wire intern**：`InternPoolStrings` 字段循环（实例 + 静态）加 `InternAttributeRefs(pool, fld.Attributes)`（复用 C3a helper）——否则 `pool.Idx` KeyNotFound（同 static-fields 踩的坑）。
- **ZbcReader/read_type**：字段读 `type_str` 后续读 `attr_count` + refs（实例块 + 静态块对称）。

## Testing Strategy

- **Golden** `src/tests/attributes/field_attrs.z42`：类含带 attr 的实例字段 + 静态字段 + 无 attr 字段，`GetFields()` → 各 FieldInfo `GetCustomAttributes()` / `GetAttribute(typeof(X))` 校实例值 + 缓存 + 空。局部接收者写法。
- **Dogfood [Test]** `reflection.z42`：同上经 runner。
- **Rust 单测**：lenient（非法 qualified → 空）+ 字段 attr 解码（构造带 cold.field_attributes 的 TypeDesc，验 builtin 解析）。
- **Fixture + stdlib regen**；版本 bump checklist 全过。
- **GREEN**：dotnet test（含新 golden + Format invariant）+ cargo test --lib；driver-direct 重建 stdlib + embedding_hello 重生（强 build.rs rerun）。以 C# GoldenTests 为权威门（xtask 僵尸 jam）。
