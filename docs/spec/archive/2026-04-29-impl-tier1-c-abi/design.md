# Design: Tier 1 C ABI Runtime Implementation

## Architecture

```
┌──────────────────────────────────────────────────────────────────────┐
│ z42 user code (C5+ 才有 syntax；C2 仅 hand-crafted bytecode)          │
│   bytecode contains `Instruction::CallNative { module, type, sym }`   │
├──────────────────────────────────────────────────────────────────────┤
│ Interp `exec_instr.rs::CallNative`                                   │
│   1. resolve TypeRegistry → RegisteredType                           │
│   2. find method by symbol → MethodEntry { fn_ptr, cif }             │
│   3. marshal Vec<Value> args → Vec<Z42Value>                         │
│   4. libffi.call(cif, fn_ptr, args) → Z42Value                       │
│   5. unmarshal Z42Value → Value, store in dst register               │
├──────────────────────────────────────────────────────────────────────┤
│ src/runtime/src/native/                                              │
│   ├── registry.rs   TypeRegistry + RegisteredType + MethodEntry      │
│   ├── marshal.rs    Z42Value <-> Value 双向                           │
│   ├── dispatch.rs   libffi cif 缓存 + call_method                    │
│   ├── loader.rs     dlopen + libloading::Library 缓存                │
│   ├── error.rs      thread_local LAST_ERROR                          │
│   └── exports.rs    z42_register_type / z42_invoke / ... 真实实现    │
├──────────────────────────────────────────────────────────────────────┤
│ VmContext.native_types: Rc<RefCell<HashMap<(String,String),          │
│                                            Arc<RegisteredType>>>>    │
├──────────────────────────────────────────────────────────────────────┤
│ thread_local CURRENT_VM: Cell<*const VmContext>                      │
│   z42_* extern 函数从这里取 VM 上下文（z42 → native 调用必然在 z42 帧 │
│   栈上，CURRENT_VM 已 set）                                           │
├──────────────────────────────────────────────────────────────────────┤
│ Native 库 (e.g. libnumz42_c.dylib)                                   │
│   #[ctor]-style or explicit numz42_register(VmCtx*) 函数             │
│   调 z42_register_type(&Z42TypeDescriptor_v1)                        │
└──────────────────────────────────────────────────────────────────────┘
```

## Decisions

### Decision 1: TypeRegistry 住在 VmContext

**选项**：
- A: 全局 `OnceLock<RwLock<HashMap>>`（进程级）
- B: VmContext 字段（每 VM 一份）

**决定**：B。
- 理由：与 `static_fields` / `pending_exception` 一致的 per-VM 隔离；测试可以 VmContext::new() 创建独立环境；避免全局可变状态污染。
- 字段类型：`Rc<RefCell<HashMap<(String, String), Arc<RegisteredType>>>>`。`Rc<RefCell>` 与 vm_context.rs 现有字段一致；外层 `Arc<RegisteredType>` 让 method dispatch 期间不持有 RefCell borrow。

### Decision 2: `z42_*` extern 函数访问 VmContext 的方式

**选项**：
- A: `z42_*` 接受 `Z42VmHandle` 参数，native 库需要传当前 vm
- B: thread_local `CURRENT_VM: Cell<*const VmContext>`，进入 interp 时 set
- C: 静态全局 VM 实例

**决定**：B。
- 理由：native callback 必然从 z42 代码发起 → 调用线程上一定有正在执行的 VM；thread_local 比传参侵入小；C 不可接受（多 VM 场景做不了）。
- 实现：`exec_function` 入口 `CURRENT_VM.set(self as *const _)`，离开（含 panic 展开）`set(null)`；类比 Phase 3f 的 exec_stack push/pop。
- 安全性：z42_* 函数取出指针时 null-check + `unsafe { &*ptr }`，misuse 直接 panic（指针来自合法 z42 callback 不会 null）。

### Decision 3: libffi 而非手写 trampoline

