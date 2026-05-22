# Design: z42 WASM playground

## Architecture

```
┌─────────────────────── browser ──────────────────────┐
│  Monaco editor       <user types source>             │
│       │                                              │
│       │ POST /compile                                │
│       ▼                                              │
│  fetch(server) ──────────► ASP.NET minimal API       │
│                                  │                   │
│                                  ▼                   │
│                            PlaygroundCompiler        │
│                                  .CompileSource(...) │
│                                  │ (in-process)      │
│                                  ▼                   │
│                            z42c → zbc bytes          │
│       ◄────────── zbc bytes ─────┘                   │
│       │                                              │
│       ▼                                              │
│  z42-wasm.wasm  (wasm-bindgen wrapper)               │
│       │                                              │
│       ├─ run_zbc(zbc_bytes, entry, [stdlib_zpkgs])   │
│       │      │                                       │
│       │      ▼                                       │
│       │   z42vm interp loop (Rust → wasm32)          │
│       │      │                                       │
│       │      ├─ Console.WriteLine → JS callback ──┐  │
│       │      └─ ConsoleError.WriteLine → JS cb   ─┤  │
│       │                                            │ │
│       ▼                                            ▼ │
│  Output panel  ◄──────────────────────  events  ──┘  │
└──────────────────────────────────────────────────────┘
                                ▲
                                │ (cold fetch, cached)
                                │
              [stdlib zpkgs as static files at /libs/*.zpkg]
              (served by playground server or static CDN)
```

**Cold-start sequence**:
1. Browser loads HTML + Monaco JS bundle (~1.5MB gz)
2. Browser fetches `z42-wasm.wasm` (~3MB unoptimized, target ~1MB after wasm-opt)
3. Browser fetches 16 stdlib zpkgs in parallel (~340KB total → ~120KB gz)
4. Editor ready, user types, hits Run
5. UI POSTs source to `/compile`, gets zbc bytes back (~50-100ms LAN, ~200ms WAN
   — in-process call, no subprocess spawn overhead)
6. UI calls `wasm.run_zbc(zbc, "Main", cachedStdlibZpkgs)` → returns
   `{stdout: string, stderr: string, exitCode: number, durationMs: number}`
7. Output panel displays

After cold start, every subsequent Run is just `/compile` API + WASM call —
no re-fetches.

## Decisions

### Decision 1: 架构 A (Blazor 全前端) vs B (z42c 留 server)

**Choice**: B (hybrid)，**in-process compile API**（不 fork subprocess）

**Reasoning**:
- B 复用现有 z42c 代码（不需要新 Rust 编译器实现）
- B 的 server 部署成本（一个 .NET binary 或 docker container）远低于 A 的
  Blazor 工程成本
- **in-process** vs subprocess：选 in-process。把 `Z42.Compiler.PlaygroundCompiler.
  CompileSource(...)` 做成 in-memory API 是两条路（B / Blazor）的共享基础 ——
  现在做掉一并解决未来 Blazor fallback 的最大前置工作；同时省 50-200ms 的
  subprocess spawn 开销 + 不需要 tempfile dance
- A 等真有强离线诉求时再做 —— 把同一个 `PlaygroundCompiler` 通过 `[JSInvokable]`
  暴露给 Blazor JS interop 即可，前端代码零改动（同一个 compile JS API）

#### 未来加 Blazor fallback：Interp vs AOT 两种模式

Blazor WebAssembly 有两种部署模式，下表对照（z42c 量级估计）：

| 维度 | Interp（默认） | AOT (`RunAOTCompilation=true`) | 当前架构 B |
|------|--------------|------------------------------|---------|
| 浏览器拿到 | 原始 .NET IL dll | 每方法预编 WASM bytecode | 无（编译走 server） |
| Cold download | **~5-8MB**（dll + Mono runtime ~3MB） | ~10-15MB（每方法膨胀 3-5×） | ~3MB（仅 vm wasm） |
| Compile latency | 500ms-2s（Mono interp 比 native 慢 5-10×） | **50-150ms**（接近 native） | 50-500ms（含网络 RTT） |
| Build 复杂度 | 低（标准 `dotnet publish`） | 高（需 emscripten toolchain；首次 publish 5-10min） | 极低 |
| 离线 | ✅ | ✅ | ❌ |
| 后端 | $0 | $0 | ~$5-20/mo VPS |

