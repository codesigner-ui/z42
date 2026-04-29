# Tasks: JIT Stack Scanning for Cycle Collector (Phase 3f-2)

> 状态：🟢 已完成 | 创建：2026-04-29 | 完成：2026-04-29 | 类型：vm（**bugfix**）

## 完成清单

### 阶段 1: 6 个 JitFrame::new 站点对接 exec_stack ✅
- [x] `jit/mod.rs::run_fn` 顶层 entry frame
- [x] `jit/helpers_object.rs::jit_call`
- [x] `jit/helpers_object.rs::jit_obj_new` ctor
- [x] `jit/helpers_object.rs::jit_vcall` primitive 路径
- [x] `jit/helpers_object.rs::jit_vcall` 类方法路径
- [x] `jit/helpers_mem.rs` ToString fallback

### 阶段 2: push/pop 模式 ✅
统一手写：
```rust
let mut callee = JitFrame::new(...);
let vm_ctx = vm_ctx_ref(ctx);
vm_ctx.push_frame_regs(&callee.regs as *const _);
let result = jit_fn(&mut callee, ctx);
vm_ctx.pop_frame_regs();
// 处理 result / recycle
```

不用 RAII guard：JIT helper 是 extern "C"，无 panic 跨 FFI；多处显式
`return 1; recycle()` 模式下，手写 push/pop 顺序更清晰。

### 阶段 3: 测试 ✅
- [x] **新增 z42 golden test** `112_gc_jit_transitive`：
      `MakeHolder()` 返回 holder，holder.Child 是 inner（inner 仅通过 holder
      间接可达）；Main 接收返回值后调 `GC.ForceCollect()`，verify
      `holder.Child.Value` 仍 = 42。**interp + JIT 双模式都过**。
- [x] 现有 110 / 111 全绿

### 阶段 4: 文档同步 ✅
- [x] `gc/rc_heap.rs` 模块文档：限制清单 2 项 → 1 项（JIT 已对接）+ 加 Phase 3f-2 完成段
- [x] `gc/README.md` 同步
- [x] `docs/design/vm-architecture.md` Phase 路线表 3f-2 ✅
- [x] `gc/mod.rs` Phase 路线表 3f-2 ✅

### 阶段 5: 验证 ✅

**编译状态**：
- ✅ `cargo build` 0 错误 0 警告
- ✅ `dotnet build` 0 错误（已建好）

**测试结果**：
- ✅ Rust unit tests: **142/142 通过**
- ✅ Rust integration tests: **4/4**
- ✅ `dotnet test`: **743/743**
- ✅ `./scripts/test-vm.sh`: **interp 104 + jit 104 = 208/208**（含
      112_gc_jit_transitive 双模式都过）

### 结论：✅ 全绿，可以归档

## 后续

唯一剩余 GC 限制：**OOM 真拒绝**。需要升级 `MagrGC` trait `alloc_*` 签名为
`Result<Value>` 或加 `try_alloc_*` API。涉及全 callsite 错误处理路径（interp +
JIT helpers + corelib）。
