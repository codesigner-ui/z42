# WASM facade — build & run

> 🟢 已落地 · facade [`platforms/wasm/`](../../../src/toolchain/host/platforms/wasm/) · spec [`2026-05-12-add-platform-wasm/`](../../spec/archive/2026-05-12-add-platform-wasm/)

把 z42 VM 编进 WebAssembly，让 JS / TS 宿主 `import` 跑 `.zbc`。**从零开始按下面 5 步走**（Step 4 / Step 5 二选一即可）。

## Step 1 — Install toolchain（一次性）

```bash
rustup target add wasm32-unknown-unknown
cargo install wasm-pack --locked
```

❗ Rust 1.88+ 必须加 `--locked`，否则 `cargo-platform` 版本冲突。

可选 demo 工具：

- **浏览器 demo**（推荐）：需要一个能正确发送
  `application/wasm` / `text/javascript` MIME 的静态服务器。三选一即可：
  - **Rust（默认）** — 与 z42 toolchain 一致：
    ```bash
    cargo install miniserve --locked
    ```
  - **.NET** — Step 2 已经装了 `dotnet`，复用即可（一次性装 global tool）：
    ```bash
    dotnet tool install -g dotnet-serve
    ```
  - **Python 3** — 多数 macOS / Linux 开发机自带，零安装：
    ```bash
    python3 -m http.server 8000     # 自带正确 .wasm MIME（Python 3.7+）
    ```
  其他可行替代（自行选用，本文档不展开）：`npx serve`、`caddy file-server`、`busybox httpd` 等。
- **Node demo**：
  ```bash
  brew install node          # macOS；其他平台用任意 ≥ 18 发行版
  ```

## Step 2 — Build compiler + stdlib（一次性 / 改 stdlib 后重跑）

```bash
dotnet build src/compiler/z42.slnx
./xtask build stdlib
```

✅ 产出 `artifacts/build/compiler/z42.Driver/bin/z42c.dll`。stdlib zpkg 由 `./xtask build stdlib` 产到 `artifacts/build/libraries/dist/release/*.zpkg`。

❗ `dotnet: command not found` → 装 .NET 10+：https://dotnet.microsoft.com/download

## Step 3 — Build the WASM facade

```bash
./xtask test platform wasm build     # wasm-pack web+nodejs → pkg-web/ pkg-nodejs/
./xtask test platform wasm assets    # fixtures + stdlib + files.json
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

## Step 4 — Run the browser demo（可选 · 无需 Node）

在 `src/toolchain/host/platforms/wasm/` 起一个静态服务器，把同目录的
`pkg-web/`、`js/stdlib/`、`demo/` 整个目录暴露出去，浏览器打开
`demo/web/index.html`。

```bash
cd src/toolchain/host/platforms/wasm
miniserve --index demo/web/index.html .          # 默认 8080，根路径直接落 index
# 或：dotnet serve -p 8000                        # 路径 /demo/web/index.html
# 或：python3 -m http.server 8000                 # 路径 /demo/web/index.html
```

然后浏览器访问 `http://127.0.0.1:<port>/`（miniserve）或
`http://127.0.0.1:<port>/demo/web/index.html`（dotnet serve / Python）。

✅ 期望：页面状态条变成 **OK**，日志区出现 `[host] Hello, World!`。

❗ DevTools 看到 `Failed to load module` / MIME 报错 → 服务器没有给 `.wasm`
   送 `application/wasm`、或给 `.js` 送 `text/javascript`；换 miniserve /
   dotnet-serve / Python 3.7+ 三者中的任意一个均可。
❗ `Z42VMError: undefined function ...` → `js/stdlib/` 空；重跑 Step 3。
❗ `file://...` 直接打开 index.html 会失败（CORS + 不能 fetch wasm）。**必须** 走 HTTP。

## Step 5 — Run the Node demo（可选 · 装了 Node 才用）

```bash
node demo/node/run.js
```

✅ 期望：`[host] Hello, World!`

❗ `Z42VMError: undefined function ...` → `js/stdlib/` 空；重跑 Step 3。

---

**See also**

- **本地打 browser-wasm SDK package**（自包含 staticlib + cdylib + wasm-bindgen 双 target + npm `package.json`）：[`../packaging.md`](../packaging.md) — `./xtask package release --rid browser-wasm`
- JS / TS API + 错误码：[`platforms/wasm/README.md`](../../../src/toolchain/host/platforms/wasm/README.md)
- 跨平台契约：[`platforms/README.md`](../../../src/toolchain/host/platforms/README.md)
- 设计 + 决策：[spec archive](../../spec/archive/2026-05-12-add-platform-wasm/)
