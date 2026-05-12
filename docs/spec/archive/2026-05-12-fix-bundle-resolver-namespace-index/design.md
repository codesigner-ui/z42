# Design: namespace index 与 mobile resolver

## Architecture

```
                   host build-stdlib.sh
                   │
                   │ 产 zpkg + 同时 emit index.json
                   ▼
        artifacts/build/libs/release/
          ├─ z42.core.zpkg
          ├─ z42.io.zpkg
          ├─ z42.math.zpkg
          ├─ z42.text.zpkg
          ├─ z42.collections.zpkg
          ├─ z42.test.zpkg
          └─ index.json     ←── 新增
              { "Std.IO": "z42.io.zpkg", ... }

         ┌─────────────────┴─────────────────┐
         ▼                                   ▼
   iOS build.sh:                       Android build.sh:
   cp 全部 → Resources/stdlib/         cp 全部 → assets/stdlib/
   cp 全部 → Tests/.../stdlib/

   BundleZpkgResolver(.module, "stdlib")  AssetZpkgResolver(assets, "stdlib")
       ↓ resolve("Std.IO")                    ↓ resolve("Std.IO")
       ↓ 查 index → "z42.io.zpkg"              ↓ 查 index → "z42.io.zpkg"
       ↓ 读字节                                 ↓ 读字节
   ────────────────────────────────────────────────────
       Tier 1 zpkg_resolver_callback (C ABI)
   ────────────────────────────────────────────────────
       z42 runtime (build_host_module)
```

## Decisions

### D1: Index 数据源 v1 hardcode 在 build-stdlib.sh

**问题：** namespace → file 映射的 ground truth 在哪？

**选项：**
- A. Hardcode 在 `build-stdlib.sh`（heredoc）
- B. 加 `[workspace.namespaces]` 到 `src/libraries/z42.workspace.toml`，build-stdlib.sh 读取
- C. 写一个 Rust 工具扫所有 .zpkg metadata 自动汇总

**决定：** **A** 起步。理由：
- stdlib 加新 lib 是大事，约定改 workspace.toml `members` 时同时更新 build-stdlib.sh 内 heredoc 不是负担
- B 需要 shell 解析 TOML（依赖 `python3 -c "import tomllib"` 或类似），增加平台依赖
- C 工作量最大，价值是"自动同步"，但 namespace 集合稳定，drift 风险低

Auto-discovery 进 Deferred；若未来出现 stdlib 频繁变更或第三方 lib 接入，再升级到 C。

### D2: Resolver 行为 — index 优先 + fallback to filename

**问题：** index.json 不存在 / namespace 不在 index 时怎么办？

**决定：** **fallback 到原 "namespace == filename" 行为**。理由：
- 向后兼容：自定义 resolver 把 zpkg 直接命名为 namespace 仍能工作（少数高级用户用法）
- 优雅降级：index 文件丢了 / 没拷到位时，stdlib 形态正常的话仍能跑（虽然丢了 multi-namespace-per-file 的能力）
- 测试友好：负路 R5（MapResolver only knows "Std.Phantom"）仍按现有行为走

### D3: index.json 格式 — 平铺 `{ns: filename}` map

**问题：** 一个 zpkg 提供多 namespace 时，index 是 `{ns: filename}` 多键展开，还是 `{filename: [ns, ...]}` 反索引？

**决定：** **`{ns: filename}` 平铺**。Reverse map 需要 resolver 维护反索引，平铺直接命中 O(1)。

格式：

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

### D4: 修 iOS + Android 一起；wasm 推迟

**问题：** 三平台 facade 受同 bug 影响。一并修还是分批？

**决定：** **iOS + Android 一起修**（symmetric 改动 + 同一 design 文档下成本可控）；**wasm 推迟**到 wasm spec。

理由：
- iOS / Android resolver 形态对称：`BundleZpkgResolver` ↔ `AssetZpkgResolver`，逻辑可以照搬
- wasm resolver 仍待设计（fetch / static / Node FS 多种形态），与 iOS / Android 模式不一样
- 现阶段 wasm 没有正路测试，wasm spec 落地时一并补；本 spec 先不动 wasm 代码

## Implementation Notes

### build-stdlib.sh 改动 sketch

在 flat view 段尾追加：

```bash
# Emit namespace index for mobile resolvers (BundleZpkgResolver /
# AssetZpkgResolver). v1 hardcoded; auto-discovery is in Deferred.
INDEX="$FLAT_DIR/index.json"
cat > "$INDEX" <<'EOF'
{
  "z42.core":        "z42.core.zpkg",
  "Std":             "z42.core.zpkg",
  "Std.Exceptions":  "z42.core.zpkg",
  "Std.IO":          "z42.io.zpkg",
  "Std.Math":        "z42.math.zpkg",
  "Std.Text":        "z42.text.zpkg",
  "Std.Collections": "z42.collections.zpkg",
  "Std.Test":        "z42.test.zpkg"
}
EOF
echo "  index:     $INDEX"
```

### iOS ZpkgResolver.swift 改动 sketch

