# Design: IsAssignableFrom / GetInterface + FQ 接口身份

## Architecture

```
编译期                            wire (.zbc 1.20)            运行期
──────────────────────────       ──────────────────          ──────────────────────────────────
EmitClassDesc: 接口块
  InterfaceTypeName(t)
  → QualifyClassName     ───────► 接口块: FQ 名               read_type → TypeDesc.interfaces (FQ)
    "Demo.IShape"                 (此前 bare "IShape")              │
                                                                   ├─ GetInterfaces() → make_type_from_name(FQ)
                                                                   │    → 真接口句柄（此前 name-only）
                                                                   ├─ is_subclass_or_eq_td 扩展：
                                                                   │    base 链每层比较 interfaces() FQ 名
                                                                   │    → x is IShape / as IShape 生效
                                                                   └─ __type_is_assignable_from(this, c)
                                                                        = is_subclass_or_eq_td(c.name, this.name)
                                                                        （类链 + 接口都 robust）
```

核心：**类型身份比较全部落到真实 `TypeDesc` 的 FQ 名**（`type_registry` 的规范键），不再比较
合成 `Type` 对象的字符串。接口块改存 FQ 名后，接口身份与类身份同等可靠。

## Decisions

### Decision 1: 接口块存 FQ 名（根因修，zbc 1.20）

**问题**：接口身份的脆弱性根源？

**决定**：`InterfaceTypeName` 此前返 bare 名（`NamedType.Name`），改为 `QualifyClassName` 产 FQ
名写入接口块。这是**根因**——bare 名让接口身份在跨命名空间同名时不可区分。FQ 名后：
- 比较 robust（FQ 名是 `type_registry` 规范键）。
- `GetInterfaces()` 的 `make_type_from_name(FQ)` 命中接口 TypeDesc → **真句柄**（IsInterface/Name/…可用）。

结构不变（仍 `u16 count + str idx[]`），仅字段语义 bare→FQ → 按 version-bumping 仍需 minor bump。

### Decision 2: 复用 `is_subclass_or_eq_td`（VM 权威判定，非字符串 hack）

**问题**：IsAssignableFrom 怎么比？

**决定**：`a.IsAssignableFrom(b)` = `is_subclass_or_eq_td(ctx, registry, b.name, a.name)`——即"b 是不是
a 或 a 的子类型"。这是 `x is`/`as` 用的同一函数，比较真实 TypeDesc 的 FQ 名（`b` 的 base 链 + 各层
接口）。**不碰合成 Type 的字符串**。方向对齐 C#：接收者 `a` 是更宽的类型。

### Decision 3: `is_subclass_or_eq_td` 扩展查接口（修接口 `is`）

**问题**：`is_subclass_or_eq_td` 只走 base_name 类链，`circle is IShape` 现返 false。

**决定**：base 链每层额外比较该 TypeDesc 声明的接口（`interfaces()`，现为 FQ 名）。命中即 true。
这让 `is`/`as`/`IsAssignableFrom` 对接口一致生效。**传递接口**（接口继承接口）仍不覆盖（接口不存
其 base-interface），延后。

### Decision 4: GetInterface 纯 z42（输入即名字）

`GetInterface(string name)` 的输入本就是名字，按名匹配是其契约（非类型身份 hack）。纯 z42 遍历
`GetInterfaces()`（现返真句柄），匹配 `t.Name == name`。与 `GetAttribute` 同款 z42 风格。

## Implementation Notes

- **is_subclass_or_eq_td**（dispatch.rs）：
  ```rust
  loop {
      if cur == target { return true; }
      let td = registry.get(cur).cloned().or_else(|| ctx.try_lookup_type(cur));
      if let Some(ref t) = td {
          if t.interfaces().iter().any(|i| &**i == target) { return true; }  // NEW
      }
      match td.and_then(|t| t.base_name.clone()) { Some(b) => cur = b, None => return false }
  }
  ```
- **builtin_type_is_assignable_from**（reflection.rs）：args[0]=this(目标), args[1]=c(源)。
  c 为 null → false。两者皆有句柄 → `is_subclass_or_eq_td(registry, &c_td.name, &this_td.name)`。
  否则（handle-less 基元/数组）→ FullName 相等回退。
- **GetInterface / IsAssignableFrom**（Type.z42）：前者纯 z42；后者 `[Native("__type_is_assignable_from")]`。
- **GetInterfaces 改善自动获得**：`__type_interfaces` 已对每个存储名 `make_type_from_name`；名变 FQ
  后自动解析到真句柄，无需改 builtin。base-walk dedup 改按 FQ 名（天然）。

## Testing Strategy

- **golden（interp+jit）**：`assignable_from.z42` ——
  - `GetInterface("IShape")` 命中 / `"INope"` 返 null；
  - `typeof(Base).IsAssignableFrom(typeof(Derived))` true / 反向 false / 自反 true / null false / 无关 false；
  - `typeof(IShape).IsAssignableFrom(typeof(Circle))` true（接口实现）；
  - **接口 `is` 生效**：`Circle c = ...; (c is IShape)` true；
  - `GetInterfaces()[0]` 现为真句柄：`.IsInterface == true`、`.FullName == "<ns>.IShape"`。
- **回归**：全量 dotnet GoldenTests（接口 `is` 行为变化不破坏既有 `is`/`as`/继承）+ ZbcReader round-trip。
- **GREEN**：dotnet + cargo + xtask vm/cross-zpkg/stdlib（regen 后）。`xtask test compiler-z42` 暂红（z42c 延后）。