**选项**：
- A: 每个签名手写 `extern "C"` thunk（编译期已知签名时 0 开销，但组合爆炸）
- B: libffi 运行时构造 cif，统一 dispatch（开销略高但通用）
- C: 限定签名子集（例：仅支持 `fn(i64, i64) -> i64`）

**决定**：B。
- 依赖：`libffi = "3.2"`。
- cif 缓存：每个 RegisteredType 的 method 在注册时根据 `signature` 字符串解析参数类型，预构建 `ffi_cif`，存在 `MethodEntry.cif`。
- 签名解析：解析 `"(&Self, i64) -> Self"` 之类字符串到 `Vec<libffi::middle::Type>`。C2 仅支持 blittable 子集（i*/u*/f*/bool/CStr/`*const T`/`*mut T`）；高层类型（String/Array）通过 pinned 块借出 raw 指针——但那是 C4 的功能，C2 只支持 raw 指针。

### Decision 4: Z42Value 标签布局

**选项**：u32 tag + u64 payload。Tag 取值最终钉死：

| Tag | 名称 | Payload |
|-----|------|---------|
| 0 | Null | 0 |
| 1 | I64  | 原始位 |
| 2 | F64  | `f64::to_bits()` |
| 3 | Bool | 0 / 1 |
| 4 | Str  | `*const u8` (UTF-8 NUL-terminated 借用) |
| 5 | Object | `*mut ScriptObject` GC 句柄 raw |
| 6 | TypeRef | `*mut Z42Type` |
| 7 | NativePtr | `*mut c_void`（C2 新增，承载 native-only 不透明指针） |

> **冻结**：tag 值进入 ABI 公约，未来加新值只能尾部追加。
> 公开常量在 `z42-abi` crate 暴露：`pub const Z42_VALUE_TAG_NULL: u32 = 0;` 等。

### Decision 5: dlopen 时机

**显式 API**：`VmContext::load_native_library(path: &str) -> Result<()>`：
- 调用 `libloading::Library::new(path)`
- 把 library 句柄存入 `VmContext.native_libs: Rc<RefCell<Vec<Library>>>`（保活到 VM drop）
- 解析符号 `numz42_register` (示例约定) 并立即调用：把 native 库的类型注册推到 VM
- 失败：`Z0910 NativeLibraryLoadFailure`，include path + dlerror 信息

**理由**：
- 显式 API 比"自动 dlopen"更安全；错误时机集中
- 测试代码主动控制何时加载；启动脚本可以批量预加载
- 加载顺序明确，便于调试

替代：注册函数命名约定（`<library_name>_register`）—— C2 PoC 用这个；正式版可改 `#[ctor]` 自动调，留待 C3。

### Decision 6: 错误传递

`thread_local LAST_ERROR: Cell<Option<Z42Error>>`：
- `z42_*` 入口 `clear()` 后开始处理；失败 `set(Some(Z42Error{code, message}))`，返回失败 sentinel（`Z42TypeRef = null`、`Z42Value = NULL_VALUE`）
- `z42_last_error()` 返回上次错误，**不清除**（多次查询同结果）；下一次 `z42_*` 调用入口才清

错误码分配（落地 C1 占位）：

| Code | 抛出条件 |
|------|---------|
| Z0905 | descriptor null / module_name null / type_name null / method_count > 0 但 methods null |
| Z0906 | `desc->abi_version != Z42_ABI_VERSION (=1)` |
| Z0910 | `libloading::Library::new` 失败 / 找不到 register entry symbol |

Z0907 / Z0908 / Z0909 留给 C3 / C4 / C5。

### Decision 7: CallNative IR dispatch（取代 C1 trap）

