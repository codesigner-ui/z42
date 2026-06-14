# Design: Type.GetInterfaces()

## Architecture

```
绑定期接口名 (IrClassDesc.Interfaces，已存在)
   │  codegen IrGen.Classes.cs:94  b.Interfaces.Select(i=>i.Name)
   ▼
zbc TYPE section 接口块 (NEW wire: u16 count + u32[] name idx)   ← 本变更
   │  C# ZbcWriter.BuildTypeSection 写 / ZbcReader.ReadTypeSection 读回 (round-trip)
   ▼
Rust read_type → ClassDesc.interfaces → TypeDescCold.interfaces (cold, 反射专用)
   │
   ▼
__type_interfaces builtin: base-walk(td.base_name 链) + 按名 dedup
   │  每个接口名 → make_type_from_name → Std.Type (name-only)
   ▼
Std.Type.GetInterfaces() : Type[]
```

## Decisions

### Decision 1: 接口块放 TYPE section 类记录尾部（静态字段块之后）

**问题：** 接口名持久化到哪。
**选项：** A — TYPE section 类记录尾追加接口块（紧随静态字段块）；B — 新独立 section。
**决定：** 选 A。与 attr-refs / flags / static-fields 同址同模式（每类一段，count 恒写=0 表无接口，保 per-class 布局统一），reader 顺序解析无歧义。新 section 是过度设计。

### Decision 2: GetInterfaces() 含继承接口（base 链聚合，运行期 base-walk）

**问题：** 只返回类自身声明的接口，还是含基类接口？
**选项：** A — 仅自身声明；B — 自身 + 继承（base 链）。
**决定：** 选 B，镜像 C# `GetInterfaces()`（默认含继承）+ 复用 inherited-static-fields 已验证的运行期 base-walk 模式：wire 只持久化**每类自身声明**的接口（最小格式成本），`__type_interfaces` 运行期沿 `td.base_name` 链聚合各祖先类自身接口，**按名 dedup**（同接口被 base+derived 都实现只出现一次）。零额外格式成本拿到 C# 语义。

### Decision 3: 接口的传递实现（interface-extends-interface）延后

**问题：** `C : IList`，而 `IList : ICollection` → GetInterfaces() 是否含 ICollection？
**决定：** 延后。z42 当前无接口类型的 TYPE 条目（接口不产 ClassDesc），故拿不到"接口继承图"。传递闭包需先持久化接口的 base-interface 列表（另一格式扩展）。本变更只做"类直接声明 + 类继承链聚合"。入 Deferred `reflection-future-transitive-interfaces`。

### Decision 4: 返回的接口 Type 是 name-only

**问题：** GetInterfaces() 返回的 `Type` 元素是否带可枚举成员的句柄。
**决定：** name-only（与 `typeof(IFoo)` 现状一致）。接口不产 TYPE 条目 → `make_type_from_name` 对接口名建 name-only Std.Type（`Name`/`FullName` 正确，`GetMethods()` 空）。这与现有 typeof(接口) 行为对齐，不引入新不一致。

## Implementation Notes

- **Wire 布局**（TYPE section 每类记录尾，static-fields 块之后）：
  `interface_count: u16` + `interface_name_idx[]: u32`。Count 恒写。
- **C# 双向**：BuildTypeSection 写（+ intern 预扫 `foreach iface: pool.Intern(iface)`，
  在 InternPoolStrings 的 per-class 段，紧随 static-fields intern）；ReadTypeSection 读 →
  `IrClassDesc.Interfaces`（List<string>）。ReadWriteRoundTrip CI 要求双向 parity。
- **Rust ClassDesc**：加 `interfaces: Box<[String]>`；read_type 在 static-fields 块后读
  `iface_count: u16` + names。
- **TypeDescCold**：加 `interfaces: Box<[Box<str>]>`（cold——无接口的类不付成本，与 static_fields 同 cold 段）。load 路径（ClassDesc→TypeDesc）透传。
- **`__type_interfaces`**：读接收者 Std.Type 的 `__qualified`/`__fullName` → `try_lookup_type` →
  沿 `base_name` 链收集各 `td.interfaces()`，`BTreeSet`/线性 dedup 按名（保声明序优先则用 Vec+seen-set），
  每名 `make_type_from_name(ctx, name)` → Value array（`alloc_array_typed("Std.Type", ...)`）。
- **base-walk 镜像** `builtin_type_fields` 的 `try_lookup_type` 跨 zpkg 懒加载路径。
- **dedup 序**：most-derived first（derived 先于 base），保证声明序稳定 + 跨 OS 确定（不依赖 HashSet 迭代序，见 common-pitfalls §1）。

## Testing Strategy

- 单元测试：`zbc_reader_tests.rs` version-pin 17/19。
- Golden e2e：`src/tests/types/get_interfaces.z42` —— 单接口/多接口/继承接口/无接口/
  `obj.GetType().GetInterfaces()` 一致性（assert-only，interp+jit）。
- dotnet GoldenTests：TYPE section round-trip（ReadWriteRoundTrip）+ 全反射回归。
- 格式 fixtures：zbc-format + zpkg-format regen（接口块出现在带接口的 fixture；多数 fixture 无接口 → 仅版本字节 + count=0 字节）。
- VM 验证：xtask test vm / cross-zpkg / stdlib。

## Deferred

- `reflection-future-transitive-interfaces`：接口的传递实现闭包（需接口 base-interface 图持久化）。
- `reflection-future-get-interface-byname`：`Type.GetInterface(string)` / `IsAssignableFrom`。
- z42c writer 接口块镜像（z42c 锁/WIP 协调后 follow-up）。