**Fallback 实施建议（不在本 spec scope）**：
1. 先 ship **Interp** 模式 —— build 简单 download 小，500ms-2s 编译延迟对教学
   / docs snippet 场景 acceptable
2. 用户反馈编译慢 → 切 **AOT** —— csproj 加 `<RunAOTCompilation>true</RunAOTCompilation>` + 重 publish；前端代码零改动（同一个 `CompileSource` JS interop 接口）
3. 混合模式（部分方法 AOT、其余 IL + Jiterpreter）对编译器代码（热路径分散）
   收益有限，**不优先尝试**

**Blazor 前置工作 = 架构 B 的 Phase 1（本 spec 已纳入）**：

```csharp
namespace Z42.Compiler;
public static class PlaygroundCompiler {
    /// 编译单文件 source，stdlib zpkgs 由 caller 传入（避免 walk fs）。
    /// 同 API 给 server（in-process call）和 Blazor（JS interop call）共用。
    public static CompileResult CompileSource(
        string source,
        IReadOnlyDictionary<string, byte[]> stdlibZpkgs
    );
}
public sealed record CompileResult(
    byte[]? ZbcBytes,
    IReadOnlyList<Diagnostic> Diagnostics
);
```

`PipelineCore.CheckAndGenerate` 已经接 `CompilationUnit`（非 file path），主要
改动在 wrapper 层（`SingleFileCompiler.Run` / `LocateDepIndex` / `LocateImportedSymbols`
的 fs-walk 路径）。预估 1-2 天 —— 当前 spec Phase 1 一并做掉。

### Decision 2: WASM wrapper 写在哪

**Choice**: `src/toolchain/playground/wasm/` 作 sibling crate（不动 `src/runtime/`）

**Reasoning**:
- z42 runtime crate 不应该 import wasm-bindgen（污染 native build deps）
- 独立 sibling crate `z42-wasm` 单独 cargo workspace member，只在 wasm 模式
  build；其 deps = z42 runtime + wasm-bindgen + console_error_panic_hook
- 同 `src/runtime/crates/z42-abi`, `z42-rs`, `z42-macros` 模式（已有 sibling
  crate 约定）。但放 `src/toolchain/playground/wasm/` 因为它是 playground
  专属，不是 runtime 通用 binding

### Decision 3: Stdlib zpkgs 怎么进浏览器

**Choice**: 16 个 zpkg 作 static 静态文件，浏览器并发 fetch + IndexedDB 缓存

**Reasoning**:
- v0 全 bundle 简单：build 时 cp 16 个 zpkg 到 `web/public/libs/`，浏览器
  fetch 后 cache 到 IndexedDB（Service Worker），后续启动从 cache 读
- 替代方案"嵌入 JS bundle 作 base64"会让 JS 变 ~500KB，加载更慢
- lazy load（只 fetch 用到的 zpkg）需要 dependency 分析 → follow-up

### Decision 4: stdout/stderr 流式 vs batch

**Choice**: Batch（run 完一次 dump）

**Reasoning**:
- 流式需要 wasm-bindgen 把 callback 暴露给 Rust，async path 复杂
- v0 batch 简单：vm 跑完 console buffer 一次性返回；UI 一次性 render
- 长运行 script 看不到中间输出 —— 加 10s timeout 杜绝；流式留 follow-up

### Decision 5: 错误显示

**Choice**: 三个区分：syntax error（来自 /compile API 4xx）、runtime error（VM
返回 stderr + exitCode != 0）、playground 内部错（fetch 失败 / wasm crash）

