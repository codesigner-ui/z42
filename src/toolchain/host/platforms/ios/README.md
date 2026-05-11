# Z42VM — iOS facade

> 🟢 H4 落地（2026-05-12）。
>
> Spec：[`docs/spec/archive/2026-05-12-add-platform-ios/`](../../../../docs/spec/archive/2026-05-12-add-platform-ios/)
> 跨平台契约：[`../README.md`](../README.md)
> 实现原理：[`docs/design/runtime/embedding.md`](../../../../docs/design/runtime/embedding.md)
> 构建工作流：[`docs/workflow/building/ios.md`](../../../../docs/workflow/building/ios.md)

把 z42 VM 编进 SwiftPM 包 + xcframework，让 Swift / SwiftUI iOS app 一行 `import Z42VM` 跑 `.zbc`。

## Quick Start

```bash
# 1. 一次性 toolchain
rustup target add aarch64-apple-ios aarch64-apple-ios-sim x86_64-apple-ios

# 2. 编 compiler + stdlib
dotnet build src/compiler/z42.slnx

# 3. 编 iOS facade
./build.sh
```

产物：`Z42VM.xcframework/` + `Resources/stdlib/*.zpkg`。详见 [`docs/workflow/building/ios.md`](../../../../docs/workflow/building/ios.md)。

## API 速记

```swift
import Z42VM

let vm = try Z42VM(
    zpkgResolver: BundleZpkgResolver(),
    stdoutHandler: { bytes in
        textArea.append(String(decoding: bytes, as: UTF8.self))
    }
)
let module = try vm.loadZbc(Data(contentsOf: zbcURL))
let entry  = try vm.resolveEntry(module, fqn: "App.Main")
_ = try vm.invoke(entry)
// vm.deinit 自动调 z42_host_shutdown
```

### `Z42VM.init(zpkgResolver:stdoutHandler:stderrHandler:)`

- `zpkgResolver: ZpkgResolver` — 默认 `BundleZpkgResolver()`（读 `Bundle.main/stdlib/<ns>.zpkg`）
- `stdoutHandler / stderrHandler: ((Data) -> Void)?` — 每条 z42 输出触发一次，UTF-8 字节

### `Z42VMValue`

```swift
public enum Z42VMValue: Equatable {
    case null
    case i64(Int64)
    case f64(Double)
    case bool(Bool)
}
```

v0.1 marshal 仅支持 null + 三种原语；string / object / Array 推迟到后续 spec（[`embedding.md §12 Deferred`](../../../../docs/design/runtime/embedding.md)）。

### `Z42VMError`

`enum` 含 10 个 case 对应 `Z42HostStatus`：`alreadyInit` / `notInit` / `badConfig` / `featureOff` / `badZbc` / `verification` / `entryNotFound` / `argMismatch` / `vmException` / `internal`。每个携带 message + 数值 `status: Int32`。

### `ZpkgResolver` 协议

```swift
public protocol ZpkgResolver {
    func resolve(namespace: String) -> Data?
}
```

内置实现：

- `BundleZpkgResolver(bundle:subdirectory:)` —— 默认从 `Bundle.main/stdlib/<ns>.zpkg` 加载
- `MapZpkgResolver([:])` —— `[String: Data]` 测试用 / 自定义来源

## 限制（v0.1）

- **仅 interp 模式**：App Store 政策禁动态代码生成；JIT 不可用，AOT 占位
- **无 native interop**：v0.1 iOS 不含 `native-interop` feature。原因：libffi-sys 在 iOS arm64 bundled 汇编时 CFI advance_loc 不兼容。后续 spec 引入 vendored libffi 后启用
- **单实例**：与其他平台一致；一个进程一个 `Z42VM`
- **同步 invoke**：长任务阻塞调用线程；UI 上请用 `DispatchQueue.global().async`
- **Demo / XCTest / CI**：推迟到独立 spec（`add-platform-ios-demo` / `-tests` / `-ci`）

## 错误码映射

详见 [`platforms/README.md`](../README.md) §错误码映射表。

## 故障排查

| 现象 | 处理 |
|------|------|
| `linker not found for aarch64-apple-ios` | `rustup target add aarch64-apple-ios` |
| `Z42VMError(20): function ... not found` | `.zbc` 与 stdlib 的命名空间不一致；检查 FQN 拼写 |
| `dyld: Library not loaded` | xcframework slice 选错；真机 vs simulator |
| stdoutHandler 没触发 | z42 代码用了非 corelib I/O；或 sink 在异步线程被 race；切回同一线程 retry |

## 与跨平台契约的对齐

类名、API 形态、错误码与 [`platforms/README.md`](../README.md) 一致。同一份 `.zbc` 在 iOS / Android / WASM 三平台行为应等价（marshal 类型限制相同）。
