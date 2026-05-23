# Design: add z42.compression

## Architecture overview

```
                                z42vm binary
                               ┌──────────────────────────────┐
z42 user code                  │ corelib BUILTINS[]           │
  ↓                            │   (in-VM static fns)         │
[Native(lib="z42_compression", │                              │
        entry="__deflate_      │ ext_builtins: HashMap        │
              compress")]      │   (dlopened lib fn ptrs)     │
  ↓                            │                              │
BuiltinInstr("__deflate_       │ native::ext::load_all()      │
              compress")       │   ↓ at VM startup            │
  ↓ resolver                   │   scan paths → dlopen        │
ext_builtins["__deflate_"…]    └──────────────────────────────┘
  ↓ fn ptr                                  ↓ dlopen
                              libz42_compression.{so,dylib,dll}
                              ┌──────────────────────────────┐
                              │ pub extern "C" fn            │
                              │   register_z42_compression_  │
                              │     builtins(register_fn)    │
                              │   → register_fn(             │
                              │       "__deflate_compress",  │
                              │       deflate_compress_impl) │
                              │                              │
                              │ extern "C" deflate_compress_ │
                              │   impl: NativeFn             │
                              │   (uses flate2 + zstd)       │
                              └──────────────────────────────┘
```

Three layers of code change, in dependency order:

1. **Compiler**: extend `[Native(lib=, entry=)]` (no `type=`) → `BuiltinInstr`
2. **VM**: native ext loader infrastructure (search paths, dlopen, ext_builtins map, resolver fallback)
3. **z42-compression cdylib**: separate crate exposing builtins
4. **z42 stdlib facade**: long-form `[Native]` declarations

## Decisions

### Decision 1: Same-build-tree ABI (no libffi, no marshal)

**问题**：cdylib ↔ z42vm 之间怎么传 `Value` / `&[Value]` / `&VmContext`？Tier 1 用 libffi + `Z42Value` tagged union（C ABI 稳定），但 byte[] / Array 路径未实现（C5 spec 未完）。

**选项**：
- A. 完成 C5 (Tier 1 byte[] marshal) 后再做 — 拖 ~1-2 周
- B. **同 build tree ABI**：cdylib 和 z42vm 从同一 z42 source tree 编译，共享 `Value` / `VmContext` 类型；版本锁定保证 ABI 一致
- C. 自定义 C ABI（拍平所有跨界数据为 `*const u8 + usize`）— 重复造 marshal

**决定**：**B**。

**理由**：
- z42-compression 是 z42 SDK 内部组件，**永远与 z42vm 同版本发布**，不存在跨版本兼容问题
- `NativeFn = fn(&VmContext, &[Value]) -> Result<Value>` 直接复用，零额外 marshal 代码
- 用户写的 Tier 1 native 库（如 numz42）仍走 C5 + libffi 路径（不同问题、不同解法）

**实施约束**：cdylib 必须用 `Cargo.toml` `path = "../.."` 依赖 z42 crate（不是 git / crates.io）。每次 release 两者一起 build + 一起打包。

### Decision 2: `[Native(lib=, entry=)]` no-`type=` short-circuit at codegen

**问题**：Tier 1 的 `[Native(lib=, type=, entry=)]` codegen 走 `CallNativeInstr` → libffi。compression 不需要 type registry（没有 stateful native type），但需要 lib= 指定加载目标。

**决定**：编译器扩展 `lib + entry`（无 `type=`）→ `BuiltinInstr(entry)`。

具体改动：

```csharp
// src/compiler/z42.Semantics/TypeCheck/TypeChecker.Native.cs
// Tier 1 form validation:
if (binding.Lib is not null && binding.Entry is not null) {
    if (binding.TypeName is null) {
        // Stdlib-internal short circuit: emit BuiltinInstr at codegen.
        // Lib hint is recorded for diagnostics but doesn't drive runtime
        // dispatch — that's the ext_builtins registry's job.
        method.NativeIntrinsic = binding.Entry;
        method.Tier1Binding    = null;  // intentionally cleared
    }
    // else: full Tier 1 type registry path (existing C6 behavior)
}
```

```csharp
// IrGen.Classes.cs:101 unchanged — it already routes through
// NativeIntrinsic when present.
```

