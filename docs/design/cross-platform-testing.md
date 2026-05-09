# Cross-Platform 测试架构

> 状态：Design Draft（2026-05-09）。落地分散到 [rewrite-z42-test-runner-compile-time](../../spec/changes/rewrite-z42-test-runner-compile-time/)（lib API） + [add-platform-wasm](../../spec/changes/add-platform-wasm/) / [add-platform-android](../../spec/changes/add-platform-android/) / [add-platform-ios](../../spec/changes/add-platform-ios/) 各自的 Testing 子段。
>
> 本文是 [cross-platform.md](cross-platform.md)（VM build 矩阵）与 [testing.md](testing.md)（测试框架架构）的桥接：**同一份测试集如何在 host / wasm / iOS / Android 一致地跑，并把失败精确报告回 CI**。

---

## 设计目标

1. **Single source of truth** — `src/tests/` 与 `src/libraries/<lib>/tests/` 的所有用例都能在每个平台跑（除显式 skip 的）；**不为某平台 fork 测试集**
2. **零重新编译** — `.zbc` 在 host CI 编译一次，分发给各平台 CI 作为输入；`.zbc` 二进制平台无关
3. **Pass 判定原生化** — assert-only 用例 = 进程退出 0；golden 用例 = stdout 匹配。两种判定都是平台原生概念，无需自定义协议
4. **Library-first runner** — z42-test-runner 是 Rust **library**，CLI 只是其薄壳；每个平台直接绑库不 fork

## 非目标

- 平台间 stdout 字面 byte-identical（换行符 / locale 允许差异，golden 比对走 trim + line-by-line）
- 在每个平台都能跑**所有**用例（filesystem / threading / native FFI 等用例可显式 `[SkipPlatform]`）
- iOS 真机 CI（v0.1 simulator-only；真机走 manual smoke）

---

## 架构总览

```
┌──────────────── Host CI（Linux / macOS）─────────────────┐
│                                                            │
│  src/tests/*.z42                                            │
│  src/libraries/<lib>/tests/*.z42                            │
│              │                                              │
│              ▼ z42c (C# 编译器) — 一次编译                  │
│  artifacts/test-zbcs/                                       │
│      ├── tests/<category>/<name>.zbc                        │
│      └── libraries/<lib>/<name>.zbc                         │
│              │                                              │
│              ▼ tar 打包 (artifacts/test-zbcs.tar)           │
│              │                                              │
│              ▼ 上传 GitHub Actions artifact                 │
└────────────────────────┬───────────────────────────────────┘
                         │
        ┌────────────────┼─────────────────────┐
        ▼                ▼                     ▼
   ┌────────┐      ┌──────────┐         ┌──────────┐
   │  WASM  │      │ Android  │         │   iOS    │
   │  CI    │      │   CI     │         │   CI     │
   └───┬────┘      └────┬─────┘         └────┬─────┘
       │                │                    │
       │ vitest         │ JUnit              │ XCTest
       │ + wasm-bindgen │ + JNI              │ + Swift FFI
       │                │                    │
       ▼                ▼                    ▼
   z42-test-runner (Rust library, 同一份 src/toolchain/test-runner/)
       │                │                    │
       ▼                ▼                    ▼
   z42_vm interp   z42_vm interp        z42_vm interp / aot
   (interp-only)   (interp-only)        (interp-only)
       │                │                    │
       ▼                ▼                    ▼
   Pass / Fail / Skip + captured stdout/stderr  ──► 各平台原生 test report
```

**核心抽象：** test-runner 不感知平台。各平台只负责
- 把 `.zbc` 字节读出来递给 runner
- 把 runner 返回的 `TestResult` 翻译成本平台 test framework 的 pass/fail
- 串接平台特有的 stdout/sink（捕获到字符串而非真打印）

---

## test-runner library API

按 [rewrite-z42-test-runner-compile-time](../../spec/changes/rewrite-z42-test-runner-compile-time/) spec 落地。最小可绑定 surface：

