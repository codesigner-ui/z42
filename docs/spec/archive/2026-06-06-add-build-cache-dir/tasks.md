# Tasks: 单工程 [build].cache_dir

> 状态：🟢 已完成 | 创建：2026-06-06 | 完成：2026-06-06

## 进度概览
- [x] 阶段 1: schema + 解析
- [x] 阶段 2: 构建路径接线
- [x] 阶段 3: 配置 + 文档 + 测试

## 阶段 1: schema + 解析
- [x] 1.1 BuildSection record 加 `string? CacheDir = null`（ProjectManifest.cs:406）
- [x] 1.2 KnownBuildKeys 加 `"cache_dir"`（ProjectManifest.cs:109）
- [x] 1.3 ParseBuild 读 `cache_dir`（ProjectManifest.cs:335）

## 阶段 2: 构建路径接线
- [x] 2.1 PackageCompiler.Run 解析 Build.CacheDir → explicitCacheDir 传 BuildTarget（PackageCompiler.cs:33+56）
- [x] 2.2 PolicyFieldPath `build.cache_dir` → m.Build.CacheDir（PolicyFieldPath.cs:67）

## 阶段 3: 配置 + 文档 + 测试
- [x] 3.1 xtask.z42.toml 加 cache_dir = "../artifacts/xtask/.cache"
- [x] 3.2 project.md [build] 字段表 + 示例加 cache_dir
- [x] 3.3 ProjectManifestTests 加 cache_dir 解析测试（设值 / 默认 null）+ known-keys 测试纳入 cache_dir（不报 WS008）
- [x] 3.4 dotnet build + dotnet test 全绿（1489/0；含新增 cache_dir 用例）
- [x] 3.5 rebuild xtask.zpkg：.zbc 落 artifacts/xtask/.cache（20 文件）、scripts/.cache 不再生成、增量 20/20 命中

## 备注
- cache_dir 仅单工程字段（workspace 成员走 `[workspace.build].cache_dir`）。
- 不涉及 zbc/zpkg 格式 → 无 version bump。
- 验证报告：dotnet build 0 errors；ProjectManifest+Policy 定向 50/50；全 compiler 套件 1489/0；
  e2e：scripts/.cache 清空后不再生成，缓存改落 artifacts/xtask/.cache，二次构建 20/20 cached。
