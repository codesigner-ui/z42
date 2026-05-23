# Tasks: add z42.compression

> 状态：🟢 已完成 | 创建：2026-05-22 | 完成：2026-05-24
> 类型：feat（新 stdlib + 新 VM infra "stdlib native ext loader"）
> Spec：[proposal](proposal.md) + [design](design.md)

## 进度概览

- [ ] 阶段 1：编译器扩展（`[Native(lib=, entry=)]` 无 `type=`）
- [ ] 阶段 2：z42-compression cdylib crate + 迁移 Rust 代码
- [ ] 阶段 3：VM native ext loader (search path + dlopen + registration)
- [ ] 阶段 4：VM ext_builtins map + resolver fallback + BuiltinId ext bit
- [ ] 阶段 5：wasm bundled-compression feature 静态 link 兜底
- [ ] 阶段 6：z42 facade (Gzip / Zlib / Deflate / Zstd / Stream / Compression / Exceptions)
- [ ] 阶段 7：Tar + Zip pure z42
- [ ] 阶段 8：workspace + index.json 注册
- [ ] 阶段 9：SDK packaging (package.sh + ci.yml verify)
- [ ] 阶段 10：mobile staticlib + cdylib 双产物
- [ ] 阶段 11：测试 (cdylib Rust unit + z42 [Test] + ext loader integration + golden)
- [ ] 阶段 12：design doc (compression + native-ext-loader) + roadmap update
- [ ] 阶段 13：验证 + 归档

## 阶段 1：编译器扩展

- [ ] 1.1 MODIFY `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Native.cs`（或 IrGen 当前的 typecheck 站点）：
  - `Lib + Entry` 无 `TypeName` → 标记为 stdlib-internal short circuit
  - 设置 `method.NativeIntrinsic = Entry`、`method.Tier1Binding = null`
  - 现有 `Lib + Type + Entry` 三件齐 → 保持 Tier 1（CallNativeInstr）
  - 仅 `Lib` 或仅 `Entry` 等不完整组合 → E0907_NativeAttributeIncomplete（现有）
- [ ] 1.2 MODIFY `src/compiler/z42.Tests/NativeAttributeTier1Tests.cs`：新增 3 cases
  - 短形式 `lib + entry` → 落到 BuiltinInstr
  - 完整 `lib + type + entry` → 落到 CallNativeInstr（原行为不变）
  - 仅 `lib` → E0907

## 阶段 2：z42-compression cdylib

- [ ] 2.1 MODIFY `src/runtime/Cargo.toml`：删除 `flate2`/`zstd` 主依赖；workspace `members` 加 `crates/z42-compression`
- [ ] 2.2 NEW `src/runtime/crates/z42-compression/Cargo.toml`：
  ```toml
  [package]
  name    = "z42-compression"
  version = "0.1.0"
  edition = "2021"

  [lib]
  name       = "z42_compression"
  crate-type = ["cdylib", "staticlib", "rlib"]

  [dependencies]
  z42    = { path = "../.." }
  anyhow = "1"

  # flate2 同 z42 main 的 target-specific 切换策略
  flate2 = { version = "1", default-features = false, features = ["rust_backend"] }
  zstd   = { version = "0.13", default-features = false }

  [target.'cfg(not(target_arch = "wasm32"))'.dependencies]
  flate2 = { version = "1", default-features = false, features = ["zlib-ng"] }
  ```
- [ ] 2.3 NEW `src/runtime/crates/z42-compression/src/lib.rs`：
  - `#[no_mangle] extern "C" fn register_z42_compression_builtins(register: RegisterFn)` entry
  - 8 个 `extern "C" fn` builtin 实现（从原 `corelib/compression.rs` 搬过来）
  - `pub use` Re-export `register_z42_compression_builtins` 以便 wasm 静态 link 路径直接调
