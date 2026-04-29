# Design: Tier 2 Rust proc macros (C3)

## Architecture

```
用户 crate                                              展开后产物（同 impl 块内）
──────────                                              ─────────────────────────
#[derive(Default)]                              →     impl Default for Counter { ... }
struct Counter { value: i64 }

#[z42::methods(module = "numz42_rs",            →     extern "C" fn __shim_Counter_inc(...) -> i64
              name = "Counter")]                      extern "C" fn __shim_Counter_get(...) -> i64
impl Counter {                                        extern "C" fn __z42_alloc_Counter() -> *mut c_void
    pub fn inc(&mut self) -> i64 { ... }              extern "C" fn __z42_dealloc_Counter(*mut c_void)
    pub fn get(&self) -> i64 { ... }                  extern "C" fn __z42_ctor_Counter(*mut c_void, ...)
}                                                     extern "C" fn __z42_dtor_Counter(*mut c_void)
                                                      static __Counter_METHODS: [Z42MethodDesc; 2] = [...]
                                                      static __Counter_DESC: Z42TypeDescriptor_v1 = {...}
                                                      impl Z42Type for Counter { ... }

z42::module! {                                  →     #[no_mangle] pub extern "C"
    name: "numz42_rs",                                fn numz42_rs_register() {
    types: [Counter],                                     unsafe {
}                                                            z42_register_type(<Counter as Z42Type>::descriptor());
                                                          }
                                                      }
```

`#[z42::methods]` 是 C3 的主入口；它一次性产出 descriptor + 方法表 + 所有 shim + `Z42Type` impl，不依赖任何外部 derive。`module!` 收口注册。

## Decisions

### Decision 1: 主入口选 `#[z42::methods]`，**`#[derive(Z42Type)]` 暂不实现**

**问题**：原计划是 `#[derive(Z42Type)]` 生成 descriptor + Z42Type impl，`#[z42::methods]` 追加方法表。两个 macro 之间需要"累积"静态数组数据，Rust proc macro 模型不直接支持（每个 macro 调用是独立 expansion）。

**选项**：
- A: 命名约定 + linkme/inventory 收集器 — 引入额外依赖、平台兼容性差、调试困难
- B: derive 占位 + methods 覆写 — 静态项不能 shadow，行不通
- C: **methods 一次性 emit 全部产物**，derive 暂不实现

**决定**：C。
- 单 macro 拿到完整 impl 块语义，能在一次展开里 emit 所有产物
- `#[z42::methods]` 接受 `module` / `name` attribute，等价 derive 的 `#[z42(module=..., name=...)]`
- `#[derive(Z42Type)]` 在 z42-macros 中保留入口签名，仍报 `compile_error!` 指向"未来 spec"（更新 message 为 "C3 主入口是 #[z42::methods]，derive(Z42Type) 推迟到 source generator 阶段"）

### Decision 2: 静态 descriptor 的拼装

`#[z42::methods]` 展开生成 descriptor 字段时：
- C 字符串走 c-string literal `c"..."` (Rust 1.77+)
- 函数指针 `Some(__z42_alloc_<Type>)`
- 数组成员引用同一文件内静态项 `__<Type>_METHODS.as_ptr()` / `.len()`
- field/trait_impl 数组在 C3 留空 (`null` / `0`)

样例（Counter）：

