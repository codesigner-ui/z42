# Design: 跨平台测试流程框架

## Architecture

```
                 z42 xtask.zpkg test platform <p> [build|assets|run]
                                   │
                       xtask_cli.z42 _dispatchTest
                                   │
                    xtask_test_platform.z42 (共享框架)
        ┌──────────────┬──────────┴───────────┬───────────────┐
        │ _backendFor(name) 注册表             │ 三步驱动:
        │   "wasm"  → WasmBackend              │  _platformBuild(b,root)  → b.BuildProject
        │   "ios"   → IosBackend               │  _platformAssets(b,root) → 共享:编fixture+收stdlib→b.Assets()落点
        │   "android" → AndroidBackend         │  _platformRun(b,root)    → b.RunTests
        └──────────────┬──────────────────────┴───────────────┘
                       │ interface IPlatformBackend
        ┌──────────────┼───────────────┬──────────────────────┐
   WasmBackend     IosBackend      AndroidBackend
   (wasm-pack)     (xcframework)   (cargo-ndk + AAR)
        └─ 都架在现有 src/toolchain/host/platforms/<p>/ 之上（产物落各平台约定目录）
```

## Decisions

### Decision 1: 接口驱动（IPlatformBackend）而非函数 if-else
**问题：** 三平台三步，怎么共享框架又隔离差异。
**决定：** 定义 `IPlatformBackend`，三平台各实现一个 class；框架按名查注册表分派。
**理由：** 与 z42 stdlib 惯例一致（如 `PriorityQueue<T> : IBasicCollection<T>`）；新增平台
只加一个 class + 注册一行；三步驱动对所有后端统一。User 2026-06-15 确认。

### Decision 2: 第②步"构建测试资产"逻辑共享，落点参数化
**问题：** 三个 build.sh 各抄一遍"编 fixture + 收 stdlib"，最易漂移。
**决定：** 共享 `_platformAssets`：编 `examples/embedding/{hello,multi_line}.z42`→.zbc（z42c driver）
+ 收集 `artifacts/build/libraries/dist/release/*.zpkg`；后端只通过 `AssetLayout` 声明落点
（fixtures 目录 / stdlib 目录 / 是否生成 files.json 索引）。
**理由：** 消除三处重复；落点是唯一真实差异（Xcode/Gradle/browser 各有约定路径）。

### Decision 3: 端到端经 Process 直接 spawn（不调旧 build.sh）
**决定：** 三步逻辑用 z42 重写，经 `Process` 直接 spawn wasm-pack/dotnet/cargo/xcodebuild/
gradle/cargo-ndk/node（沿用 xtask_common 的 `_exec`/`_execIn`）。
**理由：** 对齐 migrate-scripts-to-z42 的 bash→z42；旧 .sh 保留至 CI-proven 再删（稳妥节奏）。

### Decision 4: AssetLayout 用普通 class（非泛型字段）
后端 `Assets()` 返回 `AssetLayout`（fixturesDir / stdlibDir / wantFilesJson:bool 等字段）。
普通 class，无泛型字段（z42c 自举受限写法不适用此处——xtask 是常规 exe 项目，但仍保持简单）。

## Implementation Notes

### IPlatformBackend 接口
```
public interface IPlatformBackend {
    string Name();                  // "wasm" | "ios" | "android"
    int    BuildProject(string root);   // ① 平台原生构建（cargo + wasm-pack/xcframework/AAR）
    AssetLayout Assets(string root);    // ② 落点声明（绝对路径）
    int    RunTests(string root);       // ③ 平台 runner
}
```

### AssetLayout（共享②的目标）
```
public class AssetLayout {
    public string fixturesDir;   // .zbc 落点（绝对路径）
    public string[] stdlibDirs;  // stdlib zpkg 落点（可多个：main + test bundle）
    public bool   wantFilesJson; // browser fetch-list（仅 wasm true）
}
```

### 共享 _platformAssets（一份）
1. 校 z42c driver dll 存在
2. for src in {hello, multi_line}: `dotnet z42c.dll examples/embedding/<src>.z42 --emit zbc -o <fixturesDir>/<src>.zbc`
3. 收集 `artifacts/build/libraries/dist/release/*.zpkg` → 每个 stdlibDir（清旧 + 拷新）
4. wantFilesJson → 在 stdlibDir 写 `files.json`（zpkg 文件名 sorted JSON 数组）

### CLI 分派
`_testRouter` 加 `AddRouter("platform", ...)`；platform 子路由含 `wasm`/`ios`/`android`/`all`
叶子，各带可选 `build|assets|run` positional（空=全流程）。`_dispatchTest` 解析后调
`_platformAll/_platformBuild/_platformAssets/_platformRun`。

### 平台落点（AssetLayout 实参）
| 平台 | fixturesDir | stdlibDirs | filesJson |
|------|-------------|-----------|-----------|
| wasm | `platforms/wasm/js/fixtures` | `platforms/wasm/js/stdlib` | ✓ |
| iOS | `platforms/ios/Tests/Z42VMTests/Resources/test-fixtures` | `platforms/ios/Resources/stdlib` + 测试 bundle stdlib | ✗ |
| Android | `platforms/android/z42vm/src/androidTest/assets/test-fixtures` | `platforms/android/z42vm/src/main/assets/stdlib` | ✗ |

（落点以各平台现有 build.sh 实际路径为准，实施时逐一核对。）

## Testing Strategy
- 编译：build xtask 项目无错（含 4 新文件）
- wasm 参考后端本地端到端：`test platform wasm build`（wasm-pack）+ `assets`（fixtures+stdlib）+ `run`（local node + playwright）逐步验证，与旧 `build.sh && test.sh` 结果一致
- iOS/Android：本地无 Xcode-sim/NDK-emulator 完整环境 → 编译验证 + 与 .sh 移植保真度逐行核对；端到端验证留 CI（下一步 change）+ 旧 .sh 仍在作回退
- GREEN：dotnet GoldenTests（确认未碰编译器）+ xtask 项目编译
