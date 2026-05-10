# Spec: Tier 2 Rust API (C3)

## ADDED Requirements

### Requirement: `#[z42::methods]` is the primary entry point

`#[z42::methods(module = "...", name = "...")]` 作用于 `impl T { ... }`，一次性 emit descriptor / 方法表 / 所有 `extern "C"` shim / `Z42Type` impl。

#### Scenario: 简单类型 + 两个方法
- **WHEN** 用户写：
  ```rust
  #[derive(Default)]
  pub struct Counter { value: i64 }

  #[z42::methods(module = "demo", name = "Counter")]
  impl Counter {
      pub fn inc(&mut self) -> i64 { self.value += 1; self.value }
      pub fn get(&self) -> i64 { self.value }
  }
  ```
- **THEN** 编译通过；展开后产物含：
  - `static __Counter_DESC: Z42TypeDescriptor_v1`（指向 `__Counter_METHODS`）
  - `static __Counter_METHODS: [Z42MethodDesc; 2]`
  - `extern "C" fn __shim_Counter_inc(self_ptr: *mut Counter) -> i64`
  - `extern "C" fn __shim_Counter_get(self_ptr: *const Counter) -> i64`
  - `extern "C" fn __z42_alloc_Counter() -> *mut c_void`
  - `extern "C" fn __z42_dealloc_Counter(*mut c_void)`
  - `extern "C" fn __z42_ctor_Counter(*mut c_void, *const Z42Args)`
  - `extern "C" fn __z42_dtor_Counter(*mut c_void)`
  - `impl ::z42_rs::traits::Z42Type for Counter`

#### Scenario: 缺 `module` attribute
- **WHEN** 用户写 `#[z42::methods(name = "Counter")] impl Counter { ... }`
- **THEN** `compile_error!` span 指向 attribute；message 含 `module`

#### Scenario: 缺 `name` attribute
- **WHEN** `#[z42::methods(module = "demo")] impl Counter { ... }`
- **THEN** `compile_error!` 含 `name`

#### Scenario: 不支持类型在参数中
- **WHEN** 方法签名含 `String` 参数：
  ```rust
  pub fn set_label(&mut self, s: String) { ... }
  ```
- **THEN** `compile_error!` span 指向 `String`，message 引用"pinned types arrive in spec C4"

#### Scenario: 不支持 `self` by value
- **WHEN** 方法 `pub fn consume(self) -> i64 { ... }`
- **THEN** `compile_error!` 指向 `self`

#### Scenario: 类型缺 `Default` 导致 ctor 走不过
- **WHEN** 用户类型未实现 `Default` 但用了 `#[z42::methods]`
- **THEN** 展开通过；最终编译期失败在 `<Counter as Default>::default()` 调用点（标准 Rust 错误信息）

---

### Requirement: `module!` macro registers types

`z42::module! { name: "<mod>", types: [T1, T2] }` 生成 `<mod>_register()` 入口函数。

#### Scenario: 单类型注册
- **WHEN** `z42::module! { name: "demo", types: [Counter] }`
- **THEN** 生成 `#[no_mangle] pub extern "C" fn demo_register()` 调一次 `z42_register_type(<Counter as Z42Type>::descriptor())`

#### Scenario: 多类型注册顺序与列出顺序一致
- **WHEN** `types: [A, B, C]`
- **THEN** 注册顺序 A → B → C

#### Scenario: 模块名含非法字符
- **WHEN** `name: "z42.regex"` 或 `name: "demo-bad"`
- **THEN** `compile_error!` 指明仅支持 `[A-Za-z_][A-Za-z0-9_]*`

#### Scenario: 缺 `name` 字段
- **WHEN** `z42::module! { types: [Counter] }`
- **THEN** `compile_error!`

---

### Requirement: panic 经 shim 转为 last_error

shim 内调用用户方法时 `catch_unwind` 包裹；panic 时通过 `z42_set_panic_message` 记录 Z0905 + message 然后返回零值。

#### Scenario: panic 不跨 FFI
- **WHEN** 用户方法 `panic!("oops")` 被 shim 调用
- **THEN** shim 不 unwind 到 C 调用栈；返回 `i64::default() = 0`；`z42_last_error()` 返回 Z0905，message 含 `"native shim panic"` + `"oops"`

#### Scenario: 正常返回路径不写入 last_error
- **WHEN** 用户方法正常返回 `42`
- **THEN** shim 返回 42；`z42_last_error()` 返回 NO_ERROR (code = 0)

---

### Requirement: Rust PoC 与 C PoC 共存

`numz42-rs` Rust 版 Counter 与 `numz42-c` C 版可在同一 VM 内共注册（不同 module 名），互不干扰。

#### Scenario: 两个 module 同 VM 注册成功
- **WHEN** 测试中分别调 `numz42_register_static` (C) 与 `numz42_rs_register` (Rust)
- **THEN** `vm.resolve_native_type("numz42", "Counter")` 与 `vm.resolve_native_type("numz42_rs", "Counter")` 都返回 Some 且不互相覆盖

#### Scenario: Rust Counter 端到端
- **WHEN** 注册 numz42_rs，手工构造 IR：alloc → inc×3 → get
- **THEN** 返回 `Value::I64(3)`

---

### Requirement: `#[derive(Z42Type)]` / `#[z42::trait_impl]` 仍 trap

C3 范围内这两个 macro 入口签名仍登记，展开报清晰 `compile_error!` 指向 source generator (C5)。

#### Scenario: derive 用法报清晰错误
- **WHEN** 用户 `#[derive(Z42Type)] struct X;`
- **THEN** `compile_error!` 含 "C3 主入口是 #[z42::methods]" 或同等指引

#### Scenario: trait_impl 用法报错
- **WHEN** 用户 `#[z42::trait_impl("z42.core::Display")] impl X { ... }`
- **THEN** `compile_error!` 指向 spec C5

---

### Requirement: z42-abi `unsafe impl Sync`

`Z42TypeDescriptor_v1` / `Z42MethodDesc` / `Z42FieldDesc` / `Z42MethodImpl` / `Z42TraitImpl` 标 `unsafe impl Sync`，承诺其 raw pointer 字段都指向 `'static` 内存。

#### Scenario: 静态 descriptor 可作为 `static`
- **WHEN** 用户（或宏展开）写 `static D: Z42TypeDescriptor_v1 = ...;`
- **THEN** 编译通过（Sync 由 z42-abi crate 兜底）

## IR Mapping

不新增 IR opcode；C2 已实现的 `CallNative` runtime 与 macro 生成的 descriptor 直接对接。

## Pipeline Steps

- [ ] Lexer — 不涉及
- [ ] Parser / AST — 不涉及（macro 在 z42-macros crate，不动 z42 编译器）
- [ ] TypeChecker — 不涉及
- [ ] IR Codegen — 不涉及
- [ ] VM interp — 不涉及（macro 产物直接消费现有 `RegisteredType` 路径）
- [ ] JIT / AOT — 不涉及
- [x] z42-rs / z42-macros / z42-abi crate API surface
- [x] Tier 2 用户面向 API 落地

## Documentation Sync

- `docs/design/interop.md` §10 Roadmap C3 行 → ✅ + 完成日期
- `docs/roadmap.md` Native Interop 表 C3 → ✅
- `crates/z42-macros/README.md` 状态从"C1 接口骨架"→"C3 实现完成"
- `crates/z42-rs/README.md` 增加 `prelude` 用法示例
