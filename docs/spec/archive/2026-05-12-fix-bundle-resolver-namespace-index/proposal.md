# Proposal: BundleZpkgResolver / AssetZpkgResolver 引入 namespace index

## Why

Mobile facade 的两个默认 resolver 当前实现假设 **namespace == 文件名**：

```swift
bundle.url(forResource: namespace, withExtension: "zpkg", subdirectory: ...)
```

```kotlin
assets.open("$subdir/$namespace.zpkg")
```

但 stdlib 实际是**多对一**的：

| zpkg 文件 | 提供的 namespaces |
|---|---|
| `z42.core.zpkg`        | `Std`, `Std.Exceptions`, ... |
| `z42.io.zpkg`          | `Std.IO` |
| `z42.math.zpkg`        | `Std.Math` |
| `z42.text.zpkg`        | `Std.Text` |
| `z42.collections.zpkg` | `Std.Collections` |
| `z42.test.zpkg`        | `Std.Test` |

写 `using Std.IO;` 后运行时问 resolver `"Std.IO"` → 当前 resolver 找 `Std.IO.zpkg` → 不存在 → invoke 时报 `undefined function Std.IO.Console.WriteLine`。

`add-ios-tests` spec 实施时由 R1 / R6 / R7（依赖 Console.WriteLine 的正路 scenario）暴露了这个 bug。`host_tests` 之所以一直绿，是因为它的 `MapZpkgResolver` 在 test 代码里**手工写死**了 namespace → bytes 映射；mobile 终端用户不可能这么干。

## What Changes

引入 **namespace index 文件**（`stdlib/index.json`）作为 resolver 的 namespace-to-file 查找表：

1. **`scripts/build-stdlib.sh`** 产 stdlib 的 flat view 时，**同步生成** `artifacts/build/libs/<profile>/index.json`。内容形如：

   ```json
   {
     "z42.core":         "z42.core.zpkg",
     "Std":              "z42.core.zpkg",
     "Std.Exceptions":   "z42.core.zpkg",
     "Std.IO":           "z42.io.zpkg",
     "Std.Math":         "z42.math.zpkg",
     "Std.Text":         "z42.text.zpkg",
     "Std.Collections":  "z42.collections.zpkg",
     "Std.Test":         "z42.test.zpkg"
   }
   ```

   v1 用 build-stdlib.sh 内的 heredoc 写死；auto-discovery 进 Deferred（见末尾 Open Questions）。

2. **iOS `BundleZpkgResolver`**：构造时从 `bundle.url(forResource: "index", withExtension: "json", subdirectory: ...)` 读 index，建 `[String: String]` map；`resolve(namespace:)` 先按 map 查文件名，再按文件名读字节。Index 缺失时回退到原有"namespace == 文件名"行为（向后兼容自定义 resolver 摆放方式）。

3. **Android `AssetZpkgResolver`**：同步 fix，从 `assets.open("$subdir/index.json")` 读 index。逻辑对称。

4. **iOS `build.sh`** 把 `index.json` 一并拷进 `Resources/stdlib/` 和 `Tests/Z42VMTests/Resources/stdlib/`（双份持有，与 zpkg 同步）。

5. **Android `build.sh`** 把 `index.json` 一并拷进 `z42vm/src/main/assets/stdlib/`。

6. **wasm** 暂不动 —— 没有正路测试，下一个 wasm spec 落地时一并修。在 wasm README / spec deferred 备注。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `scripts/build-stdlib.sh`                                                          | MODIFY | 在 flat view 段尾追加 `index.json` 生成 |
| `src/toolchain/host/platforms/ios/Sources/Z42VM/ZpkgResolver.swift`                | MODIFY | `BundleZpkgResolver` + `MapZpkgResolver` 二者 + 新增 `loadIndex` 内部 helper |
| `src/toolchain/host/platforms/ios/build.sh`                                        | MODIFY | 同时拷 `index.json` 到 `Resources/stdlib/` 与 `Tests/Z42VMTests/Resources/stdlib/` |
| `src/toolchain/host/platforms/android/z42vm/src/main/java/io/z42/vm/ZpkgResolver.kt` | MODIFY | `AssetZpkgResolver` 镜像 iOS fix |
| `src/toolchain/host/platforms/android/build.sh`                                    | MODIFY | 同时拷 `index.json` 到 `z42vm/src/main/assets/stdlib/` |
| `docs/spec/changes/fix-bundle-resolver-namespace-index/{proposal,design,tasks}.md` | NEW    | 本 spec 文档 |
| `docs/spec/changes/fix-bundle-resolver-namespace-index/specs/.../spec.md`          | NEW    | scenario |
| `docs/design/runtime/embedding.md`                                                 | MODIFY | §11 resolver 协议描述补 "namespace index" 段 |

**只读引用：**
- `src/runtime/src/host/ops.rs` — `build_host_module` 看 runtime 怎么调用 resolver
- `src/runtime/src/host/host_tests.rs` — `resolver_via_map_resolver_loads_corelib_without_search_paths` 看预期 mapping
- `src/libraries/<lib>/src/*.z42` — namespace 声明（hardcode mapping 的依据）
- 工作树内 `src/toolchain/host/platforms/ios/Tests/Z42VMTests/Z42VMTests.swift`（add-ios-tests in-progress，本 spec 用作 cross-validation；不修改）

## Out of Scope

- wasm resolver 修复（独立 wasm spec 落地）
- 用 `z42c` / `z42vm` 子命令自动发现 zpkg→namespace 关系（Deferred）
- 把 index 数据搬进 `z42.workspace.toml` 作为单一真相（v2 candidate）
- 任何 resolver / 嵌入 API 形态变更
- v0.1 不在 mobile 端引入 `MapZpkgResolver` 的等价 index 加载（MapZpkgResolver 是测试 / 自定义路径用，不读 index）

## Open Questions

- [ ] **Index 数据源**：v1 hardcode 在 `build-stdlib.sh` 内 heredoc。是否应该立刻搬进 `z42.workspace.toml`？我倾向 **hardcode → 留 Deferred**；workspace.toml 加 `[workspace.namespaces]` 段是 v2 工作量（涉及 build-stdlib.sh 写一个 TOML→JSON 转换器），不增加 v1 落地的价值。
- [ ] **Resolver 缺 index 时回退行为**：旧 "namespace == 文件名"逻辑保留？我倾向 **保留**作为 fallback —— 自定义 resolver 把 zpkg 直接命名为 namespace 时仍能跑；index.json 是优化路径而非强制契约。
- [ ] **JSON 解析依赖**：iOS 用 Foundation `JSONSerialization`（系统库）；Android 用 `org.json.JSONObject`（Android SDK 内置）；都不引入新依赖。OK?