```rust
// src/toolchain/test-runner/src/lib.rs

/// 执行一个 .zbc 中的所有 [Test] / [Benchmark]。
pub fn run_zbc(bytes: &[u8], opts: RunOptions) -> SuiteResult;

/// 执行单个 [Test]（按 method id 或 fully-qualified name）。
pub fn run_one(bytes: &[u8], target: &str, opts: RunOptions) -> TestResult;

pub struct RunOptions {
    pub filter:   Option<String>,    // substring / regex
    pub tags:     Vec<String>,
    pub format:   OutputFormat,      // Pretty / Tap / Json
    pub bench:    bool,
    pub platform: PlatformInfo,      // ★ 用于 [SkipPlatform] 判定
}

pub struct PlatformInfo {
    pub name:    &'static str,       // "host" / "wasm" / "android" / "ios"
    pub mode:    ExecMode,           // Interp / Jit / Aot
    pub features: &'static [&'static str], // 已编译的 cargo features
}

pub enum TestResult {
    Pass    { name: String, duration_ns: u64 },
    Fail    { name: String, message: String, stdout: String, stderr: String },
    Skip    { name: String, reason: String },
}

pub struct SuiteResult {
    pub results: Vec<TestResult>,
    pub formatted: String,           // 完整 Pretty/Tap/Json 文本，平台直接转交本地 reporter
}
```

平台只需 `extern "C"` 导出 `run_zbc` 等价（wasm-bindgen / JNI / C ABI）。

---

## 各平台绑定模式

### WASM（[add-platform-wasm](../../spec/changes/add-platform-wasm/)）

```js
// platform/wasm/test/runner.test.js  (vitest)
import { Z42TestRunner } from '@z42/runtime';
import { readFileSync, readdirSync } from 'fs';

describe('z42 cross-platform tests on wasm', () => {
  const zbcDir = process.env.Z42_TEST_ZBCS;  // host CI 解包后的目录
  for (const file of readdirSync(zbcDir).filter(f => f.endsWith('.zbc'))) {
    test(file, async () => {
      const bytes = readFileSync(`${zbcDir}/${file}`);
      const result = await Z42TestRunner.runZbc(bytes, { platform: 'wasm' });
      expect(result.allPassed).toBe(true);
    });
  }
});
```

- 浏览器场景用 playwright + IndexedDB / fetch 加载 `.zbc`
- Node.js 场景直接 fs（更适合 CI）
- stdout 在 wasm 下走 `Z42TestIO.captureStdout` → 字符串（无 stdio）

### Android（[add-platform-android](../../spec/changes/add-platform-android/)）

```kotlin
// platform/android/z42-runtime/src/androidTest/.../Z42TestRunnerTest.kt
@RunWith(Parameterized::class)
class Z42TestRunnerTest(private val zbcAsset: String) {

    @Test fun runOnAndroid() {
        val bytes = context.assets.open("zbcs/$zbcAsset").readBytes()
        val result = Z42TestRunner.runZbc(bytes, platform = "android")
        assertTrue(result.allPassed, result.formatted)
    }

    companion object {
        @Parameterized.Parameters
        @JvmStatic fun cases() = context.assets.list("zbcs")
    }
}
```

- `.zbc` 作为 instrumented test asset 打包
- `Z42TestRunner.runZbc` 是 JNI 包装，内部调用 Rust `run_zbc(bytes, opts)`
- emulator / device 上跑（CI 用 macOS runner 提供 x86_64 emulator 加速）

### iOS（[add-platform-ios](../../spec/changes/add-platform-ios/)）

```swift
// platform/ios/Tests/Z42RuntimeTests/Z42TestRunnerTests.swift
class Z42TestRunnerTests: XCTestCase {
    func testAllZbcs() throws {
        let bundle = Bundle.module
        for url in try bundle.urls(forResourcesWithExtension: "zbc")! {
            let bytes = try Data(contentsOf: url)
            let result = try Z42TestRunner.runZbc(bytes, platform: "ios")
            XCTAssertTrue(result.allPassed, result.formatted)
        }
    }
}
```

- `.zbc` 作为 SwiftPM resource bundle
- Swift facade → C bridge → Rust `run_zbc`
- iOS App Store policy 禁 JIT → `interp-only` features；AOT 占位（M9 落地后真启）

---

## 测试选择性（skip 机制）

### 既有：`[Skip]` attribute

`testing.md` R1 已支持 `[Skip(reason: ...)]`（无条件）+ TIDX `skip_reason` 字段。