**Reasoning**:
- 用户视角三类错应该不同 UI 提示：syntax 高亮 line + col；runtime 显示
  stderr；playground 错给 "请刷新或报 bug"
- compile API 返回 4xx + JSON `{line, col, message}` 便于前端定位

### Decision 6: rate limit + abuse 防护（in-process API 模式）

**Choice**: ASP.NET 端 token-bucket per IP（1 req/s burst 5），CompileSource 调用
用 `CancellationToken` 5s timeout，**进程级**（不是 subprocess）：

- **Rate limit**：`Microsoft.AspNetCore.RateLimiting`（.NET 7+ 内置）token-bucket
- **Per-request timeout**：`CompileSource(...)` 接 `CancellationToken`，请求超 5s 取消，pipeline 协作式 cancellation
- **Source size limit**：request body 上限 256KB（malicious bomb 防护）
- **Output size limit**：zbc 大小 > 1MB 直接退 4xx（无意义 + 浏览器保护）
- **Resource isolation**：编译器 in-process 跑，无 OS-level 隔离 → 容器层做（docker memory limit / cpu shares）

**Trade-off**：in-process 没有 subprocess crash 隔离 —— compiler bug 触发的
panic 会带下 server。Mitigation：
- ASP.NET `IExceptionHandler` middleware catch + 报 5xx，**进程不死**
- 进程级 health check 定期重启（systemd / docker restart policy）
- compiler unit tests 覆盖率高 + parser fuzz testing（已有）保证 panic 罕见

### Decision 7: 安全模型

**Choice**: 假定 untrusted user code

**Reasoning**:
- 编译时：z42c 在 .NET server 进程内 in-process 跑，`PlaygroundCompiler.CompileSource`
  是**纯函数**（接收 source + stdlib zpkgs byte[]，返回 zbc bytes 或 diagnostics）
  —— 无文件系统 / 网络访问。容器层（docker / cgroups）额外限 memory + cpu
- 运行时：vm 在浏览器 wasm sandbox 里跑 —— wasm 本身就是 sandbox，浏览器
  外没影响。z42.io.File / Process / Directory 等 syscall 在 wasm feature 下
  graceful fail
- 用户不能用 playground 攻击 server（compile 是无副作用的 parse + emit；panic
  被 middleware 接住转 5xx，进程不挂；resource 上限由 .NET CancellationToken
  + ASP.NET request body limit 双层保证）或别人浏览器（wasm sandbox）

## Implementation Notes

### z42-wasm crate（Phase 1）

入口 API：

```rust
// src/toolchain/playground/wasm/src/lib.rs
use wasm_bindgen::prelude::*;

#[wasm_bindgen]
pub struct PlaygroundVm { /* VmContext etc. */ }

#[wasm_bindgen]
impl PlaygroundVm {
    /// Load stdlib zpkgs (parallel arrays — `names` + `bytes`).
    #[wasm_bindgen(constructor)]
    pub fn new(stdlib_zpkgs: js_sys::Array) -> Self { /* ... */ }

    /// Compile + run a single .z42 source. Returns ExecResult.
    /// Throws on internal panic; user-level errors come back as
    /// `result.stderr` + non-zero `result.exit_code`.
    pub fn run_zbc(
        &mut self,
        zbc_bytes: js_sys::Uint8Array,
        entry: &str,
        timeout_ms: u32,
    ) -> Result<JsValue, JsValue> { /* ... */ }
}

#[derive(serde::Serialize)]
struct ExecResult {
    stdout:      String,
    stderr:      String,
    exit_code:   i32,
    duration_ms: u32,
}
```

Build：`wasm-pack build --target web --release`. Output:
`pkg/z42_wasm_bg.wasm` + `pkg/z42_wasm.js` (glue).

### PlaygroundCompiler in-memory API（Phase 1，新增）