- [ ] 2.4 NEW `src/runtime/crates/z42-compression/src/compression.rs`：算法实现细节（从原 corelib/compression.rs 全量搬迁）
- [ ] 2.5 DELETE `src/runtime/src/corelib/compression.rs`
- [ ] 2.6 DELETE `src/runtime/src/corelib/compression_tests.rs`
- [ ] 2.7 MODIFY `src/runtime/src/corelib/mod.rs`：撤销注销 8 个 `__deflate_*` / `__zstd_*` / `__compressor_*` 静态 entry；移除 `pub mod compression;`
- [ ] 2.8 NEW `src/runtime/crates/z42-compression/src/compression_tests.rs`：unit tests（复制原 corelib/compression_tests.rs 内容）
- [ ] 2.9 `cargo build --release -p z42-compression` 产 `libz42_compression.dylib`（macOS）+ `.a`

## 阶段 3：VM native ext loader

- [ ] 3.1 NEW `src/runtime/src/native/ext.rs`：
  - `pub fn load_all(ctx: &VmContext) -> Result<()>` — 扫描搜索路径 + dlopen + invoke register entry
  - `fn native_search_paths() -> Vec<PathBuf>` — `Z42_NATIVE_PATH` env + `<exec_dir>/../native` + `<exec_dir>/native`
  - `fn parse_z42_lib_name(path) -> Option<String>` — `libz42_<name>.{so,dylib,dll}` → `<name>`
  - `fn load_one(ctx, path, name)` — dlopen + `register_<name>_builtins(callback)` invoke
  - 用 thread_local CURRENT_VM 让 register callback 在 z42vm 与 cdylib 之间安全传递 ctx 指针
- [ ] 3.2 MODIFY `src/runtime/src/native/mod.rs`：加 `pub mod ext;`
- [ ] 3.3 NEW `src/runtime/src/native/ext_tests.rs`：
  - parse_z42_lib_name 单测（lib prefix / extension / 跨平台名）
  - native_search_paths 单测（env override / 默认 fallback）
- [ ] 3.4 MODIFY `src/runtime/src/vm_context.rs::VmContext::new()`：load_all 在 init 末尾调用，错误 log warn 而非 fail

## 阶段 4：VM ext_builtins map + resolver fallback

- [ ] 4.1 MODIFY `src/runtime/src/vm_context.rs`：
  - `VmCore` 加 `ext_builtins: Mutex<ExtBuiltinTable>` 字段
  - `ExtBuiltinTable { by_name: HashMap<String, u32>, by_idx: Vec<NativeFn> }`
  - `VmCore::default()` 初始化空表
  - 还要加 `compressors: Mutex<HashMap<u64, CompressorHandle>>` slot table（streaming handle 寄存）+ `next_compressor_id: AtomicU64`
  - 注：`CompressorHandle` 类型现在 lives in z42-compression cdylib；vm_context 引用通过 `Box<dyn Any>` 或 trait object 解耦（避免 z42 main crate 依赖 z42-compression — 反向依赖）
  - **设计 fix**：`compressors` slot 改成 `HashMap<u64, Box<dyn std::any::Any + Send>>`，cdylib 端 downcast 到具体 handle type
- [ ] 4.2 MODIFY `src/runtime/src/corelib/mod.rs`：
  - 新增 `pub fn ext_builtin_id_of(ctx: &VmContext, name: &str) -> Option<BuiltinId>`
  - 修改 `exec_builtin_by_id`：检查 `id.0 & 0x8000_0000` 走 ext 路径
  - 修改 `exec_builtin(name)`：fallback chain `BUILTINS_INDEX → ext_builtins`
  - `pub type NativeFn` 改为 `pub` (外部 cdylib 引用)
- [ ] 4.3 MODIFY `src/runtime/src/metadata/resolver.rs`：builtin_id_of fallback → ext_builtin_id_of
- [ ] 4.4 MODIFY `src/runtime/src/metadata/tokens.rs`：`BuiltinId` 文档化高位含义 (`0x8000_0000` = ext)

## 阶段 5：wasm bundled-compression 静态 link

- [ ] 5.1 MODIFY `src/runtime/Cargo.toml`：
  - 加 `bundled-compression` feature：`["dep:z42-compression"]`
  - `wasm` preset 加 `"bundled-compression"`
  - `[dependencies]` 段加 `z42-compression = { path = "crates/z42-compression", optional = true }`
