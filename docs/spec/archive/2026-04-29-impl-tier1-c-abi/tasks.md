# Tasks: Implement Tier 1 C ABI Runtime (C2)

> 状态：🟢 已完成 | 完成：2026-04-29 | 创建：2026-04-29

## 进度概览

- [x] 阶段 1: 依赖与 VmContext 扩展
- [x] 阶段 2: TypeRegistry + RegisteredType
- [x] 阶段 3: Marshal 模块（Z42Value ↔ Value）
- [x] 阶段 4: Dispatch 模块（libffi）
- [x] 阶段 5: Loader 模块（dlopen + 库句柄）
- [x] 阶段 6: Error 模块（thread_local LAST_ERROR）
- [x] 阶段 7: Exports 模块（z42_* extern 函数）
- [x] 阶段 8: VmGuard + interp 入口集成
- [x] 阶段 9: CallNative IR dispatch
- [x] 阶段 10: numz42-c PoC + build.rs
- [x] 阶段 11: 端到端集成测试
- [x] 阶段 12: 错误码语义落地
- [x] 阶段 13: 文档同步
- [x] 阶段 14: 全绿验证

---

## 阶段 1: 依赖与 VmContext 扩展

- [x] 1.1 修改 `src/runtime/Cargo.toml`：
  - `[dependencies]` 加 `libffi = { version = "3.2", features = ["system"] }` 或 vendored
  - `[dependencies]` 加 `libloading = "0.8"`
  - `[build-dependencies]` 加 `cc = "1"`
- [x] 1.2 修改 `src/runtime/src/vm_context.rs`：加 `native_types` + `native_libs` 字段（Rc<RefCell<...>>）+ getter / setter / register_native_type / resolve_native_type
- [x] 1.3 修改 `src/runtime/src/vm_context_tests.rs`：基础 register/resolve 单测
- [x] 1.4 `cargo build --workspace --manifest-path src/runtime/Cargo.toml` 通过

## 阶段 2: TypeRegistry + RegisteredType

- [x] 2.1 创建 `src/runtime/src/native/mod.rs`：模块入口；声明 `pub mod registry; pub mod marshal; ...`
- [x] 2.2 创建 `src/runtime/src/native/registry.rs`：
  - `pub struct RegisteredType { module: String, name: String, descriptor_ptr: *const Z42TypeDescriptor_v1, methods: HashMap<String, MethodEntry>, ... }`
  - `pub struct MethodEntry { name: String, fn_ptr: *mut c_void, signature: String, cif: Cif, return_type: SigType }`
  - `impl RegisteredType { fn from_descriptor(desc: *const Z42TypeDescriptor_v1) -> Result<Arc<Self>> }` —— 解析 descriptor、构建 cif、组装 method 表
- [x] 2.3 创建 `src/runtime/src/native/registry_tests.rs`：register / resolve / 重复注册三个 case

## 阶段 3: Marshal 模块

- [x] 3.1 创建 `src/runtime/src/native/marshal.rs`：
  - `pub fn value_to_z42(v: &Value, target: &SigType) -> Result<Z42Value>`
  - `pub fn z42_to_value(z: &Z42Value, source: &SigType) -> Result<Value>`
  - I64 / F64 / Bool / Null / Str / NativePtr 6 个 tag 的双向
  - 类型不匹配返回 anyhow Err（不 panic）
- [x] 3.2 在 `z42-abi` 公开 `Z42_VALUE_TAG_*` 常量（NULL=0, I64=1, F64=2, BOOL=3, STR=4, OBJECT=5, TYPEREF=6, NATIVEPTR=7）
- [x] 3.3 创建 `src/runtime/src/native/marshal_tests.rs`：每个 tag 往返；F64 NaN 保留；类型不匹配 Err

## 阶段 4: Dispatch 模块（libffi）

