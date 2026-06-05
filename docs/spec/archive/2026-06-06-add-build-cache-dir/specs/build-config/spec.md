# Spec: [build].cache_dir

## ADDED Requirements

### Requirement: 单工程 [build] 支持 cache_dir 重定向增量缓存

#### Scenario: 设置 cache_dir 把缓存重定向到指定目录
- **WHEN** 单工程 `.z42.toml` 的 `[build]` 段设 `cache_dir = "../artifacts/foo/.cache"`，且从 projectDir 构建
- **THEN** 增量编译缓存的 `.zbc` 写到 `projectDir/../artifacts/foo/.cache`（相对 projectDir 解析），而非 `projectDir/.cache`

#### Scenario: 不设 cache_dir 时保持默认
- **WHEN** `[build]` 段不含 `cache_dir`
- **THEN** `BuildSection.CacheDir == null`，缓存仍写到 `projectDir/.cache`（现有行为不变）

#### Scenario: cache_dir 是 [build] 的已知键，不报 unknown-key 警告
- **WHEN** `[build]` 含 `cache_dir`
- **THEN** `ScanUnknownKeys` 不对 cache_dir 发 "unknown key" 警告（cache_dir ∈ KnownBuildKeys）

#### Scenario: policy 字段 build.cache_dir 反映实际值
- **WHEN** 通过 `PolicyFieldPath` 查询 `build.cache_dir`
- **THEN** 返回 `m.Build.CacheDir`（而非旧的硬编码 null）

## IR Mapping

不涉及 IR / zbc / zpkg 格式 —— 纯 manifest 字段 + 构建编排路径。

## Pipeline Steps

受影响阶段：
- [x] 工程文件解析（`ProjectManifest.ParseBuild`）
- [x] 构建编排（`PackageCompiler.Run` → `BuildTarget.explicitCacheDir`）
- [ ] Lexer / Parser / TypeChecker / IR Codegen / VM interp —— 均不涉及