**理由**：
- 编译器改动最小（typecheck 1 处 + 0 codegen 改动 — 因为现有 codegen 已经基于 `NativeIntrinsic != null` vs `Tier1Binding != null` 分流）
- z42 source 语法保持稳定（用户写 `[Native(lib=, entry=)]`）
- 编译器侧 lib hint 可记入 zbc metadata 用作 zpkg.dependencies 推导（"this zpkg requires libz42_compression at runtime"）— 后续 follow-up 可以实现

### Decision 3: ext_builtins is a separate HashMap, not a static slice extension

**问题**：static `BUILTINS[]: &[(&str, NativeFn)]` 在 build time 固定。怎么加 dlopened lib 的 fn_ptr？

**选项**：
- A. 把 BUILTINS 改成 runtime `Vec<...>` + 内部填充 — 改 hot path，影响所有现有 builtin
- B. **平行 `ext_builtins: Mutex<HashMap<String, NativeFn>>`**；resolver 先查 BUILTINS_INDEX，未命中再查 ext_builtins
- C. 在 build time 通过 `inventory` crate 收集 — 但需要 cdylib 在 build 时静态注册，不符合 dlopen lazy load 模式

**决定**：**B**。

**实施**：
- `VmCore` 增加 `ext_builtins: Mutex<HashMap<String, NativeFn>>` 字段
- `BuiltinId` 内部表示扩展：`u32` 高位 `0x8000_0000` 标记 "ext id"，低 31 位是 ext_builtins 的 index（同时维护 `ext_builtins_by_id: Vec<NativeFn>` 用作 id → fn 快表）
- `builtin_id_of(name)` 先查 `BUILTINS_INDEX` → 返回静态 id；未命中查 `ext_builtins_index` → 返回 ext id
- `exec_builtin_by_id(id)`：if (id & 0x8000_0000) → ext_builtins_by_id else BUILTINS
- 热路径开销：一次 bitmask 检查 — 与静态 builtin dispatch 等价（都是单次 array 索引）

### Decision 4: Native search path discovery

**问题**：z42vm 启动时怎么找 `libz42_compression.{so,dylib,dll}`？

**实施**：按以下顺序扫描，**第一个找到的赢**（后续条目不覆盖）：

```rust
fn native_search_paths() -> Vec<PathBuf> {
    let mut paths = Vec::new();

    // 1. Explicit override (CI / dev / power user)
    if let Ok(p) = std::env::var("Z42_NATIVE_PATH") {
        for part in p.split(if cfg!(windows) { ';' } else { ':' }) {
            paths.push(PathBuf::from(part));
        }
    }

    // 2. Default SDK layout (alongside z42vm binary)
    if let Ok(exe) = std::env::current_exe() {
        // <exec_dir>/../native/  (SDK package layout)
        if let Some(parent) = exe.parent().and_then(|p| p.parent()) {
            paths.push(parent.join("native"));
        }
        // <exec_dir>/native/      (release dev layout)
        if let Some(dir) = exe.parent() {
            paths.push(dir.join("native"));
        }
    }

    paths
}
```

Scanned files: `lib*.{so,dylib,dll}` (with `lib` prefix on Unix, no prefix on Windows). dlopen each. Skip files that fail to load (warn, don't fail z42vm startup — so z42 apps that don't use compression can still boot).

### Decision 5: cdylib registration entry signature

**问题**：dlopened cdylib 怎么把自己的 builtins 告诉 VM？

**决定**：每个 stdlib ext cdylib 必须 export 一个名为 `register_<libname>_builtins` 的 `extern "C"` 函数：

```rust
// src/runtime/crates/z42-compression/src/lib.rs

pub use z42::corelib::NativeFn;

type RegisterFn = unsafe extern "C" fn(name: *const c_char, fn_ptr: NativeFn);

#[no_mangle]
pub extern "C" fn register_z42_compression_builtins(register: RegisterFn) {
    unsafe {
        register(b"__deflate_compress\0".as_ptr().cast(), deflate_compress);
        register(b"__deflate_decompress\0".as_ptr().cast(), deflate_decompress);
        register(b"__zstd_compress\0".as_ptr().cast(), zstd_compress);
        register(b"__zstd_decompress\0".as_ptr().cast(), zstd_decompress);
        register(b"__compressor_begin\0".as_ptr().cast(), compressor_begin);
        register(b"__compressor_feed\0".as_ptr().cast(), compressor_feed);
        register(b"__compressor_finish\0".as_ptr().cast(), compressor_finish);
        register(b"__compressor_dispose\0".as_ptr().cast(), compressor_dispose);
    }
}

extern "C" fn deflate_compress(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    // ... flate2 impl
}
```