```rust
Instruction::CallNative { dst, module, type_name, symbol, args } => {
    let registry = ctx.native_types.borrow();
    let key = (module.clone(), type_name.clone());
    let ty = registry.get(&key).ok_or_else(|| anyhow!(
        "CallNative: unknown native type {module}::{type_name} (Z0905)"
    ))?;
    let method = ty.methods.get(symbol.as_str()).ok_or_else(|| anyhow!(
        "CallNative: unknown method {module}::{type_name}::{symbol} (Z0905)"
    ))?;

    // marshal: collect args[reg] -> Vec<Z42Value>
    let z_args: Vec<Z42Value> = args.iter()
        .map(|r| marshal::value_to_z42(frame.get(*r)?))
        .collect::<Result<_>>()?;

    // libffi call
    let z_ret: Z42Value = unsafe {
        dispatch::call(method.cif.as_ref(), method.fn_ptr, &z_args)
    };

    // unmarshal
    let v = marshal::z42_to_value(&z_ret, &method.return_type)?;
    frame.set(*dst, v);
}
```

`CallNativeVtable` / `PinPtr` / `UnpinPtr` C2 仍走 trap（保留 C1 行为，由 C4/C5 接入）。

### Decision 8: Method symbol 与 entry 寻址

`Z42MethodDesc.fn_ptr` 是 native 库提供的真实函数指针；C2 不做"二次 dlsym"，注册时 native 库已经把指针放进 descriptor。

method 表 lookup 用 `HashMap<String, MethodEntry>`，key = `name`（不是 `symbol`）。`name` 来自 z42-side 调用，C5 source generator 会确保和 manifest 一致。

### Decision 9: PoC numz42-c 形态

```c
// numz42.c
#include "z42_abi.h"

typedef struct { uint32_t rc; int64_t value; } Counter;

static void* counter_alloc(void) { return malloc(sizeof(Counter)); }
static void  counter_ctor(void* self, const Z42Args* args) {
    Counter* c = (Counter*)self; c->rc = 1; c->value = 0;
}
static void  counter_dtor(void* self) { (void)self; }
static void  counter_dealloc(void* self) { free(self); }
static void  counter_retain(void* self)  { ((Counter*)self)->rc++; }
static void  counter_release(void* self) {
    Counter* c = (Counter*)self;
    if (--c->rc == 0) { counter_dtor(c); counter_dealloc(c); }
}

// Methods
static int64_t counter_inc(void* self) { return ++((Counter*)self)->value; }
static int64_t counter_get(void* self) { return ((Counter*)self)->value; }

static const Z42MethodDesc COUNTER_METHODS[] = {
    { "inc", "(*mut Self) -> i64", (void*)counter_inc, Z42_METHOD_FLAG_VIRTUAL, 0 },
    { "get", "(*mut Self) -> i64", (void*)counter_get, Z42_METHOD_FLAG_VIRTUAL, 0 },
};

static const Z42TypeDescriptor_v1 COUNTER_DESC = {
    .abi_version    = Z42_ABI_VERSION,
    .flags          = Z42_TYPE_FLAG_SEALED,
    .module_name    = "numz42",
    .type_name      = "Counter",
    .instance_size  = sizeof(Counter),
    .instance_align = _Alignof(Counter),
    .alloc          = counter_alloc,
    .ctor           = counter_ctor,
    .dtor           = counter_dtor,
    .dealloc        = counter_dealloc,
    .retain         = counter_retain,
    .release        = counter_release,
    .method_count   = sizeof(COUNTER_METHODS) / sizeof(COUNTER_METHODS[0]),
    .methods        = COUNTER_METHODS,
    .field_count    = 0, .fields = NULL,
    .trait_impl_count = 0, .trait_impls = NULL,
};

// Entry point invoked by VmContext::load_native_library
void numz42_register(void) {
    z42_register_type(&COUNTER_DESC);
}
```

### Decision 10: Build infrastructure

`src/runtime/build.rs` (NEW)：
- 仅在 `#[cfg(test)]` 路径运行（通过环境变量 / cargo:rerun-if-changed）
- 调用 `cc::Build::new().file("tests/data/numz42-c/numz42.c").include("include/").compile("numz42_c")`，输出 `libnumz42_c.{a, dylib}` 到 OUT_DIR
- 集成测试取 path 通过 `env!("OUT_DIR")` + 硬编码扩展名

依赖：`cc = "1"` 进 build-dependencies。

## Implementation Notes