- [x] 4.1 创建 `src/runtime/src/native/dispatch.rs`：
  - `pub enum SigType { I8/I16/I32/I64/U8.../F32/F64/Bool/CStr/PtrConst(Box<SigType>)/PtrMut(Box<SigType>)/Self_/Void }`
  - `pub fn parse_signature(s: &str) -> Result<(Vec<SigType>, SigType)>`（C2 仅 blittable 子集）
  - `pub fn build_cif(params: &[SigType], ret: &SigType) -> Cif`
  - `pub unsafe fn call(cif: &Cif, fn_ptr: *mut c_void, args: &[Z42Value]) -> Z42Value`
- [x] 4.2 创建 `src/runtime/src/native/dispatch_tests.rs`：
  - 进程内 `extern "C" fn add(i64, i64) -> i64`，cif 调用返回正确
  - `extern "C" fn(i64) -> f64` 带不同返回类型
  - 不支持的签名解析返回 Err

## 阶段 5: Loader 模块

- [x] 5.1 创建 `src/runtime/src/native/loader.rs`：
  - `pub fn load_library(ctx: &VmContext, path: &Path) -> Result<()>`
  - 内部：`libloading::Library::new(path)` → 推入 `ctx.native_libs`
  - 解析符号 `<libname>_register`（lib basename 推断）；调用之
  - 失败包装为 anyhow Err 并 set thread_local LAST_ERROR (Z0910)
- [x] 5.2 在 `VmContext` 加 `pub fn load_native_library(&self, path: impl AsRef<Path>) -> Result<()>` 委托给 loader::load_library

## 阶段 6: Error 模块

- [x] 6.1 创建 `src/runtime/src/native/error.rs`：
  - `thread_local! { pub(crate) static LAST_ERROR: Cell<Option<Z42Error>> = Cell::new(None); }`
  - `pub(crate) fn set(code: u32, message: &'static str)`（leak 短字符串到 'static 或用 `Box::leak` 风险换稳定指针）
  - `pub fn last() -> Z42Error`（返回 default {code=0, message=NULL} 当无错误）
  - `pub(crate) fn clear()`（z42_* 入口调）

## 阶段 7: Exports 模块（5 个 extern 函数）

- [x] 7.1 创建 `src/runtime/src/native/exports.rs`：
  - `#[no_mangle] pub unsafe extern "C" fn z42_register_type(desc: *const Z42TypeDescriptor_v1) -> Z42TypeRef`
  - `#[no_mangle] pub unsafe extern "C" fn z42_resolve_type(module: *const c_char, type_name: *const c_char) -> Z42TypeRef`
  - `#[no_mangle] pub unsafe extern "C" fn z42_invoke(ty: Z42TypeRef, method: *const c_char, args: *const Z42Value, arg_count: usize) -> Z42Value`
  - `#[no_mangle] pub unsafe extern "C" fn z42_invoke_method(receiver: Z42Value, method: *const c_char, args: *const Z42Value, arg_count: usize) -> Z42Value`
  - `#[no_mangle] pub extern "C" fn z42_last_error() -> Z42Error`
  - 每个入口先 `error::clear()`，然后 try 操作；失败 `error::set(code, msg)` 返 sentinel
- [x] 7.2 用 `cargo build` 验证所有符号导出（`nm -D libz42_vm.dylib | grep z42_`）

## 阶段 8: VmGuard + interp 入口集成

- [x] 8.1 在 `native/exports.rs` 顶部加 `thread_local CURRENT_VM: Cell<*const VmContext>` + `VmGuard::enter(ctx)` RAII
- [x] 8.2 修改 `src/runtime/src/interp/mod.rs::exec_function`：入口 `let _vm_guard = VmGuard::enter(ctx);`，类比 `FrameGuard`
- [x] 8.3 验证 `z42_*` 函数能从 `CURRENT_VM` 取出 VmContext 引用

## 阶段 9: CallNative IR dispatch

