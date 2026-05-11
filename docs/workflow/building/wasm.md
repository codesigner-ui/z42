# WASM facade — build & run

> 🟢 已落地 · facade [`platforms/wasm/`](../../../src/toolchain/host/platforms/wasm/) · spec [`2026-05-12-add-platform-wasm/`](../../spec/archive/2026-05-12-add-platform-wasm/)

把 z42 VM 编进 WebAssembly，让 JS / TS 宿主 `import` 跑 `.zbc`。**从零开始按下面 4 步走**。

## Step 1 — Install toolchain（一次性）

```bash
rustup target add wasm32-unknown-unknown
cargo install wasm-pack --locked
```

❗ Rust 1.88+ 必须加 `--locked`，否则 `cargo-platform` 版本冲突。

Node demo 可选（不跑 demo 可跳过）：

```bash
brew install node          # macOS；其他平台用任意 ≥ 18 发行版
```

## Step 2 — Build compiler + stdlib（一次性 / 改 stdlib 后重跑）

```bash
dotnet build src/compiler/z42.slnx
```

✅ 产出 `artifacts/compiler/z42.Driver/bin/z42c.dll` + `artifacts/z42/libs/*.zpkg`。

❗ `dotnet: command not found` → 装 .NET 8+：https://dotnet.microsoft.com/download

## Step 3 — Build the WASM facade

```bash
cd src/toolchain/host/platforms/wasm
./build.sh
```

✅ 末尾应看到：

```
built:
  .../pkg-web/
  .../pkg-nodejs/
  .../js/stdlib/
```

❗ `wasm-pack: command not found` → Step 1 漏装。
❗ `fixture missing: hello.zbc` 或 `stdlib libs dir not found` → Step 2 漏跑。

## Step 4 — Run the Node demo（可选）

```bash
node demo/node/run.js
```

✅ 期望：`[host] Hello, World!`

❗ `Z42VMError: undefined function ...` → `js/stdlib/` 空；重跑 Step 3。

---

**See also**

- JS / TS API + 错误码：[`platforms/wasm/README.md`](../../../src/toolchain/host/platforms/wasm/README.md)
- 跨平台契约：[`platforms/README.md`](../../../src/toolchain/host/platforms/README.md)
- 设计 + 决策：[spec archive](../../spec/archive/2026-05-12-add-platform-wasm/)
