# Design: Type.GetProperties()

## Architecture

```
z42.core stdlib                         Rust VM (runtime)
─────────────                           ─────────────────
Std.Reflection.PropertyInfo  ◄── eager fill ── build_property_info()
  : MemberInfo                                       ▲
  { PropertyType, CanRead, CanWrite }                │ 派生
Std.Type.GetProperties()  ──[Native]──►  builtin_type_properties()
  : PropertyInfo[]                          扫 TypeDesc.vtable + cold.own_methods
                                            按 get_<X>/set_<X> 配对
```

完全镜像现有 `Type.GetMethods()` → `builtin_type_methods()` → `build_method_info()` 链；唯一新增是"方法名约定 → 属性配对"那一步派生逻辑。

## Decisions

### Decision 1: 属性从 get_/set_ 方法约定派生，而非持久化 PropertyDesc 元数据
**问题：** auto-property 降解为 field + `get_<X>`/`set_<X>` 方法，无独立 PropertyDesc 元数据。
**选项：** A — 编译器 emit PropertyDesc 进 zbc TYPE section（新 wire 字段）；B — 运行期从 `get_<X>`/`set_<X>` 方法名约定派生。
**决定：** 选 B。A 要 zbc 格式 bump（1.11→1.12）→ 撞正在进行的 `port-z42c-zbc-writer` 自举移植（强制 re-port）。B 纯 runtime，零格式变更、零编译器变更、零 port 扰动。约定（`get_`/`set_` 前缀）已是编译器既定事实（Members.cs:90 的 auto-property getter dispatch 就靠它）。

### Decision 2: get/set 配对去重 → 单条 PropertyInfo
**问题：** `X { get; set; }` 产生 `get_X` + `set_X` 两个方法。
**决定：** 按属性名（去 `get_`/`set_` 前缀）聚合：同名 getter+setter 合并为一条 `CanRead && CanWrite`；只读/只写各自单条。`PropertyType` 优先取 getter 返回类型，无 getter 时取 setter 参数类型。

### Decision 3: 顺序 = 首次出现序（vtable / own_methods 既定顺序），不另排序
**问题：** 确定性（common-pitfalls §1）。
**决定：** vtable + own_methods 本身是编译器 emit 的稳定顺序（非 hash/fs 迭代），按它首次遇到 `get_`/`set_` 的顺序产出即确定。不引入额外 sort（与 GetMethods 一致）。

### Decision 4: accessor 方法仍出现在 GetMethods（不从 GetMethods 剔除）
**问题：** `get_X`/`set_X` 既是方法又支撑属性。
**决定：** 与 C# 一致——GetMethods() 仍含 `get_X`/`set_X`，GetProperties() 是叠加的属性视图。不改 GetMethods 输出（零回归 + 零 golden 漂移）。

## Implementation Notes

- `build_property_info(ctx, name, prop_type_name, can_read, can_write)`：取 `ctx.try_lookup_type("Std.Reflection.PropertyInfo")` 的 TypeDesc，按 `field_index` 名字写槽（`Name` 继承自 MemberInfo；`PropertyType` 经 `make_type_from_name` 造 Type；`CanRead`/`CanWrite` 写 bool）。模板 = `build_method_info`（reflection.rs）。
- 派生扫描：遍历 `TypeDesc.vtable`（含继承，base-first）取 simple method name；对每个 `get_<X>`（验 0 参，经 `ctx.try_lookup_function(qualified)` 拿签名）记 getter；`set_<X>`（验 1 参）记 setter。用一个保序 map（`Vec<(name, slot)>` 线性或 `IndexMap` 思路）聚合。`own_methods` 覆盖声明的非虚 accessor（与 GetMethods 同样合并 vtable + own_methods）。
- 属性类型名经现有 `make_type_from_name` 规范化（`i32→int` 等），与 `FieldType`/`ReturnType` 一致。
- handle-less Type（`NativeData::None`）→ lenient 返空数组（绝不 bail），与 `builtin_type_fields` 一致。
- PropertyType 取 getter 的返回类型 / setter 的参数类型，经 `Function.ret_type` / `cold.param_types`（已加载，无需新 wire——与 MethodInfo 签名同源）。

## Testing Strategy

- **单元（Rust）**：`reflection.rs` 既有测试模式——若有 `reflection_tests.rs` 加 `__type_properties` 用例（读写 / 只读 / 继承 / 空）。
- **Golden**：`src/tests/types/get_properties.z42` —— 类含读写属性 + 只读属性 + 继承属性，`Assert.Equal` 校 Name/PropertyType.Name/CanRead/CanWrite/Length。
- **Dogfood [Test]**：`src/libraries/z42.core/tests/reflection.z42` 追加 GetProperties [Test]（局部变量接收者写法，规避 reflection-future-chained-property-dispatch）。
- **GREEN**：`dotnet build` + `cargo build --release` + `dotnet test`（GoldenTests 全绿）+ `z42 xtask.zpkg test lib`（z42.core dogfood）。无格式 bump → 无 fixture regen。
