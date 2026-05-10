# hello_rust — Embedding API Rust reference example

Demonstrates the Tier 2 [`z42-host`](../../embed/) crate by loading a
hello-world `.zbc` and routing its `Console.WriteLine` output through a
host-supplied stdout sink.

Spec: [`docs/design/runtime/embedding.md §9.3`](../../../../../docs/design/runtime/embedding.md).

## 一次性准备

需要先有 z42 编译器和 stdlib zpkg：

```sh
# 在仓库根：
dotnet build src/compiler/z42.slnx
```

## 编译 fixture

```sh
dotnet artifacts/compiler/z42.Driver/bin/z42c.dll \
    src/runtime/tests/data/embedding_hello/source.z42 \
    --emit zbc -o /tmp/embedding_hello.zbc
```

## 跑

```sh
cargo run --manifest-path src/toolchain/host/examples/hello_rust/Cargo.toml -- \
    /tmp/embedding_hello.zbc artifacts/z42/libs
```

## 期望输出

```
[host] Hello, World!
```

`[host]` 前缀来自宿主侧的 stdout sink，证明输出**走 host 回调**而不是 runtime 默认 stdout —— 也就是嵌入 API 控制了 IO。

## 看哪几行最关键

[`src/main.rs`](src/main.rs)：

```rust
let cfg = HostConfig {
    exec_mode: ExecMode::Interp,
    stdout: Some(Box::new(move |bytes: &[u8]| { /* 拼 [host] 前缀 */ })),
    search_paths: vec![libs_dir],
    ..Default::default()
};
let host  = Host::new(cfg)?;
let module = host.load_zbc_path(&zbc_path)?;
let entry  = host.resolve_entry(&module, "Embedding.Hello.Main")?;
host.invoke(&entry, &[])?;
```

五行覆盖整个 lifecycle。`Host` 的 `Drop` 自动调 `z42_host_shutdown`。

## 同等的 C 实现

见 [`../hello_c/`](../hello_c/) — 写法一致，desktop build pipeline 等 H4 移动平台 spec 一并落地。
