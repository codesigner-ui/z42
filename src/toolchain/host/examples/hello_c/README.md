# hello_c — Embedding API C reference example

## Status

🔵 **Reference source only**（H2b）— `main.c` 已经按 [`z42_host.h`](../../../../runtime/include/z42_host.h) 写好；但**桌面端实际链接 + 跑通**留给 H4 移动平台 spec（`add-platform-ios` / `add-platform-android`）。

## 为什么 H2b 不直接 build

z42 runtime 当前只产出 `rlib`（`src/runtime/Cargo.toml` `[lib]` 没有 `crate-type`）。把 C 程序链到 Rust 库需要 `staticlib` 或 `cdylib`：

- 加 `crate-type = ["staticlib", "rlib"]` → 出 `libz42_vm.a`（~50–80 MB debug）
- 但还要把 Rust libstd / panic / allocator 全部静态链入，命令行需要 `cargo rustc -- --print=native-static-libs` 列出的几十个系统库
- iOS / Android 已经计划在自家 spec（`platform/ios/rust/`、`platform/android/rust/`）下做这套构建配置；桌面再做一份会重复

H4 进入时会一并把 `cdylib` / `staticlib` build pipeline 跑通，届时 `main.c` 直接复用，不改一行。

## hello_c 现在能做什么

- 作为 C ABI 用法的**正确性参考**（与 [`hello_rust`](../hello_rust/) 完全等价）
- 头文件依赖只有 `z42_abi.h` 和 `z42_host.h`，可以编译验证语法（不链接）：

  ```sh
  gcc -I /Users/d.s.qiu/Documents/codesigner-ui/z42/src/runtime/include \
      -fsyntax-only main.c
  ```

- 给后续 `add-platform-ios` / `add-platform-android` spec 提供"原始 C 调用面"作为 Swift / Kotlin facade 的对齐目标

## H4 完成后预期的 build 流程（参考）

```sh
# 1. 生成 staticlib
cargo build --manifest-path src/runtime/Cargo.toml --release \
    --features staticlib

# 2. 链接（系统库列表来自 cargo rustc --print=native-static-libs）
gcc -I src/runtime/include \
    -o hello_c main.c \
    -L artifacts/rust/release -lz42_vm \
    -lSystem -lresolv -lc -lm -ldl -lpthread       # macOS 例子

# 3. 跑
./hello_c /tmp/embedding_hello.zbc artifacts/z42/libs
```

预期输出：

```
[host] Hello, World!
```

## 与 hello_rust 的关系

[`hello_rust/`](../hello_rust/) 是**当前可跑通**的等价示例（用 Tier 2 `z42-host` crate）。两份示例的输出和断言完全一致；选 Rust / C 看你的宿主语言。
