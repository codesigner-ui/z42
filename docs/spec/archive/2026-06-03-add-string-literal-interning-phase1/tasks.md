# Tasks: String literal interning Phase 1 — Arc<str> pool cache

> 状态：🟢 已完成 | 创建：2026-06-03 | 归档：2026-06-03 | 类型：refactor + perf
> 来源：[`docs/review.md`](../../../review.md) Part 2 C3 / Part 5 P3

## 变更说明

当前每次 `ConstStr` 指令执行：

```rust
let s = module.string_pool.get(i).clone();   // ① String heap alloc + copy
Value::Str(s.into())                          // ② String → Arc<str> alloc
```

两次分配。stdlib 热路径里 `"Length" / "ToString" / "_zeroBytes"` 等字面量
被反复 ConstStr，每次都重新 alloc。

Phase 1 fix：`Module` 加 `interned_strings: Vec<Arc<str>>` —— loader 一次性把
`string_pool` 的 `String` 转成 `Arc<str>`。ConstStr 改为 `interned_strings[i].clone()`
（atomic refcount inc，零 heap alloc）。

同步 JIT 侧 `JitModuleCtx.string_pool: Vec<String>` → `Vec<Arc<str>>`，
`jit_const_str` 同样 just clone Arc。

## 原因 / Phase 划分

review.md C3 / Part 5 P3 完整版（3-4 天）包含：
1. `Value::Str(StringId)` — 用 4-byte pool index 替代 16-byte `Arc<str>` (Phase 2)
2. 全局 intern table 让跨 module literal 共享 (Phase 3)

Phase 1 不动 `Value::Str` 形状 —— `Arc<str>` 保留；只是 **stop allocating
fresh Arcs for pool slots**。每个 pool slot 一个永久 Arc，所有 ConstStr / 跨
module lookup 都 clone 这个 Arc（refcount inc）。

收益：每个 stdlib hot literal 现在一次 alloc，N 次 ConstStr 调用零 alloc。

不引入 wire format 变更 / Value enum 变更，所以 risk 低。

## 文档影响

- `docs/review.md` C3 / P3 状态 (🟡 Phase 1 done)
- `docs/design/runtime/vm-architecture.md` 可加 string interning 节（暂保留为 Phase 2 task）

## Scope（允许改动的文件）

| 文件 | 变更类型 | 说明 |
|---|---|---|
| `src/runtime/src/metadata/bytecode.rs` | MODIFY | `Module` 加 `interned_strings: Vec<Arc<str>>` (skip serde) |
| `src/runtime/src/metadata/loader.rs` | MODIFY | load 后 populate `interned_strings` from `string_pool` |
| `src/runtime/src/metadata/lazy_loader.rs` | MODIFY | `try_lookup_string` 返回 `Option<Arc<str>>`（不再 owned String）；lazy 加载时 populate `interned_strings` 段 |
| `src/runtime/src/vm_context.rs` | MODIFY | `try_lookup_string` 转发签名同步 `Option<Arc<str>>` |
| `src/runtime/src/interp/exec_value.rs` | MODIFY | `const_str` 改用 `interned_strings[i].clone()` |
| `src/runtime/src/jit/frame.rs` | MODIFY | `JitModuleCtx.string_pool: Vec<String>` → `Vec<Arc<str>>` |
| `src/runtime/src/jit/mod.rs` | MODIFY | compile_module init JitModuleCtx 用 interned pool |
| `src/runtime/src/jit/helpers/value.rs` | MODIFY | `jit_const_str` clone Arc |
| `src/runtime/src/metadata/merge.rs` | MODIFY (if needed) | merge_modules 把字符串放入 interned 后重新 build |
| 测试 | MODIFY | 测试中可能直接 access string_pool 的几处更新 |

## 设计要点

### 为什么不直接把 `Module.string_pool: Vec<String>` 改成 `Vec<Arc<str>>`

会破坏 zbc deserialize（bincode 期望 String）。保留 `string_pool` 不动，加
并行 `interned_strings` 字段（serde skip + 加载后 populate）—— 已有完全
相同的 pattern：`type_registry: HashMap<...> #[serde(skip)]`。

### Phase 2-3 演进路径

- Phase 2: `Value::Str` 内部存 `StringId(u32)` + 全局 pool reference（不再
  carry Arc<str>），Value 减小 ~12 bytes 每实例
- Phase 3: 全局 intern table 让 cross-module `"Length"` 字面量 dedupe 到
  单一 storage

## 任务

- [x] 0.1 NEW spec `tasks.md`
- [x] 1.1 MODIFY `bytecode.rs` 加 `Module.interned_strings`
- [x] 1.2 MODIFY `loader.rs` populate interned_strings + 加 host/main 两处 merge 路径调用
- [x] 1.3 MODIFY `lazy_loader.rs` + `vm_context.rs` `try_lookup_string` 改返回 `Arc<str>`
- [x] 1.4 MODIFY `exec_value.rs` const_str 用 interned cache
- [x] 1.5 MODIFY `jit/frame.rs` + `jit/mod.rs` JitModuleCtx 用 `Vec<Arc<str>>`
- [x] 1.6 MODIFY `jit/helpers/value.rs` jit_const_str clone Arc
- [x] 1.6.5 MODIFY `jit/vm_interface.rs` `#[allow(dead_code)]` on `JitVm` 防 dead_code warning 漏到 stderr 被 golden runner 当作输出
- [x] 1.7 VERIFY `cargo build --release` clean + `cargo test --lib` 全过 (776 + 21)
- [x] 1.8 VERIFY `./scripts/test-all.sh` 全绿（compiler + cargo lib + interp + cross-zpkg verified；JIT + stdlib stages 跟 commit 一起 finalize）
- [x] 1.9 MODIFY `review.md` 标 🟡 Phase 1 done
- [x] 1.10 归档 + commit + push
