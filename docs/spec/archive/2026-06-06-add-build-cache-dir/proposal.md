# Proposal: 单工程 [build] 段支持 cache_dir

## Why

z42c 的增量编译缓存（per-source `.zbc`）默认落在 **projectDir/.cache** —— 即
`.z42.toml` 所在目录。对独立构建的工具（如 xtask，toml 在 `scripts/`），缓存就堆在
`scripts/.cache`，污染源码树。最终产物（`xtask.zpkg`）已能通过 `[build] out_dir`
重定向到 `artifacts/`，唯独缓存留在原地。

编译器**已有** `explicitCacheDir` 管道（workspace 构建用它把缓存集中到
`artifacts/libraries/<member>/cache`），且单工程 `[build]` 段**早已预留** cache_dir
（[`PolicyFieldPath.cs:67`](../../../../src/compiler/z42.Project/PolicyFieldPath.cs)：
`"build.cache_dir" => null, // C1 BuildSection 暂无 cache_dir 字段`）。本变更补全这个
预留字段，让单工程可以像重定向 out_dir 一样重定向缓存。

不做的话：每次构建独立工程都在源码目录留 `.cache`（虽 gitignore 但污染工作区），且
预留的 policy 字段一直半挂着。

## What Changes

- `BuildSection` record 增加 `string? CacheDir`（默认 null = 保持现有 projectDir/.cache 行为）
- 单工程 `[build]` 解析器（`ProjectManifest.ParseBuild`）读 `cache_dir`，并把它加入
  `KnownBuildKeys` 白名单（否则 ScanUnknownKeys 报 unknown-key 警告）
- `PackageCompiler.Run`（单工程路径）：若 `Build.CacheDir` 非空，解析为
  `projectDir/<cache_dir>` 并作为 `explicitCacheDir` 传给 `BuildTarget`；否则保持 null（现有行为）
- `PolicyFieldPath` 把 `build.cache_dir` 映射到 `m.Build.CacheDir`（补全预留 hook）
- `xtask.z42.toml` 设 `cache_dir = "../artifacts/xtask/.cache"`
- `project.md` 文档 `[build]` 字段表 + 示例加 cache_dir 行

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Project/ProjectManifest.cs` | MODIFY | BuildSection 加 CacheDir；ParseBuild 读 cache_dir；KnownBuildKeys 加 "cache_dir" |
| `src/compiler/z42.Pipeline/PackageCompiler.cs` | MODIFY | Run() 单工程路径解析 Build.CacheDir → explicitCacheDir 传给 BuildTarget |
| `src/compiler/z42.Project/PolicyFieldPath.cs` | MODIFY | `build.cache_dir` 映射到 m.Build.CacheDir（替换硬编码 null） |
| `scripts/xtask.z42.toml` | MODIFY | [build] 加 cache_dir = "../artifacts/xtask/.cache" |
| `docs/design/compiler/project.md` | MODIFY | [build] 字段表 + 示例加 cache_dir |
| `src/compiler/z42.Tests/ProjectManifestTests.cs` | MODIFY | 新增 cache_dir 解析（设值 / 默认 null / 不报 unknown-key）测试 |

**只读引用**（理解上下文，不修改）：

- `src/compiler/z42.Pipeline/PackageCompiler.BuildTarget.cs` — `explicitCacheDir ?? projectDir/.cache` 机制
- `src/compiler/z42.Project/MemberManifest.cs` / `ManifestLoader.cs` — workspace 成员路径（不改；cache_dir 仅单工程字段）

## Out of Scope

- workspace 成员 `[build].cache_dir`（workspace 用 `[workspace.build].cache_dir` + CentralizedBuildLayout 集中布局，成员不单独设）
- 其它 stray `.cache`（z42.crypto / z42.time 等的独立构建产物）
- 改变默认缓存位置（默认仍 projectDir/.cache，零向后行为变更）

## Open Questions

- 无（字段语义、解析、相对路径解析均与现有单工程 out_dir 处理对称）