- [ ] 5.2 MODIFY `src/runtime/src/native/ext.rs`：
  - `pub fn load_all(ctx)` 中加 `#[cfg(feature = "bundled-compression")]` 分支
  - 直接调 `z42_compression::register_z42_compression_builtins(register_cb)` 静态注册
  - dlopen 路径仍在但作为 native target 的主路径
- [ ] 5.3 VERIFY: `cargo build --target wasm32-unknown-unknown --no-default-features --features wasm` 通过
  - 产物大小检查：wasm bundle 增加 < 400 KB

## 阶段 6：z42 facade

- [ ] 6.1 NEW `src/libraries/z42.compression/z42.compression.z42.toml`
- [ ] 6.2 NEW `src/libraries/z42.compression/src/Gzip.z42`：
  - `[Native(lib = "z42_compression", entry = "__deflate_compress")] static extern byte[] _Compress(byte[], int, int);`
  - 同 `_Decompress`
  - 公开 `Compress / Decompress / CompressStream / DecompressStream` 静态方法
- [ ] 6.3 NEW `src/libraries/z42.compression/src/Zlib.z42`（同 shape，mode=1）
- [ ] 6.4 NEW `src/libraries/z42.compression/src/Deflate.z42`（mode=0）
- [ ] 6.5 NEW `src/libraries/z42.compression/src/Zstd.z42`（algo=10 + 不同 builtin name set）
- [ ] 6.6 NEW `src/libraries/z42.compression/src/CompressionStream.z42`：sealed class 包 slot_id；`Feed / Finish / Dispose`
- [ ] 6.7 NEW `src/libraries/z42.compression/src/Compression.z42`：`Fastest=1 / Default=6 / Best=9 / ZstdDefault=3 / ZstdMax=22` 常量
- [ ] 6.8 NEW `src/libraries/z42.compression/src/Exceptions.z42`：`Std.CompressionException / Std.ArchiveException : Exception`

## 阶段 7：Tar + Zip pure z42

- [ ] 7.1 NEW `src/libraries/z42.compression/src/Tar.z42`：
  - `TarEntry` 类
  - `Read(byte[]) -> TarEntry[]`：ustar 解析
  - `Write(TarEntry[]) -> byte[]`：ustar emit
- [ ] 7.2 NEW `src/libraries/z42.compression/src/Zip.z42`：
  - `ZipEntry` 类
  - `Read(byte[]) -> ZipEntry[]`：EOCD locate + central directory + per-entry decompress（调 Deflate facade）
  - `Write(ZipEntry[], int level) -> byte[]`
  - `ExtractFile(byte[], string name) -> byte[]`
  - v0 仅支持 STORE + DEFLATE methods

## 阶段 8：workspace + index.json 注册

- [ ] 8.1 MODIFY `src/libraries/z42.workspace.toml`：default-members 加 `z42.compression`
- [ ] 8.2 MODIFY `src/toolchain/host/platforms/wasm/js/stdlib/index.json`：加 `Std.Compression` / `Std.Archive` 映射
- [ ] 8.3 MODIFY `src/toolchain/host/platforms/ios/Resources/stdlib/index.json`：同
- [ ] 8.4 MODIFY `src/toolchain/host/platforms/android/z42vm/src/main/assets/stdlib/index.json`：同
- [ ] 8.5 验证 `./scripts/build-stdlib.sh` 产 `z42.compression.zpkg` 到 `artifacts/build/libraries/z42.compression/release/dist/`

## 阶段 9：SDK packaging (package.sh + ci.yml)

- [ ] 9.1 MODIFY `scripts/package.sh` / `scripts/_lib/package_desktop.sh`：
  - 增加 cdylib build step：`cargo build --release -p z42-compression --target $rid_target`
  - 增加 cp 步骤：`cp libz42_compression.{so|dylib|dll}` → `$pkg_dir/native/`
  - mobile RID 同时 cp `libz42_compression.a`
- [ ] 9.2 MODIFY `.github/workflows/ci.yml` "Verify package manifest" step：assert `<pkg>/native/libz42_compression.*` exists
- [ ] 9.3 9 个 RID smoke test：每 RID 跑 `cargo build -p z42-compression --target $rid_target` 不破

## 阶段 10：mobile staticlib + cdylib

