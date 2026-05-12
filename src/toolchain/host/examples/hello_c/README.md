# hello_c — Embedding API C reference example

## Status

🟢 **End-to-end 落地**（2026-05-12，`enable-hello-c-desktop` spec）。`main.c` 按 [`z42_host.h`](../../../../runtime/include/z42_host.h) 写好，`build.sh` 自动 build + link + 跑 + assert。

`hello_c` 与 [`hello_rust/`](../hello_rust/) 完全等价：同样 hello-world 流程、同样断言，选 Rust 还是 C 看宿主语言。两者的输出都是 `[host] hello, world`。

## Run

```sh
./build.sh
```

输出尾部：

```
running: out/hello_c out/hello.zbc artifacts/build/libs/release
stdout: [host] hello, world

✅ hello_c end-to-end OK
```

`build.sh` 自动：

1. cargo rustc 产 `libz42.a`（`--crate-type=staticlib`，与 rlib coexist in same target dir）
2. 解析 `cargo rustc --print=native-static-libs`，得到平台 native libs 列表（macOS arm64：`-liconv -lSystem -lc -lm`）
3. 编 `examples/embedding/hello.z42 → out/hello.zbc`（与 iOS XCTest 共享同一份 fixture）
4. `cc main.c -lz42 + native libs → out/hello_c`
5. 跑 binary，assert stdout

依赖：`cc` / `cargo` / `dotnet`。如果 stdlib zpkgs 未就绪，build.sh 自动调 `./scripts/build-stdlib.sh`。

## Why 主线 build 不带 staticlib

`src/runtime/Cargo.toml` `[lib]` 默认 rlib only —— `z42-test-runner` / `z42-host` / iOS / Android 都通过 `path =` 引用 z42 crate 作为 Rust 依赖，主流场景 rlib 够用。本 spec 在 `build.sh` 内通过 `cargo rustc --release --lib --crate-type=staticlib` 显式 emit 一次 `libz42.a`，与 rlib coexist；不动主 Cargo.toml `[lib]` 配置。

## 与 hello_rust 的关系

[`hello_rust/`](../hello_rust/) 用 Tier 2 `z42-host` Rust crate（人因工程友好）；`hello_c` 走 Tier 1 raw C ABI（zero-Rust 宿主用）。两份示例的 stdout 一致，但 `hello_c` 是 mobile facade（Swift / Kotlin）背后实际调用的同一份 API surface 的"原始 C 调用对照"。

## CI

本 spec 不把 `hello_c` 加入 `./scripts/test-all.sh` 默认 GREEN 路径（`hello_c` 是 example，不是 critical infrastructure；用户按需手动跑）。后续若需 CI 自动跑，新建 `add-hello-c-to-test-all` spec。