### 新增：`[SkipPlatform]`

```z42
[Test]
[SkipPlatform("wasm", reason: "no filesystem")]
void test_file_io() { ... }

[Test]
[SkipPlatform("ios", reason: "no JIT, test verifies JIT-specific behavior")]
[Feature("jit")]
void test_jit_inlining() { ... }
```

编译器在 TIDX 写 `skip_platforms: List<String>`（已有 `platform: String` 字段，扩为列表）。runner 在 `RunOptions.platform.name` 与该列表匹配时 → status=Skip。

**默认行为**：所有 `src/tests/` + `src/libraries/<lib>/tests/` 的用例在每个平台都跑。`[SkipPlatform]` 是显式 opt-out。

### Feature gate 联动

`[Feature("jit")]` 已存在 → runner 在 `RunOptions.platform.features` 不含 `"jit"` 时 skip。复用现有路径。

---

## 跨平台 parity 期望

| 测试类别 | host | wasm | android | ios |
|---|:---:|:---:|:---:|:---:|
| 基础语法 / 类型 / 控制流 | ✅ | ✅ | ✅ | ✅ |
| 类 / 接口 / 继承 / 泛型 | ✅ | ✅ | ✅ | ✅ |
| 异常 / try-catch | ✅ | ✅ | ✅ | ✅ |
| GC / 弱引用 | ✅ | ⚠️ 可能时序差异 | ✅ | ✅ |
| Closure / Lambda | ✅ | ✅ | ✅ | ✅ |
| ref/out/in（dir-mode 含 interp_only marker） | ✅ interp | ✅ interp | ✅ interp | ✅ interp |
| Native FFI（dlopen 路径） | ✅ | ❌ skip | ⚠️ 受限 | ❌ skip |
| Filesystem stdlib | ✅ | ❌ skip | ⚠️ scoped | ⚠️ scoped |
| Cross-zpkg 多包 | ✅ | 暂不跑 | 暂不跑 | 暂不跑 |
| JIT 专项（如 jit_transitive） | ✅ jit | ❌ skip | ❌ skip | ❌ skip |

> 比例预估：当前 157 case → 各平台预计跑 ~140 个（gc-cycle / weak / jit / native FFI / filesystem 大概 15-17 个用 `[SkipPlatform]`）。

---

## CI 矩阵

```yaml
jobs:
  host-tests:                       # 既有
    runs-on: ubuntu-latest
    steps:
      - dotnet test
      - ./scripts/test-vm.sh        # interp + jit
      - ./scripts/test-stdlib.sh
      - tar artifacts/test-zbcs/*.zbc → upload-artifact

  wasm-tests:
    needs: host-tests
    runs-on: ubuntu-latest
    steps:
      - download-artifact test-zbcs
      - cd platform/wasm && pnpm test  # vitest 跑遍所有 .zbc

  android-tests:
    needs: host-tests
    runs-on: macos-latest             # x86_64 emulator 加速
    steps:
      - download-artifact test-zbcs → assets/zbcs/
      - ./gradlew connectedAndroidTest

  ios-tests:
    needs: host-tests
    runs-on: macos-latest             # Xcode + simulator
    steps:
      - download-artifact test-zbcs → resources/
      - xcodebuild test -destination 'platform=iOS Simulator,...'
```

**合并门禁**：host-tests 必通；wasm/android/ios 任一失败标记 PR 红，但允许 manual override（v0.1 各平台稳定性未证明前的 escape hatch）。

---

## 设计决策

### Decision 1: Library-first vs subprocess-per-platform

**问题：** 每个平台直接绑 test-runner library，还是各自维护一个进程级 wrapper？

**选项：**
- **A：library-first（采纳）** —— test-runner 是 Rust crate，`pub fn run_zbc(...)`；wasm/android/ios 用 wasm-bindgen / JNI / C ABI 各自绑
- **B：subprocess-per-platform** —— 每平台跑一个 z42vm-test 进程，用文件 / pipe 输出 TestResult

