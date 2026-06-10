# Design: GetFields() 含静态字段（zbc 1.13）

## Architecture

```
ClassDecl.Fields (IsStatic split)         (AST, 已有)
        │ IrGen.EmitClassDesc
        ▼
IrClassDesc { Fields(实例), StaticFields(静态) }   (z42.IR)
        │ ZbcWriter — flags byte 后追加 static_field block
        ▼
zbc TYPE section ──readers──► ClassDesc { fields, static_fields }
        │ build_type_registry
        ▼
TypeDesc.fields(热)  +  TypeDescCold.static_fields(冷)
        │                              │
   builtin_type_fields: 实例(IsStatic=false) ++ 静态(IsStatic=true) → FieldInfo[]
```

## Decisions

### Decision 1: 静态字段独立列表，**不**并入实例 `fields`
**问题**：放一个 list 加 `is_static` 标志，还是两个 list？
**决定**：独立。`TypeDesc.fields` 是**实例字段热路径布局**（`field_index` / FieldGet IC 按槽序索引）——掺入静态字段会错位实例槽 index，破坏对象读写。故静态字段单独走 `ClassDesc.static_fields`（wire）+ `TypeDescCold.static_fields`（冷，反射专用）。反射时 `builtin_type_fields` 合并两者输出。

### Decision 2: 静态字段块追加在 TYPE 记录 flags 字节之后
**问题**：插哪？
**决定**：append 在 1.12 flags 字节之后（name→base→实例fields→tp→attrs→flags→**static_fields**）。读端在 flags 读完后续读 `static_field_count: u16` + 块，对前序零扰动。

### Decision 3: 静态字段进 cold box（反射专用），普通类不分配
**问题**：放 hot 还是 cold `TypeDesc`？
**决定**：`TypeDescCold.static_fields`。绝大多数类无静态字段（空）→ 不触发 cold 分配（与 own_fields 等同款 lazy）。`build_type_registry` 的 cold 判定加入 `!static_fields.is_empty()`。反射本就读 cold（own_methods 等），一致。

### Decision 4: MVP 仅声明类自身静态字段（继承静态延后）
**问题**：C# GetFields() 含继承静态。
**决定**：实例字段经 cross-zpkg fixup 已含继承；静态字段不走该 fixup（静态字段非继承存储，per 声明类）。MVP 返回**声明类自身**静态字段；继承静态反射（走 base 链聚合）记 Deferred `reflection-future-inherited-static-fields`。覆盖最常见需求（反射某类自己的静态成员），避免 fixup 复杂度。

## Implementation Notes

- **FieldInfo 槽**：`builtin_type_fields` 给每个 FieldInfo 写第三槽 `IsStatic`（bool）。实例循环写 false，静态循环写 true。`alloc_named(STD_REFLECTION_FIELDINFO, &[("Name",..),("FieldType",..),("IsStatic",..)])`。
- **wire 对称**：静态字段块与实例字段块**完全同形**（`name: u32, type_tag: u8, type_str: u32`），ZbcWriter/Reader + Rust read_type 复用实例字段的读写逻辑（可提取 helper 或直接镜像）。
- **类型名**：静态字段 `FieldType` 经 `make_type_from_name` 规范化（同实例）。
- **loader cold 判定**：`cold = (own_fields/own_methods/type_params/.../custom_attributes/static_fields 任一非空) ? Some : None`。
- **EmitClassDesc**：`StaticFields = cls.Fields.Where(f => f.IsStatic).Select(f => new IrFieldDesc(f.Name, TypeName(f.Type)))`；实例 `Fields` 维持 `Where(!IsStatic)` 不变。

## Testing Strategy

- **Golden** `src/tests/types/static_fields_reflect.z42`：类含实例 + 静态字段，`GetFields()` 校 Name/FieldType/IsStatic + count；仅静态类；继承类（验证 MVP 只含自身静态）。局部接收者写法。
- **Dogfood [Test]** `reflection.z42`：同上经 runner。
- **Rust 单测**：lenient（非 Type → 空）+ 构造带 cold static_fields 的 TypeDesc 验证 builtin 输出含静态 + IsStatic。
- **Fixture**：跑 `generate-fixtures.sh`（zbc + zpkg）regen，diff 显示 static_field block + 版本号。
- **GREEN**：dotnet build + cargo build + dotnet test（Zbc/Zpkg invariant + 新 golden）+ cargo test --lib。无 xtask 时以 C# GoldenTests 为权威门（driver-direct 重建 stdlib）。版本 bump checklist（version-bumping.md）逐项过。
