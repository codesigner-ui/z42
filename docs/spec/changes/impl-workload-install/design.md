# impl-workload-install — design（B2）

## Architecture

```
z42 workload install ios
  1. 读 manifest workloads.ios（host 校验：ios 仅 macOS）
  2. 下载 z42-<ver>-ios.tar.gz（tooling = facade+模板+glue）→ runtimes/<ver>/workloads/ios/
  3. 按需拉 target runtime pack（ios-arm64 / iossim-arm64）→ runtimes/<rid>/<ver>/（Decision 10，复用现下载层）
  4. done → 现 baked `export/publish ios` 读这些目录即可用
```

复用：launcher_network 的下载/解压 + 现 `z42 install` 的 manifest-first（release-index.json）+ sha + tgz 流式解压（add-launcher-install）。

## Decisions（需 User 拍）

### D1：runtime 与 workload **分离**（✅ User 定：为版本管理）
`z42-<ver>-<wl>.tar.gz`（workload tooling）= **Swift/Kotlin/JS facade（Sources）+ 工程模板 + native glue**，**不含 VM 库**；VM 库进独立 **runtime pack** `z42-runtime-<ver>-<rid>.tar.gz`，按 `workloads.<wl>.runtimes` 列表拉。
- **目的：版本管理**——VM 修 bug 只更 runtime pack、不动 workload tooling，反之亦然（对标 dotnet workload manifest 钉 runtime pack 版本）。
- **desktop 复用 host runtime**（`runtimes: []`）——用已装 host runtime（`z42 install <ver>`），不拉平台专属。
- ios/android/wasm 拉平台专属 runtime（D2 多 RID）；`Package.swift` 改引独立 runtime xcframework（binaryTarget）。

> **现状发现（2026-06-17）**：runtime/SDK 分包**目前只对 desktop 做了**（xtask_package_desktop 产 `z42-runtime-*` + SDK 两件）；**移动端/wasm 仍是单一合并包**（xtask_package_ios 把 facade+libz42.a 打一起）。→ **B2-1 的核心工作 = 把移动端打包也拆成 runtime pack + workload tooling**。

### D2：一个 workload → 多 target RID（✅ User 的真问题，已解）
**问题**：Android 有 android-arm64 + android-x64 两 RID，但只一个 `android` workload；ios 有 device+sim 两 RID。怎么统一？
**解**：**manifest `workloads.<wl>.runtimes` 列出该 workload 的全部 target RID**。`z42 workload install android` 读 `["android-arm64","android-x64"]` → 一并拉两 ABI runtime + workload tooling 包。一个 workload 统一多 ABI（对齐 `dotnet workload install android`）。已落 runtime-workload-distribution.md manifest schema。
- `workload install` 内部复用现 `install --rid` 的下载逻辑（逐 RID 拉）；`z42 install <ver>` 保持只装 host（Decision 8）；`--rid` 降为内部组件下载。

### D3：按需拉 runtime = eager（install workload 时一并拉），版本稳定后再细化（✅ User）
**推荐**：install workload 即把 `runtimes` 列表全部拉齐（对标 dotnet）；install 完即 ready。lazy（首次-use 才拉）等 runtime 版本策略稳定后再细化。

### D4：本地验证入口 `--from <path>`（解 egg 问题）
**推荐**：`z42 workload install <wl> --from <tarball|dir>` 跳过 manifest 从本地装。配合 xtask 本地产 workload 包 → `--from` 装 → 验 `export ios` 能找到 SDK。让 B2 不依赖 release 即可 e2e；CI 上传 release 走后续。

## Implementation Notes
- 产包复用现 `xtask_package_{ios,android,wasm}` 的 facade/SDK 打包逻辑（封成 `z42-<ver>-<wl>.tar.gz`）。
- 下载/manifest 复用 launcher_network + 现 install；workload 安装目录 `runtimes/<ver>/workloads/<wl>/`。
- 扫描/注册循环按 [common-pitfalls.md §1](../../../../.claude/rules/common-pitfalls.md) **显式 sort**。

## Testing Strategy
- e2e：xtask 产 workload 包（local）→ `z42 workload install <wl> --from <pkg>` → 断言 `runtimes/<ver>/workloads/<wl>/` 内容 + `export ios` 能解析到 SDK。
- `workload list` / `remove` 的 [Test]/e2e。

## 分离的又一好处：local runtime override（✅ User, 2026-06-17）
runtime 独立成包后，**可独立指向本地构建的 runtime**（dev：`z42 link <local-runtime-dir> --as <ver>` 式），workload tooling 不变。版本管理 + 本地替换两个好处都来自这条分离。

## B2-1 实现图（iOS 已深读 xtask_package_ios.z42，完全摸清）

现 `_packageIos` 产**一个合并包**：`native/Z42VM.xcframework`(VM 库)+ `native/include/*.h` + `Sources/`+`Package.swift`(binaryTarget `path:"native/Z42VM.xcframework"`)+ `libs/` + examples。拆成：

| 拆出 | 内容 | 对标 desktop |
|---|---|---|
| **runtime pack** `z42-runtime-<ver>-<rid>/` | `native/Z42VM.xcframework`(该 rid slice)+ `include/*.h` + `libs/`（stdlib）| `_buildRuntimePackage`（desktop 已有，照搬结构）|
| **workload tooling** `z42-<ver>-ios/` | `Sources/Z42VM` + `Sources/Z42VMC` + `Package.swift`（binaryTarget 改引独立 runtime）+ examples + manifest | — |

**纠缠点（B2-1 与 B2-2 必须一起想）**：
1. **Package.swift 的 binaryTarget path** 现指 `native/Z42VM.xcframework`（包内）。拆后 xcframework 在独立 runtime pack（装到 `runtimes/<rid>/<ver>/`），tooling 在 `runtimes/<ver>/workloads/ios/`。→ Package.swift 需**install 时把 path 改写成已装 runtime 的 xcframework 绝对路径**（或 export 生成工程时填）。即 **tooling 的 Package.swift 是模板，路径在 install/export 时解析**——这必然牵连 B2-2 的 install 逻辑。
2. **iOS xcframework 是多 slice 单容器**（device+sim 一个 .xcframework），不是每 RID 一个 .a。→ ios 的 runtime pack 用**一个多-slice xcframework**；manifest `runtimes` 对 ios 实为"一个容器覆盖多 slice"，对 android 才是"多 ABI 多 .so"。两平台模型不同，install 逻辑要分别处理。
3. **orchestration**（xtask_package.z42）现对 ios 只调 `_packageIos` 产一个包；要像 desktop 那样**调两次**（tooling + runtime pack）。

→ **结论：B2-1（拆包）与 B2-2（install 解析 Package.swift 路径 + 组合 tooling+runtime）耦合,应合并为一个"iOS 端到端拆分 + install"实现单元**，不宜先只产包（产了的 Package.swift 路径悬空、无法 swift build 验证）。

## 分期（B2 内部，按上述纠缠修正）
- **B2-1+2（iOS 端到端）**：拆 iOS 打包（runtime pack + tooling）+ `workload install --from`（装 tooling + runtime + 解析 Package.swift 路径）+ swift build 验证（iOS 工具链本地可跑：Xcode 16.4 + ios targets 已装）。
- **B2-3（android/wasm）**：照 iOS 模式拆（android 多 .so/gradle、wasm module/npm；NDK/wasm-pack 多归 CI）。
- **B2-4（CI release）**：release.yml 上传 workload + runtime packs + manifest 联网装。