```rust
const __Counter_MODULE_C: *const ::core::ffi::c_char = c"numz42_rs".as_ptr();
const __Counter_NAME_C:   *const ::core::ffi::c_char = c"Counter".as_ptr();

extern "C" fn __z42_alloc_Counter() -> *mut ::core::ffi::c_void {
    ::std::boxed::Box::into_raw(::std::boxed::Box::new(
        ::core::mem::MaybeUninit::<Counter>::uninit()
    )) as *mut _
}
extern "C" fn __z42_dealloc_Counter(p: *mut ::core::ffi::c_void) {
    if !p.is_null() {
        unsafe { drop(::std::boxed::Box::from_raw(p as *mut Counter)); }
    }
}
extern "C" fn __z42_ctor_Counter(p: *mut ::core::ffi::c_void, _args: *const ::z42_abi::Z42Args) {
    unsafe { ::core::ptr::write(p as *mut Counter, <Counter as ::core::default::Default>::default()); }
}
extern "C" fn __z42_dtor_Counter(p: *mut ::core::ffi::c_void) {
    unsafe { ::core::ptr::drop_in_place(p as *mut Counter); }
}

#[allow(non_upper_case_globals)]
static __Counter_METHODS: [::z42_abi::Z42MethodDesc; 2] = [
    ::z42_abi::Z42MethodDesc {
        name:      c"inc".as_ptr(),
        signature: c"(*mut Self) -> i64".as_ptr(),
        fn_ptr:    __shim_Counter_inc as *mut ::core::ffi::c_void,
        flags:     ::z42_abi::Z42_METHOD_FLAG_VIRTUAL,
        reserved:  0,
    },
    ::z42_abi::Z42MethodDesc {
        name:      c"get".as_ptr(),
        signature: c"(*const Self) -> i64".as_ptr(),
        fn_ptr:    __shim_Counter_get as *mut ::core::ffi::c_void,
        flags:     ::z42_abi::Z42_METHOD_FLAG_VIRTUAL,
        reserved:  0,
    },
];

#[allow(non_upper_case_globals)]
static __Counter_DESC: ::z42_abi::Z42TypeDescriptor_v1 = ::z42_abi::Z42TypeDescriptor_v1 {
    abi_version:    ::z42_abi::Z42_ABI_VERSION,
    flags:          ::z42_abi::Z42_TYPE_FLAG_SEALED,
    module_name:    __Counter_MODULE_C,
    type_name:      __Counter_NAME_C,
    instance_size:  ::core::mem::size_of::<Counter>(),
    instance_align: ::core::mem::align_of::<Counter>(),
    alloc:          Some(__z42_alloc_Counter),
    ctor:           Some(__z42_ctor_Counter),
    dtor:           Some(__z42_dtor_Counter),
    dealloc:        Some(__z42_dealloc_Counter),
    retain:         None,
    release:        None,
    method_count:   2,
    methods:        __Counter_METHODS.as_ptr(),
    field_count:    0,
    fields:         ::core::ptr::null(),
    trait_impl_count: 0,
    trait_impls:    ::core::ptr::null(),
};

unsafe impl Sync for __Counter_DescSyncMarker {}
struct __Counter_DescSyncMarker;
// ↑ 不需要 — Z42TypeDescriptor_v1 含 raw pointer，需要 unsafe impl Sync。
//   Macro 直接 emit `unsafe impl Sync for ...` 给类型外（type 外部 marker）

impl ::z42_rs::traits::Z42Type for Counter {
    const MODULE: &'static str = "numz42_rs";
    const NAME:   &'static str = "Counter";
    fn descriptor() -> *const ::z42_abi::Z42TypeDescriptor_v1 {
        &__Counter_DESC
    }
}
```

> **Sync 处理**：`Z42TypeDescriptor_v1` 含 raw pointer（`*const c_char` / `*const Z42MethodDesc` 等）—— Rust 默认 `*const T: !Sync`。但所有指针都指向 `'static` 内存，确实安全。Macro emit `unsafe impl Sync for SyncWrapper { ... }`（包一个 newtype）或在 `static` 上加 `unsafe impl Sync` 简化。
>
> 简化版：**直接对 z42-abi 的 `Z42TypeDescriptor_v1` / `Z42MethodDesc` impl Sync**（z42-abi crate 内做一次，标 `unsafe impl Sync for Z42TypeDescriptor_v1 {}`）。理由：所有字段都设计为静态指向 `'static` 内存，crate-level `unsafe impl Sync` 表达此承诺。Macro 输出端不再做 marker。
>
> **C3 一并修改 z42-abi**：加 `unsafe impl Sync for Z42TypeDescriptor_v1 {}` 等 Sync 实现（Sync-only，不 Send，因为 z42 是单线程 VM）。