**采纳 A**：
- 移动平台 subprocess 启动慢且受限（iOS 完全禁子进程；Android 受 sandbox 限）
- 库调用零额外开销，wasm 唯一可行路径（无进程概念）
- CLI 仍存在，host 用；移动平台直接绑库
- 代价：需要把 runner 的逻辑彻底解耦于 stdio（用 `String` 而非 `println!`）—— 与 `Std.Test.TestIO.captureStdout` 已有方向一致

### Decision 2: zbc 是否每平台重新编译

**问题：** host CI 编译一份 .zbc 分发，还是每平台 CI 各自编译？

**选项：**
- **A：host 编译一次（采纳）** —— `.zbc` 平台无关；编译只在 host
- **B：各平台 cross-compile** —— 每平台 CI 复制一份编译流程

**采纳 A**：
- `.zbc` 设计就是平台无关字节码，跨平台编译没意义
- 减少 CI 时间（C# 编译器只跑一次）
- 让 .zbc 与 z42c 版本绑定明确（host commit hash 即版本）
- 代价：上下游 CI job 强依赖 → 用 GitHub Actions `needs:` + `upload-artifact` / `download-artifact` 解决

### Decision 3: SkipPlatform 写在源还是 runner

**问题：** 平台过滤的归属。

**选项：**
- **A：源代码级 attribute（采纳）** —— `[SkipPlatform("wasm")]` 编译期写 TIDX，runner 读取
- **B：runner 配置级** —— 维护一份 `wasm-skip-list.toml`，runner 启动时按文件过滤

**采纳 A**：
- 与代码同居 → grep 即知；改测试时改 attribute 同步
- TIDX 已有 `platform` 字段（R1 落地），扩为列表零格式 churn
- 单一真相来源；CI runner 不需维护额外清单
- 代价：需要给 [SkipPlatform] 加 attribute validator（E0916+ 错误码）—— 现有 R4.A 框架可扩

---

## 实施分期建议

按依赖顺序：

1. **Phase 1 — test-runner library 化**（基础）
   spec：[rewrite-z42-test-runner-compile-time](../../spec/changes/rewrite-z42-test-runner-compile-time/) （已 DRAFT）
   交付：`pub fn run_zbc(...)`，CLI 改成调用库；host 现有 `./scripts/test-stdlib.sh` 不变

2. **Phase 2 — `[SkipPlatform]` attribute**（轻量）
   新 spec（小）：扩 TIDX `skip_platforms`、加 attribute validator、runner 读 RunOptions.platform.name 匹配

3. **Phase 3 — host CI 产 zbc artifact bundle**（轻量）
   新 spec（小）：`./scripts/build-test-zbcs.sh` 把所有 `src/tests/**/*.z42` + `src/libraries/<lib>/tests/*.z42` 编译到 `artifacts/test-zbcs/`，CI upload

4. **Phase 4 — wasm 平台 + testing 子段**（独立）
   spec：[add-platform-wasm](../../spec/changes/add-platform-wasm/)，加 Testing 段（vitest 消费 zbc bundle）

5. **Phase 5 — android + ios 平台**（独立，可并行）
   spec：add-platform-android / add-platform-ios，各自 Testing 段

每阶段独立可交付且不阻塞下一个；Phase 1 是关键路径，其余可并行。

---

## Deferred / Open Questions

- **Bencher 跨平台**：`Std.Test.Bencher` 用 `__bench_now_ns` builtin。wasm 需用 `performance.now()` 桥；移动平台用 `clock_gettime`。每平台需要一个 native shim。Phase 4/5 内解决。
- **真机 CI**：iOS / Android 真机租用（BrowserStack / Firebase Test Lab）成本与稳定性 trade-off，v0.1 simulator/emulator 即可，真机走 manual smoke
- **stdout 字符编码**：移动平台 NSString / Java String 都是 UTF-16，golden 比对前要 normalize 到 UTF-8 + LF。`Std.Test.TestIO.captureStdout` 已是 UTF-8 string，platform binding 转字符串时遵循
- **测试时长上限**：iOS XCTest 单测默认 60s timeout；某些 GC stress 测试可能超时 → 加 `[Timeout(secs)]` attribute（占位，不在 Phase 1-5 内）
- **wasm 内存上限**：浏览器默认 wasm linear memory ~2GB，但单 test instantiation 应 << 100MB。如有大堆测试需要 `[SkipPlatform("wasm", reason: "memory")]`
