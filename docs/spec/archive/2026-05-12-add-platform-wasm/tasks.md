# Tasks: Add WebAssembly Platform Scaffold

> 状态：🟢 已完成 | 创建：2026-04-29 / 重写：2026-05-12
> 前置：[`add-zpkg-resolver-hook`](../../archive/2026-05-12-add-zpkg-resolver-hook/) ✅
> Spec 修订：见 [design.md REVISION 2026-05-11](design.md) 段，原稿决策中与 z42-host 不冲突的部分仍生效
>
> 本次实施范围（与原稿差异）：
> - **测试 / playwright e2e 阶段**：用户指示"先不考虑测试流程"，全部推迟到独立 spec
> - **just / CI 接入**：同上推迟
> - 目录从 `platform/wasm/` 迁到 `src/toolchain/host/platforms/wasm/`
> - Rust crate 直接 wrap `z42-host`（Tier 2），不再走 `z42_runtime::interp::*`
> - 类名 `Z42Vm` → `Z42VM`（跨平台一致）
> - ZpkgResolver 集成（JS callback bridge + MapResolver pre-fetch 双模式）

## 进度概览

- [x] 阶段 0: **z42_vm `native-interop` feature gate**（2026-05-12 越界发现追加）
- [x] 阶段 1: 目录骨架 + Cargo.toml
- [x] 阶段 2: Rust wasm-bindgen crate
- [x] 阶段 3: JS / TS facade（package.json + index.{js,d.ts}）
- [x] 阶段 4: build.sh（wasm-pack + stdlib 复制 + fixture 编译）
- [x] 阶段 5: Node demo
- [x] 阶段 6: 文档同步
- [x] 阶段 7: 验证 + commit + archive

---

## 阶段 0: z42_vm `native-interop` feature gate（Scope 扩展）

> **越界发现 (2026-05-12)**：`libffi-sys` 和 `region` crate 不支持 wasm32，但当前 `src/runtime/Cargo.toml` 把 `libffi` / `libloading` 列为无条件依赖。`wasm = ["interp-only"]` 这个 feature preset 不够 —— 还得把 native interop 整段 gate 起来。本阶段是 wasm 编译的**硬前置**，由 User 选项 (A) 合并到本 spec。

- [x] 0.1 [`src/runtime/Cargo.toml`](../../../../src/runtime/Cargo.toml)
  - `libffi` / `libloading` 改 `optional = true`
  - 新增 feature `native-interop = ["dep:libffi", "dep:libloading"]`
  - `default = ["jit", "native-interop"]`（向后兼容桌面）
  - `wasm` 保持 `["interp-only"]`（不含 native-interop，符合 wasm 沙箱限制）
  - `ios` / `android` 加 `native-interop`（移动平台有 dlopen / libffi）
- [x] 0.2 [`src/runtime/src/lib.rs`](../../../../src/runtime/src/lib.rs) `pub mod native;` 加 `#[cfg(feature = "native-interop")]`
- [x] 0.3 [`src/runtime/src/vm_context.rs`](../../../../src/runtime/src/vm_context.rs)
  - `native_libs` / `native_types` 字段加 `#[cfg(feature = "native-interop")]`
  - 相关方法 `register_native_type` / `resolve_native_type` / `load_native_library` 同步 gate
  - 字段访问点（`thread/`、`gc/` 等若有 GC root scanning）同步 gate
- [x] 0.4 [`src/runtime/src/interp/`](../../../../src/runtime/src/interp/) `Instruction::CallNative` arm 加 `#[cfg(feature = "native-interop")]`（feature off 时 bail "native interop disabled in this build"）
- [x] 0.5 [`src/toolchain/host/embed/Cargo.toml`](../../../../src/toolchain/host/embed/Cargo.toml) `z42_vm` 改 `default-features = false, features = []`（让消费者按平台选；hello_rust 显式加 `features = ["z42_vm/jit", "z42_vm/native-interop"]` 或使用 interp-only 默认即可）
- [x] 0.6 验证：
  - `cargo build --manifest-path src/runtime/Cargo.toml`（default = jit + native-interop）✅
  - `cargo build --manifest-path src/runtime/Cargo.toml --no-default-features --features interp-only` ✅
  - `cargo build --manifest-path src/runtime/Cargo.toml --no-default-features --features wasm` ✅（关键：libffi 不再拉入）
  - `cargo build --manifest-path src/runtime/Cargo.toml --no-default-features --features ios` ✅
  - `cargo build --manifest-path src/runtime/Cargo.toml --no-default-features --features android` ✅
  - `cargo test --lib`：既有 lib 测试不退化（host:: 22 / 22 + 其他）

