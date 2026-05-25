# Design: add-gc-oom-exception

## Architecture

```
alloc_object / alloc_array  →  Value::Null (strict OOM)
        │
        ▼  (检测在各 interp callsite)
make_stdlib_exception(ctx, module, "Std.OutOfMemoryException", msg)
        │                   │
        │ Ok(exc_obj)        │ Err / exc_obj == Null  (双重 OOM)
        ▼                   ▼
  Ok(Some(exc_obj))    Ok(Some(Value::Null))   ← best-effort fallback
        │
        ▼
  propagate up exec_instr → catch handler 或 uncaught
```

## Decisions

### Decision 1: 检测位置 — callsite，不改 heap
**问题**：OOM 检测放在 heap 内部（`alloc_object` 直接 throw）还是 interp callsite？

**heap 内部**：需要 heap 知道 `VmContext` + `Module`，破坏分层；heap 目前
只持有 `GcEvent` 回调，无 ctx 引用。

**callsite**：exec_object / exec_array / exec_call 都有 `ctx` + `module`。
`alloc_object` 返 Null = 信号，现有设计。

**决定**：callsite 检测。heap API 不变。

### Decision 2: 双重 OOM fallback — 临时禁用 strict OOM
**问题**：`make_stdlib_exception` 内部也调 `alloc_object`，strict OOM 下 exception
对象 alloc 也会返 Null，导致 throw `Value::Null`，catch 无法匹配。

**决定**：每个 OOM handler 在调用 `make_stdlib_exception` 前临时设
`set_strict_oom(false)`，构造完后 `set_strict_oom(true)`。因为 alloc_object
仅在 strict OOM 下返 Null，反向推断严格 OOM 当前必然开启，重新开启是安全的。
Exception 对象本身很小，略微超出 `max_heap_bytes` 是可接受的 best-effort。
如连 exception 构造也 OOM（理论上不可能在临时禁用期间），fallback 为 `Value::Null`。

### Decision 3: exec_array 返回类型扩展
`array_new` / `array_new_lit` 当前返 `Result<()>`，需改为 `Result<Option<Value>>`
与 `obj_new` 对齐。同时更新 `exec_instr.rs` caller 处理 `Ok(Some(exc))`。

### Decision 4: exec_call mk_clos unreachable! 修复
```rust
// 当前（panic 风险）：
let env = match env_val {
    Value::Array(rc) => rc,
    _ => unreachable!("alloc_array must return Value::Array"),
};

// 改后：
let env = match env_val {
    Value::Array(rc) => rc,
    Value::Null => {
        // strict OOM: build + propagate OOM exception
        let exc = ...;
        return Ok(Some(exc));
    }
    _ => bail!("mk_clos: alloc_array returned unexpected {:?}", env_val),
};
```
`mk_clos` 返回类型从 `Result<()>` → `Result<Option<Value>>`；
`exec_instr.rs::MkClos` caller 同步更新。

### Decision 5: JIT 路径 — 延后
JIT helpers 不经过 exec_object/exec_array，JIT-mode OOM 仍抛 anyhow 错误
（与当前行为一致）。记入 gc.md Deferred 段。

## Testing Strategy
- 单元测试：Rust-level `#[test]` 直接操作 heap strict OOM + interp exec
- Golden test：`src/tests/gc/gc_oom_exception/` end-to-end z42 脚本
  验证 catch + message 内容