Phase 1 把 z42c 重构出 in-memory entry，供 server (Phase 2) **以及未来的 Blazor
fallback** 共用：

```csharp
// src/compiler/z42.Compiler/PlaygroundCompiler.cs
namespace Z42.Compiler;
public static class PlaygroundCompiler {
    public static CompileResult CompileSource(
        string source,
        IReadOnlyDictionary<string, byte[]> stdlibZpkgs,
        CancellationToken cancellationToken = default
    );
}

public sealed record CompileResult(
    byte[]? ZbcBytes,                      // null 表示编译失败
    IReadOnlyList<Diagnostic> Diagnostics, // 含 line/col/message
    long CompileTimeMs
);
```

实现要点：
- 复用 `PipelineCore.CheckAndGenerate(CompilationUnit, ...)`（已 fs-free）
- 替换 `SingleFileCompiler` 的 fs-walk：传入的 `stdlibZpkgs: Dict<string,byte[]>`
  直接 feed 给 TSIG cache + DepIndex builder（不再 `Directory.EnumerateFiles`）
- z42c CLI 留 binary 形态，内部改为薄 wrapper（read file → CompileSource → write file）

### Server project（Phase 2）

ASP.NET Core minimal API + in-process compile：

```csharp
// src/toolchain/playground/server/Program.cs
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRateLimiter(...);  // 1 req/s per IP
builder.Services.AddCors(...);
var app = builder.Build();

app.MapPost("/compile", async (CompileRequest req, CancellationToken ct) => {
    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
    var result = PlaygroundCompiler.CompileSource(req.Source, _stdlibZpkgs, timeoutCts.Token);
    return result.ZbcBytes != null
        ? Results.Json(new { zbc = Convert.ToBase64String(result.ZbcBytes), durationMs = result.CompileTimeMs })
        : Results.BadRequest(new { diagnostics = result.Diagnostics });
});

app.MapGet("/libs/{name}.zpkg", (string name) => ...);  // static stdlib

app.UseRateLimiter();
app.UseCors();
app.UseExceptionHandler();  // catch panic → 5xx，进程不挂
app.Run();
```

启动时 load 一次 stdlib zpkgs 到 `_stdlibZpkgs` 静态字典，请求间复用（in-process
shared state，不需重新读盘）。

### Web frontend（Phase 3）

Vite + TS + Monaco：

```typescript
// src/toolchain/playground/web/src/main.ts
import init, { PlaygroundVm } from '../wasm/pkg/z42_wasm.js';

await init();  // load .wasm
const zpkgs = await fetchStdlibZpkgs();  // 16 parallel fetches + IDB cache
const vm = new PlaygroundVm(zpkgs);

editor.onRun(async (source: string) => {
    const zbc = await fetchCompile(source);  // POST /compile
    const result = vm.run_zbc(zbc, 'Main', 10000);
    output.render(result.stdout, result.stderr, result.exit_code);
});
```

### Monaco z42 language（Phase 4）

Token-level rules：keyword set, literal strings, comments, identifiers.
Mostly mechanical — port the z42 keyword list from the lexer:

```typescript
monaco.languages.register({ id: 'z42' });
monaco.languages.setMonarchTokensProvider('z42', {
    keywords: ['namespace', 'using', 'class', 'public', /* ... ~60 keywords ... */],
    tokenizer: { /* monarch rules */ },
});
```

## Testing Strategy

- **z42-wasm**: cargo test in wasm-pack mode + manual smoke (load + run hello world)
- **server**: integration test — boot server + curl `/compile` with hello world + expect 200 + valid zbc
- **web**: Playwright smoke — load page + type source + hit Run + assert output text
- **end-to-end**: `scripts/build-playground.sh` produces 3 artifacts (wasm pkg, server binary, web dist)，本地 `npm run dev` + curl 测端到端

不打算把 playground 加入 `test-all.sh`（额外 ~30s 的 wasm-pack build 不值），
单独 ./scripts/test-playground.sh 手动跑。