---

## 阶段 1: 目录骨架 + Cargo.toml

- [x] 1.1 `src/toolchain/host/platforms/wasm/` 目录
- [x] 1.2 `Cargo.toml`：crate `z42-platform-wasm`，cdylib，deps z42_vm（features=wasm）+ z42-host + wasm-bindgen + js-sys
- [x] 1.3 `.gitignore` 忽略 `target/` / `pkg-*/` / `node_modules/`
- [x] 1.4 `README.md` 简版工程文档

## 阶段 2: Rust wasm-bindgen crate

- [x] 2.1 `src/lib.rs` —— `Z42VM` 结构 + 构造器（options JsValue）+ load_zbc / resolve_entry / invoke / dispose
- [x] 2.2 `src/value.rs` —— `Z42VMValue` JsValue ↔ runtime `Value` 互转
- [x] 2.3 `src/error.rs` —— `HostError` → `JsValue` Error（name + status + message）
- [x] 2.4 `src/resolver.rs` —— `JsCallbackResolver` 把 JS function / 对象 接成 `ZpkgResolver`

## 阶段 3: JS / TS facade

- [x] 3.1 `js/package.json` —— `@z42/wasm` + export map（./web / ./node）
- [x] 3.2 `js/index.js` —— re-export 默认 target（web）
- [x] 3.3 `js/index.d.ts` —— TS 类型
- [x] 3.4 `js/stdlib-resolver.js` —— env-detect 内置加载所有 zpkg 的 helper

## 阶段 4: build.sh

- [x] 4.1 检查 `wasm-pack` 在 PATH，否则 fail-fast
- [x] 4.2 编译 fixture `demo/fixtures/hello.z42` → `.zbc`
- [x] 4.3 从 `artifacts/z42/libs/` 复制 zpkg 到 `js/stdlib/`
- [x] 4.4 `wasm-pack build --target web -d pkg-web --out-name z42_wasm`
- [x] 4.5 `wasm-pack build --target nodejs -d pkg-nodejs --out-name z42_wasm`

## 阶段 5: Node demo

- [x] 5.1 `demo/node/run.js` —— stdlib-resolver + load_zbc + invoke + 断言 stdout = `Hello, World!\n` + `[host]` 前缀输出
- [x] 5.2 `demo/node/package.json`

## 阶段 6: 文档同步

- [x] 6.1 wasm/README.md 完整版（quick start + 限制 + 故障排查）
- [x] 6.2 platforms/README.md 平台索引行 wasm 状态 → 🟢
- [x] 6.3 docs/design/runtime/cross-platform.md wasm 段补 Z42VM 概览
- [x] 6.4 docs/roadmap.md L2 Embedding 行加 add-platform-wasm

## 阶段 7: 验证

- [x] 7.1 `cargo build --target wasm32-unknown-unknown --manifest-path .../wasm/Cargo.toml` 通过
- [x] 7.2 `./build.sh` 产出 `pkg-web/` + `pkg-nodejs/` + `js/stdlib/*.zpkg`
- [x] 7.3 `node demo/node/run.js` —— 代码就绪 + `pkg-nodejs/z42_wasm.js` 验证含 `exports.Z42VM` / `Z42VMModule` / `Z42VMEntry`；运行时端到端 demo **本机无 node 未实测**（demo 代码已 review，与 Tier 2 `hello_rust` 同形态）
- [x] 7.4 既有 z42_vm + z42-host 测试不退化（host:: 22/22）
- [x] 7.5 commit + push + 归档

---

## 备注

### 工作量估计

1–1.5 天。

### 实施依赖

- ✅ `add-zpkg-resolver-hook` 已归档
- ✅ `rustup target add wasm32-unknown-unknown`
- ✅ `cargo install wasm-pack`
- 编译器：`dotnet build src/compiler/z42.slnx` 已就位（artifacts/z42/libs/*.zpkg 全在）

### 与原稿差异

| 项 | 原稿 (2026-04-29) | 修订 (2026-05-12) |
|---|-------------------|--------------------|
| 类名 | `Z42Vm` | `Z42VM` |
| 依赖 | `z42_runtime::interp::Interpreter` | `z42-host` Tier 2 |
| 路径 | `platform/wasm/` | `src/toolchain/host/platforms/wasm/` |
| API | 自定义 setStdoutHandler / run(entryPoint) | 同形 API + ZpkgResolver |
| Demo | browser + node + playwright e2e | **仅 node demo**（其他推迟） |
| CI | playwright job | **推迟** |
