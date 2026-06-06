# Design: 以 zpkg NSPC 自描述取代 index.json

## Architecture

唯一原则：**namespace（及包名）归属由 zpkg 的 `NSPC`/`META` section 权威表达；任何
`namespace → 文件/字节` 映射只能扫这些 section 派生，不再手维护 `index.json`。**

嵌入式加载链分两层、互不相干（用户来信澄清）：
- **加载 hook**（保留）：宿主→运行时提供 zpkg 字节的回调（`ZpkgResolver::resolve(ns) → bytes`，
  C `Z42ZpkgResolverFn`）。桌面扫 `search_paths`、移动/wasm 由平台 resolver 或宿主提供。
- **namespace 归属**（本变更改造）：从"读 index.json"改为"读 zpkg 的 NSPC"。

```
runtime build_host_module: for ns in ["z42.core"] + imports:
    resolver.resolve(ns) → bytes        ← hook 不变
        resolver 内部: namespace → bytes 表由"扫 zpkg + 读 NSPC"派生（不再读 index.json）
    或 corelib / search_paths (NSPC 扫描)
```

## Decisions

### D1: 保留加载 hook
**问题**：是否随 index.json 一起删掉 `ZpkgResolver` 回调？
**决定**：**保留**。hook 是嵌入式（web playground / 移动 REPL / 网络加载）提供字节的必要机制，
与 index.json（namespace 映射）正交。

### D2: 新增 `z42_zpkg_read_namespaces` helper
**决定**：C ABI `z42_zpkg_read_namespaces(bytes, len, visit, user_data)`，visitor 回调每个
**解析键**。复用 Rust `read_zpkg_meta`（已存在），暴露 wasm-bindgen `readNamespaces` + Android
JNI `Z42VM.readNamespaces`。让 Swift/Kotlin/JS 不重写 zpkg 二进制解析。

### D3: 解析键 = 包名 + namespace（关键）
**问题**：helper 只返回 NSPC namespace 够吗？
**实测**：`z42.core.zpkg` 的 NSPC = `[Std, Std.Collections]`，**不含 `z42.core`**。但运行时请求
prelude 是按**包名** `z42.core`。旧 index.json 同时含包名键 + namespace 键。
**决定**：helper 返回 **`META.name`（包名）+ 每个 NSPC namespace**。resolver 据此建表，
`resolve("z42.core")`（prelude）与 `resolve("Std.IO")`（import）都命中。

### D4: 浏览器枚举替身
**问题**：浏览器经 HTTP 无法 `readdir`。
**决定**：`build.sh` 拷 zpkg 时顺手 `ls` 出 `files.json`（纯文件名数组，派生、零手维护、**非**
namespace 映射）；浏览器 fetch 它 → 逐个 fetch zpkg → 读 NSPC 建表。Node `readdir` 直接枚举。

### D5: 不新增注入 API
"主动注入"（web playground/REPL）用现有 `MapResolver`（宿主读 NSPC 填表）经 hook 提供，
无需 `z42_host_add_zpkg`。

## Implementation Notes
- `ZpkgInfo{ name, namespaces, ... }` 由 `read_zpkg_meta` 返回；helper visit `name` 后 visit 每个 `namespaces`。
- visitor 的 `ns` 是 `len` 字节 UTF-8（无 NUL），仅调用期有效，host 须立即拷贝。
- Android JNI：visitor JNIEnv-free（收集 C 串），调用后再建 `String[]`。
- 平台 resolver 枚举按文件名排序（确定性，common-pitfalls §1）。

## Testing Strategy
- Rust 单元（host_tests.rs）：`z42_zpkg_read_namespaces` 对真实 z42.core.zpkg 返回 `z42.core` + `Std`；garbage → BadZbc；null visitor → BadConfig。
- 回归：删 `dist/index.json` 后 host 测试仍全绿（证明运行时不依赖 index.json）。
- GREEN：`cargo test`（绿）；C# `dotnet test`（绿）；mobile/wasm facade 构建 = CI（本机无 Xcode/NDK/wasm-pack）。