VM side calls the entry, passing a register callback that inserts (name, fn_ptr) into ext_builtins.

### Decision 6: VM startup orchestration

**问题**：VM 启动时何时扫 native 扩展？

**决定**：在 `VmContext::new()` 内、`Module` load 之前。

```rust
impl VmContext {
    pub fn new() -> Pin<Box<Self>> {
        let core = Arc::new(VmCore::default());
        // ... existing init ...
        let ctx = /* ... */;

        // add-z42-compression: load stdlib native extensions.
        // Failures are logged but don't abort VM startup so apps that
        // don't need any native ext can still boot.
        if let Err(e) = crate::native::ext::load_all(&ctx) {
            tracing::warn!("native ext load: {}", e);
        }

        ctx
    }
}
```

`ext::load_all` scans paths → dlopens libs → calls `register_<basename>_builtins` for each.

### Decision 7: wasm bundled-compression Cargo feature

**问题**：wasm32 没有 dlopen。

**决定**：z42 main crate 加 `bundled-compression` feature；wasm preset 自动开启；feature on 时 z42 main crate **直接 link** z42-compression crate（作为 staticlib），并在 VmContext::new() 中 directly register builtins（绕过 dlopen path）：

```toml
# src/runtime/Cargo.toml
[features]
default = ["jit", "native-interop"]
bundled-compression = ["dep:z42-compression"]
wasm = ["interp-only", "bundled-compression"]   # wasm 默认开

[dependencies]
z42-compression = { path = "crates/z42-compression", optional = true }
```

```rust
// In VmContext::new():
#[cfg(feature = "bundled-compression")]
{
    z42_compression::register_z42_compression_builtins(|name, fn_ptr| {
        ctx.core.ext_builtins.lock().insert(name.to_string(), fn_ptr);
    });
}
```

Same `ext_builtins` registry, just populated at compile-time via direct call instead of dlopen.

### Decision 8: mobile staticlib + cdylib

**问题**：iOS / Android 上是否仍走 dlopen？

**决定**：**两个都产，integrator 二选一**。

Build pipeline 产物：
- `libz42_compression.so` (Android) / `.dylib` (iOS) — cdylib
- `libz42_compression.a` — staticlib（用 `crate-type = ["cdylib", "staticlib"]`，cargo 一次 build 两个产物）

SDK package layout：
- Android `<pkg>/native/{libz42_compression.so, libz42_compression.a}`
- iOS `<pkg>/native/{libz42_compression.dylib, libz42_compression.a, Z42Compression.xcframework/}`（xcframework 包含静态 + 动态 slice）

Integrator (Kotlin / Swift) 在 build system 选择 link `.so` (runtime dlopen by z42vm) 还是直接 `.a` (compile-time link → 需要在 z42vm side 强制 bundled-compression feature)。前者灵活、后者 binary 单一更紧凑。

### Decision 9: Backward compatibility（None — new package）

z42.compression 是新包；现有 stdlib 不变。但本 spec **建立 stdlib native ext loader 基建**，未来 spec 可独立把现有 stdlib（crypto 等）迁过来。

## Implementation Notes

### z42 facade API

```z42
namespace Std.Compression;

using Std;

public static class Gzip {
    [Native(lib = "z42_compression", entry = "__deflate_compress")]
    private static extern byte[] _Compress(byte[] data, int level, int mode);

    [Native(lib = "z42_compression", entry = "__deflate_decompress")]
    private static extern byte[] _Decompress(byte[] data, int mode);

    public static byte[] Compress(byte[] data) {
        return _Compress(data, Compression.Default, 2);  // mode 2 = gzip
    }

    public static byte[] Compress(byte[] data, int level) {
        return _Compress(data, level, 2);
    }

    public static byte[] Decompress(byte[] data) {
        return _Decompress(data, 2);
    }

    public static CompressionStream CompressStream(int level) {
        return CompressionStream._BeginEncode(AlgoId.Gzip, level);
    }

    public static CompressionStream DecompressStream() {
        return CompressionStream._BeginDecode(AlgoId.Gzip);
    }
}

// Zlib / Deflate / Zstd 同 shape，mode/algo 不同
```

