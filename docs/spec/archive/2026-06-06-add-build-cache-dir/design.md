# Design: 单工程 [build].cache_dir

## Architecture

缓存目录解析链（单工程路径）：

```
.z42.toml [build].cache_dir (string?)
  → ProjectManifest.ParseBuild → BuildSection.CacheDir
  → PackageCompiler.Run:
        explicitCacheDir = CacheDir == null
            ? null
            : Path.GetFullPath(Path.Combine(projectDir, CacheDir))
  → BuildTarget(..., explicitCacheDir: explicitCacheDir)
  → cacheDir = explicitCacheDir ?? Path.Combine(projectDir, ".cache")   // 已存在，BuildTarget.cs:72
```

与 out_dir 的单工程处理对称：out_dir 也是 `Path.Combine(projectDir, Build.OutDir)`
（PackageCompiler.cs:33），cache_dir 用同样的相对解析 —— 不做 workspace 那套
PathTemplateExpander 模板展开（单工程 out_dir 同样不展开模板，保持一致）。

## Decisions

### Decision 1: cache_dir 是单工程专属字段，不进 workspace 成员路径
**问题：** workspace 成员的 `[build]` 是否也支持 cache_dir？
**选项：** A — 成员也支持；B — 仅单工程。
**决定：** 选 B。workspace 用 `[workspace.build].cache_dir` + `CentralizedBuildLayout`
集中所有成员缓存到 `artifacts/libraries/<member>/cache`；成员单独设 cache_dir 会与
集中布局语义冲突，且 `PackageCompiler` 成员路径（line 114）已用 `EffectiveCacheDir`。
仅在单工程 `ProjectManifest.ParseBuild` 解析 cache_dir，避免"成员设了却被
EffectiveCacheDir 静默忽略"的 footgun。`MemberManifest.ParseOptionalBuild` 不动。

### Decision 2: 默认 null 保持向后行为
**问题：** 不设 cache_dir 时？
**决定：** `CacheDir` 默认 null；`PackageCompiler.Run` 传 null → `BuildTarget` 回退
`projectDir/.cache`。零行为变更，opt-in。

### Decision 3: 相对路径相对 projectDir 解析
**问题：** cache_dir 相对谁？
**决定：** 相对 projectDir（toml 所在目录），与 out_dir 完全一致。xtask 的
`"../artifacts/xtask/.cache"` 从 `scripts/` 解析为 `artifacts/xtask/.cache`。

## Implementation Notes

- `BuildSection` 改为
  `record BuildSection(string OutDir, string Mode, bool Incremental, string? CacheDir = null)`
  —— 位置参数带默认值，现有 `new BuildSection("dist","interp",true)` 调用点
  （ProjectManifest:332 / ManifestLoader:185 / MemberManifest:183）不受影响。
- 无参 ctor `BuildSection() : this("dist","interp",true)` 仍有效（CacheDir → null）。
- `KnownBuildKeys`（ProjectManifest.cs:109）加 `"cache_dir"`，否则 ScanUnknownKeys 警告。
- `PolicyFieldPath.cs:67` `"build.cache_dir" => null` → `=> m.Build.CacheDir`。
- `PackageCompiler.Run` 在算出 `outDir` 后紧接着算 explicitCacheDir，传入 BuildTarget 的
  `explicitCacheDir:` 命名参数（当前 Run() 未传该参数，走默认 null）。

## Testing Strategy

- 单元测试（`ProjectManifestTests.cs`）：
  - cache_dir 设值 → `BuildSection.CacheDir` == 该值
  - 不设 → `CacheDir == null`
  - cache_dir 不触发 unknown-key 警告
- 端到端手验：rebuild xtask.zpkg，确认 `.zbc` 落到 `artifacts/xtask/.cache`，
  `scripts/.cache` 不再新增
- 不需新 golden / VM 测试（不涉及 zbc/zpkg 格式或运行期语义）