### thread_local CURRENT_VM 的细节

```rust
// src/runtime/src/native/exports.rs
thread_local! {
    pub(crate) static CURRENT_VM: Cell<*const VmContext> = Cell::new(std::ptr::null());
}

pub(crate) struct VmGuard<'a> { _phantom: PhantomData<&'a VmContext> }
impl<'a> VmGuard<'a> {
    pub fn enter(ctx: &'a VmContext) -> Self {
        CURRENT_VM.with(|cell| cell.set(ctx as *const _));
        Self { _phantom: PhantomData }
    }
}
impl Drop for VmGuard<'_> {
    fn drop(&mut self) { CURRENT_VM.with(|cell| cell.set(std::ptr::null())); }
}

// In interp::exec_function entry:
let _vm_guard = VmGuard::enter(ctx);
```

### libffi cif 构造

```rust
// src/runtime/src/native/dispatch.rs
use libffi::middle::{Cif, Type, CodePtr, Arg};

fn build_cif(signature: &str) -> Result<Cif> {
    let (params, ret) = parse_signature(signature)?;
    let param_types: Vec<Type> = params.iter().map(blittable_to_ffi_type).collect();
    let ret_type = blittable_to_ffi_type(&ret);
    Ok(Cif::new(param_types, ret_type))
}
```

签名解析（C2 simple recursive-descent，不引入完整 type system parser）：
- `"() -> i64"`、`"(*mut Self) -> i64"`、`"(i64, i64) -> i64"`、`"(*const u8, usize) -> i32"`
- 不支持：`&[T]`、`String`、`Result<T, E>`（这些在 C4/C5）

### dlopen 与库句柄寿命

`Library` 必须存活到所有方法调用结束。VmContext 持有 `Vec<Library>`：

```rust
pub struct VmContext {
    // ...
    pub(crate) native_libs: Rc<RefCell<Vec<libloading::Library>>>,
}
```

VM drop 时自动释放（`Library::Drop` 触发 dlclose）。

## Testing Strategy

| 测试 | 位置 | 验证 |
|------|------|------|
| TypeRegistry CRUD | `native/registry_tests.rs` | register / resolve / 重复注册（覆盖 vs 拒绝）|
| Marshal 往返 | `native/marshal_tests.rs` | I64/F64/Bool/Null/Str 各 tag 双向不丢精度 |
| Dispatch 单元 | `native/dispatch_tests.rs` | 进程内函数指针（不 dlopen）+ libffi 调用 = 期望返回 |
| extern API 错误码 | `native/error.rs` doctests / 集成 | abi_version 不匹配 → Z0906；descriptor null → Z0905 |
| End-to-end | `tests/native_interop_e2e.rs` | dlopen libnumz42_c → register → 手工 zbc 调 inc 三次 → get 返回 3 |
| 不破坏 C1 | 现有 `tests/native_opcode_trap.rs` | `CallNativeVtable` / `PinPtr` / `UnpinPtr` 仍 trap（C2 不动它们）|
| 全绿 | `dotnet test` + `./scripts/test-vm.sh` | 没有现有测试回归 |

## Risk & Rollback

- **风险 1**：libffi 依赖在某些平台编译失败（需要本地 libffi 头文件）
  - 缓解：`libffi = "3.2"` 包含 vendored 模式（`features = ["bundled"]`）；启用避免外部依赖
- **风险 2**：build.rs 在交叉编译 / 受限 CI 环境失败（cc 不可用）
  - 缓解：build.rs 包 `cargo:rerun-if-env-changed=Z42_SKIP_NATIVE_POC`；环境变量为 1 时跳过编译，e2e 测试相应 skipped
- **风险 3**：thread_local CURRENT_VM 在多线程 host 中假设过强
  - 缓解：当前 z42 单线程，**只在主 z42 解释器线程上 set**；多线程 (L3) 出现时再设计 per-thread VM stack
- **回滚**：`Instruction::CallNative` 分支变回 `bail!` 即可恢复 C1 行为；其余新文件可以 git revert 单 commit