- [ ] 10.1 VERIFY `crate-type = ["cdylib", "staticlib", "rlib"]` 在 iOS / Android target 都产 `.a` + `.dylib`/`.so`
- [ ] 10.2 MODIFY iOS xcframework build（如 `scripts/_lib/package_ios.sh`）：把 `libz42_compression.a` 加入 xcframework slice
- [ ] 10.3 文档：在 `docs/design/runtime/native-ext-loader.md` 标注 mobile integrator 二选一（dlopen vs static link）

## 阶段 11：测试

- [ ] 11.1 z42-compression cdylib Rust unit tests（搬迁自原 compression_tests.rs，~20 cases）
- [ ] 11.2 NEW `src/runtime/src/native/ext_tests.rs`：~5 cases（path parse / search / load fixture）
- [ ] 11.3 NEW `src/libraries/z42.compression/tests/gzip_round_trip.z42` + zlib / deflate / zstd / streaming / error 共 ~8 z42 [Test] 文件
- [ ] 11.4 NEW `src/libraries/z42.compression/tests/tar_multi.z42` + `zip_multi.z42`
- [ ] 11.5 NEW `src/tests/compression/gzip_hello/` golden（source.z42 + expected_output.txt）
- [ ] 11.6 VERIFY: `cargo test -p z42-compression --release` 全绿
- [ ] 11.7 VERIFY: `cargo test --release` (z42 main) 不破现有测试
- [ ] 11.8 VERIFY: `./scripts/test-stdlib.sh` 覆盖 z42.compression 6+ test files 全绿

## 阶段 12：文档

- [ ] 12.1 NEW `docs/design/stdlib/compression.md`：API + 性能 benchmark + Deferred 段
- [ ] 12.2 NEW `docs/design/runtime/native-ext-loader.md`：基建设计文档（架构 / 搜索路径 / register entry signature / wasm 兜底 / mobile static-or-dynamic / 与 Tier 1 关系）
- [ ] 12.3 MODIFY `docs/design/stdlib/roadmap.md`：
  - "已落地" 加 z42.compression
  - P2 表划掉
  - "Deferred Backlog Index" 加 compression.md + native-ext-loader.md 索引
- [ ] 12.4 MODIFY `docs/design/stdlib/overview.md`：包列表加 z42.compression
- [ ] 12.5 MODIFY `docs/design/runtime/vm-architecture.md`：加 "Native ext loader" 章节，指向 native-ext-loader.md
- [ ] 12.6 NEW `src/libraries/z42.compression/README.md`：简介 + API 列表 + cdylib vs in-VM 注释

## 阶段 13：验证 + 归档

- [ ] 13.1 `./scripts/test-all.sh --parallel` 全绿
- [ ] 13.2 `cargo test --release` (workspace) 全绿
- [ ] 13.3 跨平台编译验证：`just build-wasm-feature` / `just build-ios-feature` / `just build-android-feature` 不破
- [ ] 13.4 binary size 验证：
  - z42vm 大小**不增加**（compression 已经移出来）
  - libz42_compression.dylib < 600 KB（macOS arm64 release）
- [ ] 13.5 性能 spot check：1 MB 文本 gzip 默认 level via z42 facade 路径，本地 macOS arm64 ≥ 150 MB/s（含 dlopen + ext_builtins lookup + value marshal overhead）
- [ ] 13.6 mv `docs/spec/changes/add-z42-compression/` → `docs/spec/archive/YYYY-MM-DD-add-z42-compression/`
- [ ] 13.7 commit + push

## 备注 / Open Questions

实施中浮现的问题留这里答：

- (待答) ext_builtins lookup 在 BuiltinId 高位 0x8000_0000 标记是否会与现有 BuiltinId range 冲突？（当前 BUILTINS 仅 ~80 条，距离 2^31 远）
- (待答) cdylib `path = "../.."` 依赖 z42 crate 是否引起 Cargo workspace cycle 警告？预案：用 `default-features = false` + 精确 feature 选择
- (待答) wasm static link 路径是否需要重新发明 native_libs lifetime keep-alive（无 dlopen，没有 lib handle 要保留）？预案：feature-gate 跳过 native_libs push
