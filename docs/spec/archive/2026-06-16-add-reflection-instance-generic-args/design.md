# Design: 实例泛型 args

## Architecture

```
new Box<int>()  ──ObjNew──► ScriptObject{ type_desc=Box, type_args=["int"] }
                                    │ obj.GetType()  (__obj_get_type)
                                    ▼
                            type_args 非空？
                              ├─ 是 → make_constructed_type(ctx, td.name, type_args)
                              │        → Box 句柄 + __typeArgs=[typeof(int)] 槽
                              │        → GetGenericArguments() 返 [int]（读槽，#1）
                              └─ 否 → make_type_object(td)  （非泛型，不变）
```

复用 #1（add-reflection-generic-type-definition）的 `make_constructed_type` —— 它解析 base 名得
定义句柄、把 args 解析为 `Std.Type[]` 挂 `__typeArgs` 槽。typeof 侧与实例侧由此走同一构造型表示。

## Decisions

### Decision 1: 复用 make_constructed_type（typeof 与实例同构）

**决定**：`__obj_get_type` 对泛型实例调 `make_constructed_type(ctx, &td.name, &type_args)`，与
`typeof(Box<int>)` 产出**完全相同**的构造型 Type。好处：`obj.GetType()` 与 `typeof` 对同一构造泛型
的反射结果一致（GetGenericArguments / IsGenericTypeDefinition 行为统一），零新机制。

### Decision 2: 纯运行期，无格式 bump

`ScriptObject.type_args` 已由 ObjNew 写入（add-default-generic-typeparam），`GetGenericArguments()`
已读 `__typeArgs` 槽（#1）。本变更只接通 `__obj_get_type` 一处，无 wire / IR / stdlib 改动。

## Implementation Notes

```rust
Some(Value::Object(rc)) => {
    let td = rc.type_desc_arc().clone();
    let type_args = rc.type_args();          // &[String]，lockless 访问器
    if type_args.is_empty() {
        Ok(make_type_object(ctx, td))        // 非泛型，不变
    } else {
        let args: Vec<String> = type_args.to_vec();   // 脱离 borrow，make_constructed_type 会 alloc
        Ok(crate::corelib::reflection::make_constructed_type(ctx, &td.name, &args))
    }
}
```

- `td.name` 是 FQ 名（registry key）→ make_constructed_type 内 make_type_from_name 命中同一 td → 真句柄。
- `type_args` 先 `to_vec()` 脱离 GcRef borrow，避免 make_constructed_type 内 alloc 时的别名问题。

## Testing Strategy

- **golden（interp+jit）**：`instance_generic_args.z42` ——
  - `Box<int> b = new Box<int>(); Type t = b.GetType();` → `t.GetGenericArguments()` = `[int]`、
    `t.IsGenericType` true、`t.IsGenericTypeDefinition` false；
  - `t.GetGenericTypeDefinition().GetGenericArguments()` 空（定义型）；
  - 非泛型实例 `new Plain()` GetType().GetGenericArguments() 空（回归不变）；
  - 与 `typeof(Box<int>)` 一致：`b.GetType().GetGenericArguments()[0].Name == typeof(Box<int>).GetGenericArguments()[0].Name`。
- **GREEN**：cargo + dotnet GoldenTests + xtask vm/cross-zpkg/stdlib（无 bump，无 version dance）。
