# Design: runtime 动态加载 + 静态调用

## Architecture

```
launcher(z42, 跑在 VM 内)
  │  Runtime.LoadZpkg("z42.workload.ios")          ← 逻辑名，不含路径
  ▼
[builtin] __load_zpkg(name)
  │
  ├─ 1. 已加载缓存命中？→ no-op（幂等）
  │
  ├─ 2. Apphost ZpkgResolver（若注册）
  │     Desktop launcher : FileSystemResolver → $Z42_HOME/.../<name>.zpkg
  │     Android apphost  : AssetResolver      → AssetManager.open(<name>.zpkg)
  │     iOS apphost      : BundleResolver     → Bundle.path(forResource:<name>)
  │     (均由 native 层在 VM 初始化时通过 z42_host API 注入)
  │
  └─ 3. 默认 filesystem 优先级搜索（未注册 Resolver 时）
        .z42/workloads/<name>.zpkg             项目本地（最高）
        $Z42_HOME/runtimes/<ver>/workloads/<name>/  版本作用域 workload 目录
        $Z42_HOME/workloads/<name>.zpkg        全局 fallback
  │
  ▼   解析到 bytes → ZpkgCandidate → LazyLoader.declared_zpkgs.insert + load_zpkg_file
      (类型/函数注册进全局表；transitive deps 自动 re-seed；幂等)

launcher
  │  int rc = Runtime.CallStatic("Z42.Workload.Ios.IosExport.Run", args)
  ▼
[builtin] __call_static(fqn, string[])
  │   try_lookup_function(fqn) → Arc<Function>（懒加载触发）
  │   校验签名 (string[])->int（严格，任一不符 → RuntimeException）
  │   重入 VM：以单个 string[] 实参建帧执行（interp/JIT 按当前后端）
  ▼   取 int 返回 → 透传给 launcher
```

复用既有(勘察确认):
- `metadata/lazy_loader.rs:277 load_zpkg_file` + `declared_zpkgs` / `loaded_zpkgs`(运行时加载已具备,只缺"任意路径声明"入口)。
- `vm_context.rs:1140 try_lookup_function` → `lazy_loader.rs:139 resolve_function`(按 FQN 解析,含懒加载 + first-wins)。
- `loader.rs:801 try_fixup_inheritance`(跨 zpkg 继承/接口修复)。
- `crates/z42-host`(原 host-api)的 native→z42 entry 调用路径(host 跑 entry)→ 推广为 builtin 重入。

## Decisions

### Decision 1: 静态函数约定,不用对象/接口/Invoke
**问题**:launcher 要调用运行时才知名字的 workload 入口。
**选项**:A 反射 `Invoke`(通用 marshaling,难,0.5.x);B 建对象 + cast baked 接口 + 虚调用(需 `__create_instance` + 接口 vtable 修复);C 约定固定签名静态函数 + 按名调用。
**决定**:**C**。约定 `<FQN>(string[])->int`,builtin 签名固定 → 无通用 marshaling、无对象、无接口。最少 VM 改动。

### Decision 2: `__load_zpkg` 声明 + 即时加载
**问题**:登记后懒加载,还是即时加载?
**决定**:`__load_zpkg` 登记进 `declared_zpkgs` **并立即 `load_zpkg_file`**(类型/函数即刻注册),确定性更好;后续 `__call_static` 的 `try_lookup_function` 命中已加载表。幂等:已加载 → no-op。

### Decision 3: 固定签名 `(string[])->int`,严格校验
**问题**:`__call_static` 解析到的函数签名不符怎么办?
**决定**:严格——非"1 个 string[] 参数 + int 返回 + static" → 抛 `RuntimeException`(清晰报错)。规避通用 marshaling 的前提就是签名固定。

### Decision 4: packed 自包含优先
**问题**:workload zpkg 的依赖怎么满足?
**决定**:阶段 1 先要求 **packed 自包含**(deps 内联),`__load_zpkg` 零 dep 解析。transitive dep 懒链接(`lazy_loader` 已支持 re-seed)留作可选增强,不在本变更验证。

### Decision 5: ZpkgResolver — 平台透明的逻辑名解析（2026-06-19）
**问题**:`LoadZpkg` 的参数若是绝对文件系统路径，在 Android（APK assets / OBB）等移动平台上无法通用——asset 不在普通文件系统路径下。
**选项**:
- A: scheme URI（`asset://`, `bundle://`）— z42 代码需感知平台，每个平台写法不同。
- B: 逻辑名 + apphost 注册 Resolver（inversion of control）— z42 代码只写包名，平台差异全部下沉到 native 层。
- C: bytes-based `LoadZpkgBytes(byte[])` — 平台无关但把 IO 推给 z42 调用方。

**决定**:**B**。`LoadZpkg` 参数统一为**逻辑包名**（如 `"z42.workload.ios"`），VM builtin 持有一个可选的 `ZpkgResolver` 钩子。

解析优先级：
1. 已加载缓存（幂等）
2. Apphost 注册的 `ZpkgResolver`（平台特定：filesystem / AssetManager / Bundle）
3. 默认 filesystem 优先级搜索（项目本地 → 版本作用域 → 全局，各找 `<name>.zpkg`）

Resolver 是 Rust trait（`z42_host` crate），由 apphost 在 `VmBuilder` 初始化时注入：
```rust
// desktop launcher (default):
vm.set_zpkg_resolver(FileSystemResolver::new(search_paths));

// android:
vm.set_zpkg_resolver(AssetResolver::new(asset_manager));

// ios:
vm.set_zpkg_resolver(BundleResolver::new(bundle_path));
```
未注册时走默认 filesystem 搜索，向后兼容。z42 代码跨平台一致。

## Implementation Notes
- **builtin 重入 VM(关键)**:`__call_static` 在一个正在执行的 z42 程序(launcher)内,需重入解释器/JIT 执行另一个函数并取返回。必须确认/实现"从 builtin 嵌套调用 z42 函数"路径——host-api 的 native→z42 是顶层版本,这里是 mid-execution 嵌套版本。**实施第一步先验证该重入可行**(否则方案需调整)。
- **JIT 一致性**:被调函数可能被 JIT;重入须走标准后端分发,interp+JIT 两路都覆盖(`vm-jit-consistency`)。
- **GC**:args 数组 + 被调函数对象同堆,正常 GC;重入帧纳入栈根。
- **错误传播**:被调函数抛异常 → 同 VM,沿重入边界传回 launcher(可 catch)。
- **格式**:纯 runtime builtin + stdlib extern,**无 zbc/zpkg 格式 bump**(不改二进制格式)。需确认 z42c 能编新 stdlib(自举同步)。

## Testing Strategy
- **Rust 单测**:`declare_from_path`(登记+加载一个测试 zpkg);`__call_static` 签名校验(正确签名调用成功、错误签名报错);幂等加载。
- **VM e2e(golden)**:`src/tests/dynamic-load-call/load_call/` —— 一个 main zpkg `__load_zpkg` 另一个测试 zpkg、`__call_static` 调其 `Foo.Run(string[])->int` 传参取返回;**interp + JIT 结果一致**。
- **GREEN**:`z42 xtask.zpkg test`(含 vm interp+jit / cross-zpkg);Rust `cargo test`(runtime 单测,见 memory:改 runtime 必跑 cargo test)。
