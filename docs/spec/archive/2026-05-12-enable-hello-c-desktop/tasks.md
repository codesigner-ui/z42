# Tasks: 让 hello_c desktop staticlib build + run 端到端跑通

> 状态：🟢 已完成 | 创建：2026-05-12 | 归档：2026-05-12 | 类型：fix（build infra；H2b 时遗留的 reference-only 状态升级到落地）

**变更说明：** `src/toolchain/host/examples/hello_c/main.c` 在 H2b 写好后一直停在 "reference source only" 状态 —— runtime 当时是 rlib-only。`redesign-artifact-layout` (2026-05-12) 把 runtime 改成 `crate-type = ["cdylib", "staticlib", "rlib"]`，`libz42.a` 现在就在 `artifacts/build/runtime/release/`，可以直接接通。本 spec 写 `build.sh` 让 hello_c 端到端 build + 跑 + assert stdout。

**原因：** Tier 1 C ABI 终于有完整 reference 例子；为 mobile facade 提供"原始 C 用法对照"；与 `examples/hello_rust` 形成 C + Rust 双语言对照。

**文档影响：**
- `src/toolchain/host/examples/hello_c/README.md` 状态段从 🔵 reference → 🟢 落地
- 暂不加入 `./scripts/test-all.sh` 默认 GREEN 路径（hello_c 是 example，不是 critical infrastructure）；落地后用户可手动 `./build.sh` 验

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/toolchain/host/examples/hello_c/main.c`      | MODIFY | 入口 FQN 从 `"Embedding.Hello.Main"` → `"Hello.Main"`（对齐 `examples/embedding/hello.z42` 复用现成共享 fixture）|
| `src/toolchain/host/examples/hello_c/build.sh`    | NEW    | cargo build runtime → z42c 编 fixture → cc 链 staticlib → 跑 + assert |
| `src/toolchain/host/examples/hello_c/README.md`   | MODIFY | 状态段 reference → 落地；加 "Run" 段 |
| `docs/spec/changes/enable-hello-c-desktop/tasks.md` | NEW  | 本文件 |

**只读引用：**

- `src/runtime/include/{z42_abi,z42_host}.h` — C ABI 头
- `examples/embedding/hello.z42` — 共享 fixture（iOS XCTest 也用）
- `src/toolchain/host/examples/hello_rust/` — 对照 Rust 版本（不动）

## 任务清单

- [x] 1.1 改 main.c 入口 FQN `"Embedding.Hello.Main"` → `"Hello.Main"`；过时的"reference only"语句改为"end-to-end 落地"
- [x] 1.2 新建 `build.sh`：cargo rustc --crate-type=staticlib → 解析 native-static-libs → z42c 编 fixture → cc 链 + 跑 + assert
- [x] 1.3 跑 `build.sh` 端到端，stdout 匹配 `[host] hello, world`
- [x] 1.4 README 状态 🔵 → 🟢，加 "Run" 段
- [x] 1.5 `./scripts/test-all.sh` 6 stage 全绿（hello_c 不进默认 GREEN）
- [x] 1.6 `.gitignore` 加 `out/`
- [x] 1.7 commit + push（type=fix, scope=host/examples/hello_c）

## 实施备注

- staticlib 通过 `cargo rustc --release --lib --crate-type=staticlib` 显式 emit，与 rlib coexist 在同一 target dir，不动主 Cargo.toml `[lib]` 配置（保留"主流 path 依赖走 rlib"的设计意图）
- native libs 列表通过 `cargo rustc --print=native-static-libs` **首次**编译时一次性记录到 `out/native-static-libs.txt`；后续 cache hit 时复用记录值（rustc cache hit 不重新打印 note）；macOS arm64 是 `-liconv -lSystem -lc -lm`
- main.c 用 `search_paths` 配置（hello_c H2b 时已写好），不走 resolver hook。两条路径都是 v0.1 支持的；resolver hook 是 mobile / wasm 偏好，search_paths 是 desktop 偏好
- 期间发现一次 `StdlibSidecarRoundTripsLineTable` flaky failure（重跑通过；与 stdlib zsym 刚被 cargo clean 删后未及时重建有关，与 hello_c 无关）
- entry FQN `Hello.Main` 复用 `examples/embedding/hello.z42` —— 与 add-ios-tests R1 完全共享一份 fixture

## 备注

- hello_c 复用 `examples/embedding/hello.z42` 而非新建 fixture，避免 examples/embedding 与 examples/hello_c 各自一套；与 add-ios-tests 共享同一份 source-of-truth
- macOS 上 native-static-libs 一般是 `-lSystem -lresolv -lc -lm -lpthread -ldl`；build.sh 用 `cargo rustc --print=native-static-libs` 动态获取以兼容平台差异
- 后续若需 CI 上自动跑 hello_c，再起 `add-hello-c-to-test-all` spec 把它接进 stage 7
