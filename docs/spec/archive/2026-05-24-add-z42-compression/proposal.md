# Proposal: add z42.compression

## Why

z42 stdlib has no compression primitives. Real workloads need them:

- **`setup-tools.sh` self-hosting (build-driver backlog)**: NDK / Android
  SDK come as `.zip`; release artifacts ship as `.tar.gz`. Today the
  bash scripts shell out to `unzip` / `tar`. Native z42 unblocks running
  these scripts on a host that doesn't have GNU/BSD tooling pre-
  installed (windows-x64 without Git Bash, minimal containers).
- **HTTP response decoding** (when `z42.net` lands): `Content-Encoding:
  gzip` / `br` is universal on the modern web; without compression
  primitives `z42.net` would be limited to plaintext.
- **z42 release tarballs**: `release_archive.sh` currently uses host
  `tar` / `bsdtar`. Self-hosting the release pipeline needs in-language
  tar.gz / zip writers.
- **Log / cache artifacts**: zpkg uses its own compression internally
  today; future z42 apps reading external `.gz` logs or write-cache
  files need the primitive.

Roadmap places this at P2 ([`docs/design/stdlib/roadmap.md`](../../../design/stdlib/roadmap.md#p2--中等优先))
with "FFI 包 zlib 优先". This proposal cashes that in **AND** establishes
the broader "stdlib native code lives outside z42vm" pattern that all
future heavy native stdlibs (z42.net / z42.numerics / second-wave
z42.crypto algorithms) will follow.

## What Changes

### Architecture

z42.compression is the **first stdlib package whose native code is built
as a separate cdylib loaded on demand by z42vm**, not statically linked
into the z42vm binary. This is a new pattern with cross-cutting
infrastructure:

```
src/runtime/                                z42vm binary (no flate2/zstd link)
  └── crates/
      └── z42-compression/                  libz42_compression.{so,dylib,dll}
          ├── flate2 (zlib-ng feature)      separate cdylib + staticlib
          ├── zstd
          └── compression_natives.rs        — Rust impls of __deflate_* / __zstd_*

VM startup → scan native search paths → dlopen libz42_compression →
            register_z42_compression_builtins(register_callback) →
            ext_builtins map populated → resolver fallback finds names

z42 user code: [Native(lib="z42_compression", entry="__deflate_compress")]
               → BuiltinInstr (compiler short-circuits when lib= without type=)
               → name lookup hits ext_builtins → call into dlopened cdylib
```

**Platform behavior**:
- **Desktop (linux / macOS / windows)**: cdylib loaded via dlopen
- **iOS / Android**: ship BOTH cdylib and staticlib in SDK package; integrator picks
- **wasm**: dlopen unavailable → static-link via Cargo feature `bundled-compression` in z42 main crate

### Public API

1. **New stdlib package `z42.compression`** (depends on `z42.core`).
2. **Public API** (single zpkg, two namespaces):
   - `Std.Compression.Gzip.{Compress, Decompress, CompressStream,
     DecompressStream}` — RFC 1952 gzip container
   - `Std.Compression.Zlib.{...}` — RFC 1950 zlib
   - `Std.Compression.Deflate.{...}` — raw DEFLATE (RFC 1951)
   - `Std.Compression.Zstd.{...}` — Zstandard
   - `Std.Archive.Tar.{Read, Write, ListEntries}` — ustar streaming
   - `Std.Archive.Zip.{Read, Write, ListEntries, ExtractFile}` — zip
   - `Std.CompressionException` / `Std.ArchiveException`
3. **8 native builtin entries** registered via the cdylib:
   - `__deflate_compress` / `__deflate_decompress` (mode = 0/1/2 raw/zlib/gzip)
   - `__zstd_compress` / `__zstd_decompress`
   - `__compressor_begin` / `__compressor_feed` / `__compressor_finish` / `__compressor_dispose` (streaming)
4. **Native search**:
   - `Z42_NATIVE_PATH` env var (colon-separated on Unix, semicolon on Windows) overrides default
   - Default: `<z42vm exec dir>/../native/` + `<sdk root>/native/` when SDK rooted
5. **Compiler change**: extend `[Native(lib=, entry=)]` (without `type=`) to emit `BuiltinInstr(entry)` instead of `CallNativeInstr` — stdlib-internal short circuit that bypasses Tier 1 type registry (which still requires C5 byte[] marshal that's not done).
6. **Workspace + build-stdlib + index.json + SDK packaging registration.**
7. **README + design doc** (`docs/design/stdlib/compression.md`).

## Scope（允许改动的文件）

**修改 / 新增 — 编译器**：

| 文件 | 变更 | 说明 |
|------|------|------|
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Native.cs` | MODIFY | 允许 `lib + entry` 无 `type=` 组合（stdlib-internal short form） |
| `src/compiler/z42.Semantics/Codegen/IrGen.Classes.cs` | MODIFY | tier1 with no type → emit `BuiltinInstr(entry)` instead of `CallNativeInstr` |
| `src/compiler/z42.Tests/NativeAttributeTier1Tests.cs` | MODIFY | 新增 case：stdlib-internal short form |

**修改 / 新增 — VM runtime**：

| 文件 | 变更 | 说明 |
|------|------|------|
| `src/runtime/Cargo.toml` | MODIFY | 删除 flate2/zstd 主依赖；新增 `bundled-compression` feature 用于 wasm |
| `src/runtime/src/corelib/compression.rs` | DELETE | 移到新 crate |
| `src/runtime/src/corelib/compression_tests.rs` | DELETE | 同 |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 注销 8 个 `__deflate_*` / `__zstd_*` / `__compressor_*` entry |
| `src/runtime/src/native/ext.rs` | NEW | Stdlib native extension loader：搜索路径 + dlopen + symbol registration |
| `src/runtime/src/native/mod.rs` | MODIFY | 加 `pub mod ext;` |
| `src/runtime/src/vm_context.rs` | MODIFY | VmCore 加 `ext_builtins: Mutex<HashMap<String, NativeFn>>` + `compressors` slot table |
| `src/runtime/src/corelib/mod.rs` | MODIFY | `exec_builtin` / `builtin_id_of` fallback 到 `ext_builtins` |
| `src/runtime/src/metadata/tokens.rs` | MODIFY | `BuiltinId` 高位区分 static vs ext |
| `src/runtime/src/lib.rs` | MODIFY | VM init 路径调 `ext::load_all` 扫描并 dlopen ext libs |
| `src/runtime/src/native/ext_tests.rs` | NEW | ext loader 单测 |

**修改 / 新增 — z42-compression cdylib crate**：

| 文件 | 变更 | 说明 |
|------|------|------|
| `src/runtime/Cargo.toml` (workspace `members`) | MODIFY | 加 `crates/z42-compression` |
| `src/runtime/crates/z42-compression/Cargo.toml` | NEW | crate-type `["cdylib", "staticlib"]` + flate2 (zlib-ng) + zstd deps |
| `src/runtime/crates/z42-compression/src/lib.rs` | NEW | `extern "C" fn register_z42_compression_builtins(...)` + 8 `extern "C"` builtin impls |
| `src/runtime/crates/z42-compression/src/compression.rs` | NEW | Rust impl（从原 corelib/compression.rs 搬过来） |
| `src/runtime/crates/z42-compression/src/compression_tests.rs` | NEW | unit tests |

**修改 / 新增 — z42 stdlib package**：

| 文件 | 变更 | 说明 |
|------|------|------|
| `src/libraries/z42.compression/` | NEW dir | 包根 |
| `src/libraries/z42.compression/z42.compression.z42.toml` | NEW | manifest |
| `src/libraries/z42.compression/src/Gzip.z42` | NEW | `[Native(lib="z42_compression", entry="__deflate_compress")]` |
| `src/libraries/z42.compression/src/Zlib.z42` | NEW | 同 |
| `src/libraries/z42.compression/src/Deflate.z42` | NEW | 同 |
| `src/libraries/z42.compression/src/Zstd.z42` | NEW | 同 |
| `src/libraries/z42.compression/src/Tar.z42` | NEW | 纯 z42 |
| `src/libraries/z42.compression/src/Zip.z42` | NEW | 纯 z42 + 调 Deflate facade |
| `src/libraries/z42.compression/src/CompressionStream.z42` | NEW | streaming wrapper |
| `src/libraries/z42.compression/src/Compression.z42` | NEW | level constants |
| `src/libraries/z42.compression/src/Exceptions.z42` | NEW | `CompressionException` / `ArchiveException` |
| `src/libraries/z42.compression/tests/*.z42` | NEW | `[Test]` cases |
| `src/libraries/z42.workspace.toml` | MODIFY | default-members 加 z42.compression |

**修改 / 新增 — index.json 注册**：

| 文件 | 变更 |
|------|------|
| `src/toolchain/host/platforms/wasm/js/stdlib/index.json` | MODIFY |
| `src/toolchain/host/platforms/ios/Resources/stdlib/index.json` | MODIFY |
| `src/toolchain/host/platforms/android/z42vm/src/main/assets/stdlib/index.json` | MODIFY |

**修改 / 新增 — SDK packaging**：

| 文件 | 变更 | 说明 |
|------|------|------|
| `scripts/package.sh` | MODIFY | 把 `libz42_compression.{so,dylib,dll}` 加进 `<pkg>/native/`；mobile 平台同时复制 `.a` |
| `scripts/_lib/package_desktop.sh` | MODIFY | desktop 平台 cdylib 名解析 |
| `.github/workflows/ci.yml` | MODIFY | "Verify package manifest" 步骤加 `test -f $pkg/native/libz42_compression.*` |

**修改 / 新增 — 文档**：

| 文件 | 变更 |
|------|------|
| `docs/design/stdlib/compression.md` | NEW |
| `docs/design/runtime/native-ext-loader.md` | NEW — 新 infra 设计文档 |
| `docs/design/stdlib/roadmap.md` | MODIFY |
| `docs/design/stdlib/overview.md` | MODIFY |
| `src/libraries/z42.compression/README.md` | NEW |

**只读引用**：

- `src/runtime/src/native/loader.rs` — Tier 1 type loader 参考
- `src/libraries/z42.crypto/` — 现有 in-VM stdlib pattern 参考
- `docs/spec/archive/2026-05-20-add-threading-stdlib/` — slot table 模式参考
- `docs/design/language/interop.md` §5 — Tier 1 spec 参考

## Out of Scope

- **Brotli / xz / LZ4 / Snappy / bz2 / 7z**：留 follow-up spec
- **libdeflate batch 快通道**：v0 不引；v1 perf upgrade
- **zstd dictionary API**：v0 不暴露
- **zip 加密**：v0 不支持
- **Tier 1 完整 byte[] marshal (C5)**：本 spec 不解决；用 stdlib-internal short circuit 路径绕开
- **现有 stdlib native（crypto / threading 等）迁移到 ext loader**：本 spec 只把 z42.compression 走通；其他 stdlib 迁移留独立 spec
- **真正"按调用懒加载" lazy dlopen**：v0 是"VM 启动时加载 + ext_builtins map 一次性填充"。需要按 IR 指令首次 hit 才 dlopen 留 v1

## Open Questions

- [ ] **搜索路径默认值优先级**：`Z42_NATIVE_PATH` env > `<exec_dir>/../native/` > `<exec_dir>/native/`？还是平铺扫所有？
- [ ] **lib not found 行为**：z42vm 启动时 lib 找不到 → 警告还是 fatal？倾向警告（运行时调用未注册 builtin 时再 fail），让 z42vm 不依赖 compression 也能跑
- [ ] **wasm `bundled-compression` feature 默认值**：默认 on 还是要显式 opt-in？倾向 on（wasm bundle 自动包含，调用方无感）
- [ ] **mobile staticlib 与 cdylib 命名冲突**：iOS / Android RID 是产 `libz42_compression.a` + `libz42_compression.dylib` 两个并存，还是不同后缀？倾向并存（macOS 已经类似处理 libz42.a + libz42.dylib）