### Decision 3: 默认 ctor 走 `Default`

C2 numz42-c PoC 的 `counter_ctor` 显式 `c->value = 0;`。C3 Rust 版生成的 `__z42_ctor_Counter` 调 `<T as Default>::default()`。要求用户类型实现 `Default`，否则编译期报错。

未来 C5 source generator 会引入 `[Native] fn new(...)` 真正的 ctor 重载（带参数 marshal），那时 `default()` 路径作为"零参 ctor 默认"。

### Decision 4: 方法签名映射表

| Rust 写法 | 推断 ABI 签名 |
|----------|-------------|
| `&self` | `(*const Self) -> ...` 起手 |
| `&mut self` | `(*mut Self) -> ...` 起手 |
| `self`（by value） | **拒绝** |
| 参数 `i8..i64`, `u8..u64`, `f32`, `f64`, `bool` | 同名 |
| `usize`/`isize` | `u64`/`i64` |
| `*const T` / `*mut T` | 同名（任意 T，Macro 不解析） |
| `()` 返回 | `void` |
| 返回 `i*/u*/f*/bool/*const T/*mut T` | 同名 |
| 其他（`String` / `Vec<T>` / `&[T]` / `&str` / 闭包 / generic / `impl Trait`） | **拒绝**，`compile_error!` 指向具体 token |

`Self` 类型的返回（按值）C3 拒绝；C5 引入 boxed-return 后再接。

### Decision 5: shim panic 兜底

每个 shim：
```rust
#[no_mangle]
unsafe extern "C" fn __shim_Counter_inc(self_ptr: *mut Counter) -> i64 {
    match ::std::panic::catch_unwind(::std::panic::AssertUnwindSafe(|| {
        Counter::inc(unsafe { &mut *self_ptr })
    })) {
        Ok(v) => v,
        Err(_) => {
            ::z42_rs::native_helpers::set_panic("Counter::inc panicked");
            <i64 as ::core::default::Default>::default()
        }
    }
}
```

z42-rs 新增 `native_helpers` 模块，转发到 z42-vm 暴露的 `z42_set_panic_message`：

```rust
// crates/z42-rs/src/native_helpers.rs
use core::ffi::c_char;

extern "C" {
    pub fn z42_set_panic_message(msg: *const c_char);
}

pub fn set_panic(msg: &str) {
    if let Ok(cs) = ::std::ffi::CString::new(msg) {
        unsafe { z42_set_panic_message(cs.as_ptr()); }
    }
}
```

z42-vm 端：
```rust
// src/runtime/src/native/exports.rs
#[no_mangle]
pub extern "C" fn z42_set_panic_message(msg: *const c_char) {
    if msg.is_null() { return; }
    let s = unsafe { CStr::from_ptr(msg) }.to_string_lossy().into_owned();
    error::set(Z0905, format!("native shim panic: {s}"));
}
```

### Decision 6: `module!` 注册函数命名

```rust
z42::module! {
    name: "numz42_rs",
    types: [Counter, Tally],
}
```

→
```rust
#[no_mangle]
pub extern "C" fn numz42_rs_register() {
    unsafe {
        ::z42_abi::z42_register_type(<Counter as ::z42_rs::traits::Z42Type>::descriptor());
        ::z42_abi::z42_register_type(<Tally as ::z42_rs::traits::Z42Type>::descriptor());
    }
}
```

模块名仅允许 `[A-Za-z_][A-Za-z0-9_]*` —— 含 dot/dash 报错（与 z42-vm 端 `loader::guess_register_symbol` 对齐）。

### Decision 7: trait_impl 推迟

C3 不实现 `#[z42::trait_impl]`；保留 `compile_error!` 指向 C5（与 source generator 一起设计 z42-side trait 形状）。

### Decision 8: 错误诊断走 `syn::Error::to_compile_error()`