### Compiler change detail

[`src/compiler/z42.Semantics/TypeCheck/TypeChecker.Native.cs`] currently
validates Tier 1 binding requires all three fields. New rule:

```csharp
// Existing Tier 1 (with type=)
if (binding.Lib != null && binding.TypeName != null && binding.Entry != null) {
    // Tier 1 full: keep as-is, codegen emits CallNativeInstr
    return ValidationResult.Ok(binding);
}

// New: stdlib-internal short form (lib + entry without type)
if (binding.Lib != null && binding.Entry != null && binding.TypeName == null) {
    // Downgrade to BuiltinInstr at codegen by setting NativeIntrinsic
    method.NativeIntrinsic = binding.Entry;
    method.Tier1Binding    = null;  // clear so codegen takes intrinsic path
    return ValidationResult.Ok(null);  // not Tier 1
}

// Existing failure: incomplete Tier 1
return ValidationResult.Error(E0907_NativeAttributeIncomplete);
```

### VM ext loader

New file `src/runtime/src/native/ext.rs`:

```rust
use std::collections::HashMap;
use std::ffi::CStr;
use std::os::raw::c_char;
use std::path::PathBuf;
use anyhow::Result;
use parking_lot::Mutex;

use crate::corelib::NativeFn;
use crate::vm_context::VmContext;

type RegisterFn = unsafe extern "C" fn(*const c_char, NativeFn);
type RegisterEntryPoint = unsafe extern "C" fn(RegisterFn);

/// Scan search paths, dlopen each `libz42_*.{so,dylib,dll}`, invoke
/// `register_<basename>_builtins(callback)` to populate ext_builtins.
pub fn load_all(ctx: &VmContext) -> Result<()> {
    for dir in native_search_paths() {
        if !dir.is_dir() { continue; }
        for entry in std::fs::read_dir(&dir)?.flatten() {
            let path = entry.path();
            if let Some(name) = parse_z42_lib_name(&path) {
                if let Err(e) = load_one(ctx, &path, &name) {
                    tracing::warn!("load {}: {}", path.display(), e);
                }
            }
        }
    }
    Ok(())
}

fn parse_z42_lib_name(path: &std::path::Path) -> Option<String> {
    // Match libz42_<name>.{so,dylib,dll} → "<name>"
    let stem = path.file_stem()?.to_str()?;
    let core = stem.strip_prefix("lib").unwrap_or(stem);
    core.strip_prefix("z42_").map(String::from)
}

fn load_one(ctx: &VmContext, path: &std::path::Path, name: &str) -> Result<()> {
    let lib = unsafe { libloading::Library::new(path)? };
    let entry_sym = format!("register_z42_{}_builtins", name);
    let register: libloading::Symbol<RegisterEntryPoint> =
        unsafe { lib.get(entry_sym.as_bytes())? };

    // Pass a register callback that captures ctx via thread-local CURRENT_VM.
    // The cdylib calls this for each builtin it wants to expose.
    thread_local! {
        static REG_CTX: std::cell::Cell<*const VmContext> = std::cell::Cell::new(std::ptr::null());
    }

    extern "C" fn register_cb(name_cstr: *const c_char, fn_ptr: NativeFn) {
        let ctx_ptr = REG_CTX.with(|c| c.get());
        if ctx_ptr.is_null() { return; }
        let ctx = unsafe { &*ctx_ptr };
        let name = unsafe { CStr::from_ptr(name_cstr) }.to_string_lossy().into_owned();
        let mut ext = ctx.core.ext_builtins.lock();
        let id = ext.len() as u32 | 0x8000_0000;
        ext.insert(name, fn_ptr);
        // The id assignment + insert ordering matters for the parallel
        // Vec lookup table — see ext_builtins_by_id maintenance below.
    }

    REG_CTX.with(|c| c.set(ctx as *const VmContext));
    unsafe { register(register_cb); }
    REG_CTX.with(|c| c.set(std::ptr::null()));

    ctx.core.native_libs.lock().push(lib);  // keep lib alive for VM lifetime
    Ok(())
}

fn native_search_paths() -> Vec<PathBuf> {
    let mut paths = Vec::new();
    if let Ok(p) = std::env::var("Z42_NATIVE_PATH") {
        let sep = if cfg!(windows) { ';' } else { ':' };
        for part in p.split(sep) { paths.push(PathBuf::from(part)); }
    }
    if let Ok(exe) = std::env::current_exe() {
        if let Some(parent) = exe.parent().and_then(|p| p.parent()) {
            paths.push(parent.join("native"));
        }
        if let Some(dir) = exe.parent() {
            paths.push(dir.join("native"));
        }
    }
    paths
}
```

