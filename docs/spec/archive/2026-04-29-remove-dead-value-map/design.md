# Design: Remove Dead Value::Map

## Architecture

简单的 dead code 清理；无新组件。

```
Before:                          After:
─────────                        ─────────
Value enum                       Value enum
├── I64                          ├── I64
├── F64                          ├── F64
├── Bool                         ├── Bool
├── Char                         ├── Char
├── Str                          ├── Str
├── Null                         ├── Null
├── Array(Rc<RefCell<...>>)      ├── Array(Rc<RefCell<...>>)
├── Map(Rc<RefCell<...>>)  ❌     └── Object(Rc<RefCell<...>>)
└── Object(Rc<RefCell<...>>)

trait MagrGC                     trait MagrGC
├── alloc_object                 ├── alloc_object
├── alloc_array                  ├── alloc_array
├── alloc_map  ❌                  ├── write_barrier (default)
├── write_barrier (default)      ├── collect (default)
├── collect (default)            ├── collect_cycles (default)
├── collect_cycles (default)     └── stats
└── stats
```

## Decisions

### Decision 1: 同时清理 catch-all 兜底

**问题**：`value_to_str` 用 `_ => format!("{:?}", other)` 兜底；删除 Map 后 catch-all
仍在 → 未来加新 variant 仍会被静默兜底，重蹈覆辙。

**选项**：
- A 保留 catch-all（最小改动）
- B 转 exhaustive match（编译器强制每个 variant 显式覆盖）

**决定**：B。这是把"删除一个死 variant"的契机扩成"建立编译期防护"的小型加码。
代价仅一行，收益是未来加 variant 时无法漏 —— 直接对应 ADDED Requirement。

### Decision 2: PartialEq 也用 exhaustive

`PartialEq for Value` 当前用了 `_ => false` 兜底。删除 Map 后保留这个 catch-all
没问题（异质 variant 比较返回 false 是定义本身），但为统一风格也改 exhaustive。

实施：每对 (variant, variant) 显式列出 + 跨 variant 用 `(_, _) => false` 收尾
（这不是 catch-all，是显式的 cross-variant rule，编译器仍能在新增 variant 时
警告"new variant 没有匹配新 same-variant arm"）。

实际上更简洁的写法是保留 `_ => false` 兜底，因为新增 variant 时该 catch-all
仍是合理 default —— 加 variant 不必改 PartialEq 也不算 bug。**保留 PartialEq
catch-all**，仅 `value_to_str` 改 exhaustive（因为后者每个 variant 真的需要
特定 String 形式）。

### Decision 3: 不重命名 / 不调整其它 variant 顺序

仅做减法，不重排。`Value` 内部布局对 `bincode` 序列化不重要（Value 不直接序列化），
但保守起见维持顺序避免误伤。

## Implementation Notes

```rust
// types.rs — 删除 line 107、123 的 Map arm
pub enum Value {
    I64(i64),
    F64(f64),
    Bool(bool),
    Char(char),
    Str(String),
    Null,
    Array(Rc<RefCell<Vec<Value>>>),
    Object(Rc<RefCell<ScriptObject>>),
}

// PartialEq 保留 catch-all（见 Decision 2）

// convert.rs — value_to_str exhaustive
pub fn value_to_str(v: &Value) -> String {
    match v {
        Value::I64(n)  => n.to_string(),
        Value::F64(f)  => f.to_string(),
        Value::Bool(b) => b.to_string(),
        Value::Char(c) => c.to_string(),
        Value::Str(s)  => s.clone(),
        Value::Null    => "null".to_string(),
        Value::Array(rc) => {
            let inner: Vec<String> = rc.borrow().iter().map(value_to_str).collect();
            format!("[{}]", inner.join(", "))
        }
        Value::Object(rc) => format!("{}{{...}}", rc.borrow().type_desc.name),
    }
}
```

interp / JIT 中 `ArrayGet` / `ArraySet` / `FieldGet` 的 Map 分支直接删除：
- `ArrayGet` / `ArraySet`：原本支持 `Value::Map` 当 dict 用，删除后非 Array 直接报错
- `FieldGet`：原本对 Map 支持 `.Length`/`.Count`，删除后不再支持

`__len` builtin 同理：删除 Map 分支，错误消息从 "expected array, string, or map" 改为 "expected array or string"。

## Testing Strategy

- 编译保证（exhaustive match 在编译期捕获）
- 现有 86 个 Rust 单元测试 + 735 dotnet 测试 + 202 VM golden 测试必须 100% 通过
- **预期**：因为 `Value::Map` 从未被构造过，所有现有测试都不依赖 Map 行为，删除应零行为变化
- 如有任何 golden test 失败 → 说明 grep 漏了某处构造路径，回阶段 3 更新 Scope