每个 fail 点用 `syn::Error::new_spanned(token, "msg").to_compile_error()` —— span 指向具体 token，message 指明问题。`tests/expand_smoke.rs` 用 `trybuild` 抓诊断。

### Decision 9: 不引入 `ctor` crate

显式 `<module>_register()` 入口，与 C2 `numz42-c` 保持一致。

## Implementation Notes

### z42-macros 子模块组织

```
crates/z42-macros/src/
├── lib.rs                  ← #[proc_macro*] 入口；路由到子模块
├── methods_attr.rs         ← #[z42::methods] 实现（核心）
├── module_macro.rs         ← module! 实现
├── signature.rs            ← 方法签名 Rust → ABI 字符串映射
├── shim.rs                 ← extern "C" shim 代码生成
└── util.rs                 ← c-string literal、ident 转换、错误辅助
```

### Workspace 调整

`crates/z42-rs/Cargo.toml` 加 `z42-macros = { path = "../z42-macros" }`。lib.rs 加：
```rust
pub use z42_macros::{methods, module, trait_impl, Z42Type};
pub mod native_helpers;

pub mod prelude {
    pub use crate::traits::{Visitor, Z42Traceable, Z42Type};
    pub use crate::types::{Descriptor, Z42Args, Z42Error, Z42TypeRef, Z42Value};
    pub use ::z42_macros::{methods, module, trait_impl, Z42Type as DeriveZ42Type};
}
```

### z42-abi `unsafe impl Sync`

新增（cumulative，不破 ABI）：
```rust
unsafe impl Sync for Z42TypeDescriptor_v1 {}
unsafe impl Sync for Z42MethodDesc {}
unsafe impl Sync for Z42FieldDesc {}
unsafe impl Sync for Z42MethodImpl {}
unsafe impl Sync for Z42TraitImpl {}
```

声明承诺：所有 raw pointer 字段都指向 `'static` 内存（descriptor 自身、c-string、shim 函数都是 `'static`）。

### 测试组织

- `crates/z42-macros/tests/expand_smoke.rs`：trybuild 验证若干 pass / fail 用例
- `src/runtime/tests/data/numz42-rs/mod.rs`：测试内联 PoC（`Counter` Rust 版）
- `src/runtime/tests/native_interop_e2e.rs`：扩展 — `rust_counter_register_and_invoke` + `c_and_rust_modules_coexist`

> Rust 版 PoC 不放独立 crate，避免 workspace 复杂化；放进 `tests/data/numz42-rs/` 作为 mod 引入测试 binary 即可。

## Testing Strategy

| 测试 | 位置 | 验证 |
|------|------|------|
| `#[z42::methods]` 正常 expansion | trybuild pass/ | 编译通过、shim + descriptor + Z42Type impl 存在 |
| 缺 module attribute | trybuild fail/ | `compile_error!` 指向 attribute、message 指明 `module` 缺失 |
| 不支持类型 (`String` 参数) | trybuild fail/ | `compile_error!` 指向 String 参数 |
| `module!` 缺 `name` | trybuild fail/ | `compile_error!` 指向 macro |
| Rust Counter alloc → inc → get | `native_interop_e2e.rs` | 返回 `I64(3)` |
| C 与 Rust 模块共存 | `native_interop_e2e.rs` | 同 VM 注册两 module 不冲突 |
| 全绿 | dotnet test + ./scripts/test-vm.sh | 不回归 |

## Risk & Rollback

- **风险 1**：Rust 1.77 c"..." literal 不可用（旧 toolchain）
  - 缓解：CI 配 `rust-toolchain.toml` 锁 1.77+；如需要兼容更老版本，改用 `concat!(..., "\0").as_ptr() as *const c_char`
- **风险 2**：`Sync` 承诺被未来 ABI 字段破坏（如非 'static 指针）
  - 缓解：z42-abi v2 / v3 升级时审视；C3 绑定的指针一律 'static，安全
- **回滚**：`#[z42::methods]` / `module!` 改回 `compile_error!` 即可恢复 C1 状态；其他文件无侵入