### Resolver fallback

`src/runtime/src/corelib/mod.rs::builtin_id_of(name)`:

```rust
pub fn builtin_id_of(name: &str) -> Option<BuiltinId> {
    // Static fast path
    if let Some(&id) = builtin_index().get(name) {
        return Some(BuiltinId(id));
    }
    // ext fallback (requires VmContext — see ext_builtin_id_of below)
    None  // caller must call ext_builtin_id_of with ctx if this returns None
}

pub fn ext_builtin_id_of(ctx: &VmContext, name: &str) -> Option<BuiltinId> {
    let ext = ctx.core.ext_builtins.lock();
    ext.get_index_of(name).map(|idx| BuiltinId(idx as u32 | 0x8000_0000))
}
```

Resolver call site (`metadata/resolver.rs`):

```rust
let id = corelib::builtin_id_of(name)
    .or_else(|| corelib::ext_builtin_id_of(ctx, name))
    .ok_or(...)?;
```

`exec_builtin_by_id`:

```rust
pub fn exec_builtin_by_id(ctx: &VmContext, id: BuiltinId, args: &[Value]) -> Result<Value> {
    if id.0 & 0x8000_0000 != 0 {
        let idx = (id.0 & 0x7FFF_FFFF) as usize;
        let ext = ctx.core.ext_builtins.lock();
        let (_, fn_ptr) = ext.get_index(idx).expect("ext id valid");
        fn_ptr(ctx, args)
    } else {
        BUILTINS[id.0 as usize].1(ctx, args)
    }
}
```

Note: `HashMap::get_index` doesn't exist in std; need `indexmap` crate or
secondary `Vec<NativeFn>` maintained alongside. Use the Vec approach to
avoid new dep:

```rust
pub(crate) struct ExtBuiltins {
    by_name: HashMap<String, u32>,    // name → idx
    by_idx:  Vec<NativeFn>,           // idx → fn_ptr
}
```

### Tests + verification

Same as v0 plan (z42 `[Test]` files + golden tests + Rust unit tests in
the cdylib crate). Add a new test category: "ext loader integration":
- VM with `Z42_NATIVE_PATH` pointed at fixture dir
- assert ext_builtins populated after `VmContext::new()`
- exec_builtin_by_id(ext_id) actually runs the cdylib function

### Build pipeline impact

`scripts/package.sh` changes:

```bash
# Build z42-compression cdylib (mirrors how libz42 itself is built)
cargo build --release --manifest-path src/runtime/crates/z42-compression/Cargo.toml \
    --target $rid_target

# Copy artifact into <pkg>/native/
cp $cargo_target/release/libz42_compression.{so,dylib,dll} $pkg_dir/native/

# Mobile: also produce staticlib
if [[ "$rid" == ios-* || "$rid" == android-* ]]; then
    cp $cargo_target/release/libz42_compression.a $pkg_dir/native/
fi
```

`ci.yml` "Verify package manifest + native artifacts" step adds:

```bash
test -f "$pkg_dir/native/libz42_compression.so" \
    || test -f "$pkg_dir/native/libz42_compression.dylib" \
    || test -f "$pkg_dir/native/libz42_compression.dll" \
    || { echo "missing native/libz42_compression.*"; exit 1; }
```

## Testing Strategy

(unchanged from previous draft — see tasks.md Stage 6 for the full list)

## Backward Compatibility

No breaking changes. New package, new infrastructure. Existing stdlib
(crypto / threading / etc.) continues to use in-VM `BUILTINS[]` — ext
loader is a parallel path, not a replacement.

Future spec `migrate-stdlib-native-to-ext-loader` (separate work) will
selectively migrate stdlibs where the benefit (binary size, modularity)
justifies the cost. Not in this spec.