- [x] 9.1 修改 `src/runtime/src/interp/exec_instr.rs`：把 `Instruction::CallNative { .. }` 分支从 `bail!` 改为：
  ```
  resolve type from registry → method by symbol → marshal args
  → unsafe { dispatch::call(cif, fn_ptr, &z_args) } → unmarshal → frame.set(dst, ...)
  ```
- [x] 9.2 保留 `CallNativeVtable` / `PinPtr` / `UnpinPtr` 的 trap（C4/C5 接入）
- [x] 9.3 修改 `tests/native_opcode_trap.rs`：删 `call_native_traps_with_spec_pointer` 测试（CallNative 已不 trap）；其他 3 个保留

## 阶段 10: numz42-c PoC + build.rs

- [x] 10.1 创建 `src/runtime/tests/data/numz42-c/numz42.c`（按 design §Decision 9 示例）
- [x] 10.2 创建 `src/runtime/build.rs`：cc-rs 把 numz42.c 编译为 dylib，输出到 OUT_DIR
- [x] 10.3 通过环境变量 `Z42_SKIP_NATIVE_POC=1` 可禁用编译（CI fallback）
- [x] 10.4 验证 `cargo build` 触发编译，OUT_DIR 含 `libnumz42_c.{dylib,so,dll}`

## 阶段 11: 端到端集成测试

- [x] 11.1 创建 `src/runtime/tests/native_interop_e2e.rs`：
  - `#[test] fn counter_register_and_invoke()`：
    1. `VmContext::new()`
    2. `vm.load_native_library(env_or_skip("libnumz42_c.dylib"))` —— skip if `Z42_SKIP_NATIVE_POC=1`
    3. 手工构造 Module + Function 含 CallNative bytecode（alloc → inc×3 → get）
    4. `interp::run` → 断言返回值 = I64(3)
  - `#[test] fn unknown_type_traps_z0905`
  - `#[test] fn unknown_method_traps_z0905`

## 阶段 12: 错误码语义落地

- [x] 12.1 修改 `docs/design/error-codes.md`：把 Z0905 / Z0906 / Z0910 从"Reserved"改为详细抛出条件（参见 design §Decision 6）
- [x] 12.2 verify Z0907/Z0908/Z0909 仍标"Reserved"留给后续 spec
- [x] 12.3 grep 确认 Z0905/Z0906/Z0910 在源码至少有一个抛出点（不再仅文档）

## 阶段 13: 文档同步

- [x] 13.1 修改 `docs/design/interop.md` §10 Roadmap C2 行 → ✅ + 完成日期
- [x] 13.2 修改 `docs/design/vm-architecture.md`：VM 状态段补 `native_types` + `native_libs` 字段说明
- [x] 13.3 修改 `docs/roadmap.md` Native Interop 表 C2 行 → ✅
- [x] 13.4 创建 `src/runtime/src/native/README.md`（第 3 层 README）

## 阶段 14: 全绿验证

- [x] 14.1 `cargo build --workspace --manifest-path src/runtime/Cargo.toml`
- [x] 14.2 `cargo test --workspace --manifest-path src/runtime/Cargo.toml`
- [x] 14.3 `dotnet build src/compiler/z42.slnx` + `dotnet test src/compiler/z42.Tests/z42.Tests.csproj`
- [x] 14.4 `./scripts/test-vm.sh`
- [x] 14.5 输出验证报告（参见 workflow.md 阶段 8）
- [x] 14.6 spec scenarios 逐条对照实现位置
- [x] 14.7 Scope 表与实际改动 1:1 对齐确认

## 备注

- 若 libffi 在用户平台编译失败，启用 `libffi = { version = "3.2", features = ["bundled"] }` 切换 vendored 模式
- numz42-c PoC 故意只用 C 写（不用 Rust）—— 验证 Tier 1 ABI 真的语言中立
- 阶段 7.2 验证导出符号是 sanity check，确保 native 库能 link 到 z42 binary 暴露的 `z42_*`