```swift
public struct BundleZpkgResolver: ZpkgResolver {
    public let bundle: Bundle
    public let subdirectory: String?
    private let index: [String: String]    // namespace → filename

    public init(bundle: Bundle = .main, subdirectory: String? = "stdlib") {
        self.bundle = bundle
        self.subdirectory = subdirectory
        self.index = Self.loadIndex(bundle: bundle, subdirectory: subdirectory)
    }

    public func resolve(namespace: String) -> Data? {
        // 1. index 优先
        if let filename = index[namespace],
           let basename = filename.split(separator: ".").first.map(String.init),
           let ext = filename.split(separator: ".").last.map(String.init),
           let url = bundle.url(
               forResource: basename,
               withExtension: ext,
               subdirectory: subdirectory
           ) {
            return try? Data(contentsOf: url)
        }
        // 2. fallback: namespace as filename
        guard let url = bundle.url(
            forResource: namespace,
            withExtension: "zpkg",
            subdirectory: subdirectory
        ) else { return nil }
        return try? Data(contentsOf: url)
    }

    private static func loadIndex(bundle: Bundle, subdirectory: String?) -> [String: String] {
        guard let url = bundle.url(
            forResource: "index",
            withExtension: "json",
            subdirectory: subdirectory
        ),
        let data = try? Data(contentsOf: url),
        let json = try? JSONSerialization.jsonObject(with: data, options: []) as? [String: String]
        else { return [:] }
        return json
    }
}
```

注意：`forResource:withExtension:subdirectory:` API 把文件名拆成 basename + extension。`z42.io.zpkg` → basename `z42.io`, extension `zpkg`。Swift 内置 `URL.deletingPathExtension` / `URL.pathExtension` 更稳健 —— 实施时用 Foundation API 而非手工 split。

### Android ZpkgResolver.kt 改动 sketch

```kotlin
class AssetZpkgResolver(
    private val assets: AssetManager,
    private val subdir: String = "stdlib",
) : ZpkgResolver {
    private val index: Map<String, String> = loadIndex()

    override fun resolve(namespace: String): ByteArray? {
        // 1. index 优先
        index[namespace]?.let { filename ->
            try {
                return assets.open("$subdir/$filename").use { it.readBytes() }
            } catch (_: IOException) { /* fall through */ }
        }
        // 2. fallback: namespace as filename
        return try {
            assets.open("$subdir/$namespace.zpkg").use { it.readBytes() }
        } catch (_: IOException) { null }
    }

    private fun loadIndex(): Map<String, String> = try {
        val text = assets.open("$subdir/index.json").use {
            it.readBytes().toString(Charsets.UTF_8)
        }
        val obj = org.json.JSONObject(text)
        val result = HashMap<String, String>(obj.length())
        val keys = obj.keys()
        while (keys.hasNext()) {
            val k = keys.next()
            result[k] = obj.getString(k)
        }
        result
    } catch (_: Exception) { emptyMap() }
}
```

### Platform build.sh 改动

- iOS：在拷 stdlib 段尾追加把 `index.json` 拷到 `Resources/stdlib/` 和 `Tests/Z42VMTests/Resources/stdlib/`
- Android：类似，拷到 `z42vm/src/main/assets/stdlib/`

### embedding.md §11 文档段

在 resolver 协议描述末尾加：

> **Namespace index（mobile / wasm 默认 resolver 用）**：默认 mobile 平台 resolver 通过 stdlib 目录中的 `index.json`（map namespace → 具体 zpkg 文件名）解析 namespace；index 由 host `build-stdlib.sh` 产出。自定义 resolver 可不读 index，自行维护 namespace → bytes 映射。Index 缺失或 namespace 不在 index 时，默认 resolver 回退到 namespace-as-filename 行为，向后兼容简单摆放方式。

## Deferred / Future Work

### auto-derive-namespace-index: 让 build-stdlib.sh 从 zpkg 元数据自动产 index

- **来源**：本 spec 草稿期 D1
- **触发原因**：v1 hardcode 在 build-stdlib.sh，drift 风险低但仍存在
- **前置依赖**：暴露 zpkg metadata 的 CLI（候选：`z42vm metadata <zpkg> --provided-namespaces` 或 `z42c index <dir>`）
- **触发条件**：(a) 出现 stdlib 频繁加 namespace 的场景 (b) 第三方 lib 想接入嵌入 API
- **当前 workaround**：build-stdlib.sh 内 heredoc 与新加 lib 一起维护

## Testing Strategy

- **R1 验证**：跑 `./scripts/build-stdlib.sh` 后 cat `artifacts/build/libs/release/index.json` 看格式正确
- **R2 验证**：iOS unit test `BundleResolverIndexTests`（本 spec 不新增，复用 add-ios-tests 的 R1/R6/R7）
- **R3 验证**：Android 暂无 instrumented test framework，本 spec 不做 unit test；下一个 `add-android-tests` spec 会覆盖
- **R5 验证**：iOS `swift test` 7/7 绿
- **全局**：`./scripts/test-all.sh` 全绿（必须；workflow.md 新规则）
