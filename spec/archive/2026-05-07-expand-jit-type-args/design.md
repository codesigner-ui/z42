# Design: expand-jit-type-args

## Architecture

```
IR `Instruction::ObjNew { type_args: Vec<String> }` (already exists, D-8b-3 Phase 2)
  │
  ▼  jit/translate.rs (new marshal step)
type_args.as_ptr() + type_args.len()
(IR Vec<String> lives in module, address valid for module lifetime)
  │
  ▼  Cranelift call: jit_obj_new(... existing args ..., type_args_ptr, type_args_count)
  │
  ▼  helpers_object.rs jit_obj_new (extended signature)
      after `alloc_object`:
          if type_args_count > 0:
              let slice = std::slice::from_raw_parts(type_args_ptr, type_args_count)
              rc.borrow_mut().type_args = slice.iter().cloned().collect()
```

## Decisions

### Decision 1: Marshal Vec<String> across ABI — single helper extension vs separate setter helper

**问题**：JIT 路径需要把 `type_args: Vec<String>` 传给 helper。两种方案：

- A. 扩展现有 `jit_obj_new` 签名加 2 个参数（type_args_ptr + type_args_count）；helper 内部 alloc 后立即 populate
- B. 保持 `jit_obj_new` 不变；新增 `jit_set_type_args` helper，translate 时如果 type_args 非空，紧接 `jit_obj_new` 调用后再 emit 一次

**选项 A**：
- 优点：单次 helper call 完成 alloc + populate；行为原子（ctor 调用前 type_args 已就绪，与 interp 一致）
- 缺点：`jit_obj_new` 现有签名 9 参，再加 2 → 11 参，签名变长；ABI 重新声明

**选项 B**：
- 优点：现有 `jit_obj_new` 签名不变；新 helper 单一职责好测试
- 缺点：ctor 调用发生在 `jit_obj_new` 内部，ctor 内可能访问 `default(T)` —— 此时 type_args 还没写入！interp 路径的 type_args populate 也是在 ctor 调用 BEFORE，本变更必须保留这个顺序，B 方案破坏了

**决定**：选 **A**（扩展 jit_obj_new 签名）。

理由：interp ObjNew handler 的写法是 `alloc_object → populate type_args → call ctor`，原子顺序保证 ctor 内访问 `this.type_args` 拿得到正确值。JIT 必须复制这个顺序。Option B 把 populate 放到 ctor 之后，导致 ctor 内 `default(T)` 拿不到 type_args（与 interp 行为发散）。

### Decision 2: Pointer marshal — *const String 原始指针 vs 重新打包成 *const (ptr, len)

**问题**：Vec<String> 内每个 String 是 24 字节（ptr + len + cap）。helper 需要读各 String 的内容。

- A. 直接传 `*const String`，helper 用 `slice::from_raw_parts(ptr, count)` 重建 `&[String]`
- B. translate 时遍历 Vec<String> 重新打包成 `Vec<(*const u8, usize)>` 平面数组，传打包后的指针

**选项 A**：依赖 Rust ABI（`String` 跨 `extern "C"` 边界）。Helper 端用 `&[String]` 重建依赖布局稳定 —— 由于两侧都是同一 Rust crate / 同一 rustc 编译，布局一致；安全。

**选项 B**：完全 FFI 安全（仅传 raw pointer + usize），但需要额外打包 + leak 防止 dropping。

**决定**：选 **A**。

理由：现有 `regs_val!` 已经用相同模式（`*const u32` slice）；`*const String` 是直接扩展，无新概念。两侧编译单元一致，布局保证。translate 端直接 `let p = type_args.as_ptr() as i64`，零额外开销。

### Decision 3: Pointer 寿命

IR `Instruction::ObjNew` 的 `type_args: Vec<String>` 字段持有于 `module.functions[i].blocks[j].instructions[k]`。Module 在 `JITModule` 编译期间被借用引用；编译完成后 module 仍然存在（user code 持有 LoadedArtifact）。helper 在运行期被 JIT-compiled 函数调用，那时 module 仍存活。

**结论**：raw pointer 转成 i64 const 直接 burn-in 到 JIT 代码安全。

## Implementation Notes

### helpers_object.rs

新增 `jit_obj_new` 末尾两参：

```rust
pub unsafe extern "C" fn jit_obj_new(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32,
    cls_name_ptr: *const u8, cls_name_len: usize,
    ctor_name_ptr: *const u8, ctor_name_len: usize,
    args_ptr: *const u32, argc: usize,
    type_args_ptr: *const String, type_args_count: usize,
) -> u8 {
    // ... existing alloc logic produces obj_val ...
    // populate type_args BEFORE ctor call (mirror interp order)
    if type_args_count > 0 {
        if let Value::Object(ref rc) = obj_val {
            let slice = std::slice::from_raw_parts(type_args_ptr, type_args_count);
            rc.borrow_mut().type_args = slice.iter().cloned().collect();
        }
    }
    // ... existing ctor call logic ...
}
```

### translate.rs

ObjNew 翻译加 marshal：

```rust
Instruction::ObjNew { dst, class_name, ctor_name, args, type_args } => {
    let d = ri!(*dst);
    let (cp, cl) = str_val!(class_name);
    let (kp, kl) = str_val!(ctor_name);
    let (ap, al) = regs_val!(args);
    // type_args: pass *const String + count
    let tap = builder.ins().iconst(ptr, type_args.as_ptr() as i64);
    let tac = builder.ins().iconst(types::I64, type_args.len() as i64);
    let inst = builder.ins().call(hr_obj_new, &[frame_val, ctx_val, d, cp, cl, kp, kl, ap, al, tap, tac]);
    let ret  = builder.inst_results(inst)[0]; check!(ret);
}
```

ABI 声明同步加 `[ptr, i64t]`：

```rust
obj_new: decl!("jit_obj_new",
    [ptr, ptr, i32t, ptr, i64t, ptr, i64t, ptr, i64t, ptr, i64t],
    [i8t]),
```

## Testing Strategy

- 移除 5 个测试的 `interp_only` 标记
- `./scripts/test-vm.sh` 同时跑 interp + jit；两边输出对齐
- 新增 cargo unit test（可选）：构造 fake JitFrame，验证 jit_obj_new 写入 type_args 路径
