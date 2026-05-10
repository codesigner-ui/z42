# Tasks: Extend NativeFn Signature with VmContext

> 状态：🟢 已完成 | 创建：2026-04-29 | 完成：2026-04-29 | 类型：refactor

## 变更说明

把 corelib `NativeFn` 类型从 `fn(&[Value]) -> Result<Value>` 扩展为
`fn(&VmContext, &[Value]) -> Result<Value>`，让所有 builtin 函数能访问
`ctx.heap()`（以及未来的 static_fields / pending_exception 等）。

完成 MagrGC Phase 1.5：把最后 3 处 `Rc::new(RefCell::new(...))` 直构（`__obj_get_type`、
`__env_args`、`corelib/tests.rs`）迁到 `ctx.heap().alloc_*(...)` 接口。

## 完成清单

### 阶段 1: NativeFn + dispatch + entry point ✅
- [x] `corelib/mod.rs::NativeFn` 类型签名改为 `fn(&VmContext, &[Value]) -> Result<Value>`
- [x] `corelib/mod.rs::exec_builtin` 入口签名加 `ctx: &VmContext`
- [x] dispatch_table 调用 `(ctx, args)`

### 阶段 2: ~55 个 builtin 函数签名扩展 ✅
- [x] `corelib/io.rs` —— 6 个（println / print / readline / concat / len / contains）
- [x] `corelib/string.rs` —— 6 个（length / char_at / from_chars / to_string / equals / hash_code）
- [x] `corelib/math.rs` —— 12 个（pow / sqrt / floor / ceiling / round / log / log10 / sin / cos / tan / atan2 / exp）
- [x] `corelib/char.rs` —— 3 个（is_whitespace / to_lower / to_upper）
- [x] `corelib/convert.rs` —— 14 个 builtin（parse 系列 4 + 原型方法 10）
- [x] `corelib/fs.rs` —— 9 个（file_*5 + env_*2 + process_exit + time_now_ms）
- [x] `corelib/object.rs` —— 5 个（obj_get_type / ref_eq / hash_code / equals / to_str）

### 阶段 3: 2 个 Rc::new 直构迁移 ✅
- [x] `corelib/object.rs::builtin_obj_get_type` —— 走 `ctx.heap().alloc_object`
- [x] `corelib/fs.rs::builtin_env_args` —— 走 `ctx.heap().alloc_array`

### 阶段 4: 调用方传 ctx ✅
- [x] `interp/exec_instr.rs::Instruction::Builtin` —— `exec_builtin(ctx, name, args)`
- [x] `interp/dispatch.rs` 中 `__obj_to_str` fallback —— `exec_builtin(ctx, ...)`
- [x] `jit/helpers_object.rs::jit_builtin` —— `exec_builtin(vm_ctx, ...)`
- [x] `jit/helpers_mem.rs` 中 `__obj_to_str` fallback —— `exec_builtin(vm_ctx, ...)`

### 阶段 5: corelib/tests.rs ✅
- [x] 加 `fn ctx() -> VmContext` test helper
- [x] 加 `fn obj(&VmContext, &str) -> Value` 走 `ctx.heap().alloc_object`
- [x] 全部 ~30 个 `exec_builtin(name, args)` 调用更新为 `exec_builtin(&c, name, args)`

### 阶段 6: 文档同步 ✅
- [x] `gc/README.md` —— 已知限制段移除 corelib 直构条目，加 2026-04-29 完成注释
- [x] `docs/design/vm-architecture.md` —— "GC 子系统" 段同步移除 + Phase 路线表 Phase 1.5 状态改 ✅
- [x] `gc/mod.rs` —— Phase 路线表 Phase 1.5 状态改"已落地"

### 阶段 7: 验证 ✅

**编译状态**：
- ✅ `cargo build --manifest-path src/runtime/Cargo.toml` —— 0 错误 0 警告
- ✅ `dotnet build src/compiler/z42.slnx` —— 0 错误（已建好）

**测试结果**：
- ✅ Rust unit tests: **120/120 通过**
- ✅ Rust integration tests (`zbc_compat`): **4/4**
- ✅ `dotnet test`: **735/735**
- ✅ `./scripts/test-vm.sh`: **interp 101 + jit 101 = 202/202**

**全代码库 `Rc::new(.*RefCell` grep**：
仅 3 处命中，全部在 `src/runtime/src/gc/rc_heap.rs`：
- 第 3 行（注释，说明行为等价历史构造）
- 第 179 行（`alloc_object` 内部）
- 第 189 行（`alloc_array` 内部）

即：**全代码库唯一物理分配点已收口到 RcMagrGC 后端**，所有调用都走 `MagrGC` trait 接口。

### 结论：✅ 全绿，可以归档
