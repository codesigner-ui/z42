# Design: Restructure build output directories

## Architecture

```
.z42.toml [build]
  output_dir?  ──┐
  cache_dir?   ──┤
  dist_dir?    ──┤
                 ▼
        ProjectManifest.BuildSection
        (raw — null = unset)
                 │
                 ▼
        ResolvedManifest.ResolvedBuild
        (effective — defaults computed)
                 │
                 ▼
        CentralizedBuildLayout (workspace) or
        Driver single-file path
                 │
                 ▼
        Compiler / PackageCompiler
        — writes .zbc to cache_dir
        — writes .zpkg / exe to dist_dir
```

## Decisions

### Decision 1: 三字段 + 计算默认值（vs 单字段 / vs 五字段）

**问题**：用户实际需要几个独立可调的目录？

**选项**：
- A — **三字段（`output_dir` + `cache_dir` + `dist_dir`）** with defaults `${output_dir}/.cache` / `${output_dir}/dist`
- B — 单字段 `output_dir`，cache/dist 子目录写死不能配
- C — 五字段（再分 `incremental_dir` / `debug_info_dir`），更细粒度

**决定**：**A**。
- 比 B 灵活：用户想把 cache 放 RAM disk / tmpfs 时只覆盖 `cache_dir`，不影响 dist
- 比 C 简单：增量元数据 / 调试符号都属于 "中间产物" 范畴，不需要再拆；future 真有第三类时再加（YAGNI）
- 默认值机制让 90% 项目只设一个 `output_dir`（或全不设走默认）—— 三字段并存不带来额外认知负担

### Decision 2: 重命名 `out_dir` → `dist_dir`，pre-1.0 直接换不留 alias

**问题**：保留 `out_dir` 作为 alias 给现存 toml 软着陆？

**选项**：
- A — **直接 rename**，老 `out_dir` 字段触发 WS008 unknown-key warning（已有 hygiene 机制）
- B — 保留 `out_dir` 作为 `dist_dir` alias，发 deprecation warning，N 个版本后删

**决定**：**A**。
- 与 [philosophy.md "不为旧版本提供兼容"](../../../.claude/rules/philosophy.md#不为旧版本提供兼容2026-04-26-强化) 一致：pre-1.0 不留兼容路径
- 仓库内所有 `*.z42.toml` 在 same commit 全部更新（机械工作）
- 用户的项目 toml：升级 SDK 后会从 WS008 提示中得到迁移指引（"unknown key 'out_dir'; did you mean 'dist_dir'?" 由 Levenshtein 命中）

### Decision 3: 单文件编译 `output_dir` 默认 = 源文件目录

**问题**：`z42c build foo.z42` 无 manifest 时，产物放哪？

**选项**：
- A — **源文件所在目录**（`output_dir = dirname(foo.z42)`）—— 在不同 cwd 跑结果稳定
- B — 当前 cwd（受 cwd 影响，不稳定）
- C — 临时目录 / `/tmp` —— 太隐蔽

**决定**：**A**。CLI flag `--output-dir` 覆盖（优先级 CLI > manifest > 默认）—— 这是 Open Question 1 的推荐方案。

### Decision 4: Workspace 与 project 字段对齐 + member 继承覆盖

**问题**：workspace 已有 `out_dir` / `cache_dir`（默认 `"dist"` / `".cache"`）；同 schema 后继承规则？

**决定**：workspace `[build]` 同样三字段；member 任一字段 unset → 继承 workspace 的 effective 值；workspace 任一字段 unset → 走该字段的全局默认（workspace_root 是 `output_dir`）。

```
member.cache_dir    若设 → 用 member.cache_dir
               若未设 → workspace.cache_dir 若设 → 用之
                                       若未设 → ${workspace_root}/.cache
```

模板变量 `${output_dir}` 加入插值集合（与现有 `${workspace_dir}` / `${profile}` / `${member_name}` 并列）。

### Decision 5: `[sources]` 不动 — iteration 2 仅文档化

**问题**：User 原 iteration 2 提的"加 glob + 加 exclude"功能其实都已实现 ([ProjectManifest.cs:177-213](../../../src/compiler/z42.Project/ProjectManifest.cs#L177-L213) 用 `Microsoft.Extensions.FileSystemGlobbing.Matcher`)。

**决定**：User 已确认"现有够用，只补文档"。design doc `docs/design/compiler/project.md` 补：
- `[sources].include` glob 示例（`src/**/*.z42` / `lib/**/*.z42` / per-target `[[exe]].src`）
- `[sources].exclude` 用法（默认空，常见 pattern 示例）
- 与 `[[exe]].src` 的覆盖关系
- 不引入 negation pattern（`!path`）/ 不默认收紧 exclude（保持当前行为，避免破坏）

## Implementation Notes

### 默认值计算位置

**不在 `ParseBuild` 里填默认值** —— 那一层只读 TOML 字面值。

`BuildSection` 用 `string?` 表示三个字段（null = 未设）；effective 路径在 `ResolvedManifest.Resolve(...)` 里计算：

```csharp
public sealed record BuildSection(
    string? OutputDir,
    string? CacheDir,
    string? DistDir,
    string  Mode,
    bool    Incremental);

// ResolvedManifest.Resolve(...) 内
string effectiveOutputDir = build.OutputDir ?? defaultOutputDir;       // defaultOutputDir = dirname(toml) or dirname(srcFile)
string effectiveCacheDir  = build.CacheDir  ?? Path.Combine(effectiveOutputDir, ".cache");
string effectiveDistDir   = build.DistDir   ?? Path.Combine(effectiveOutputDir, "dist");
```

这样 raw schema 保留"未设"信号，便于：
- workspace member 继承时区分"显式空"（继承 workspace）与"显式覆盖"
- 序列化回 TOML 时不写出默认值（保持 schema 简洁）

### CentralizedBuildLayout 模板变量

现有 `${workspace_dir}` / `${profile}` / `${member_name}` 插值保留。新增 `${output_dir}` 指 effective output 根（workspace 模式下 = workspace_root；project 模式下 = project_dir）。

`cache_dir` / `dist_dir` 字段值若引用 `${output_dir}`，在 effective 计算时先插值再 Path.Combine。

### Workspace member 互动

```
z42-workspace.toml
[build]
output_dir = "/build/z42-workspace"   # 整个 workspace 产物根

member-a/z42.toml
[build]
cache_dir = "/dev/shm/z42-cache/a"    # 只覆盖 cache (放 tmpfs)；dist 继承 workspace
```

→ member-a 的 effective：
- `output_dir` = `/build/z42-workspace`（继承 workspace）
- `cache_dir`  = `/dev/shm/z42-cache/a`（member 覆盖）
- `dist_dir`   = `/build/z42-workspace/dist`（继承 workspace 的默认）

### 单文件编译路径

`z42c build foo.z42` （无 .z42.toml）：

```csharp
// Driver 内
string sourceDir = Path.GetDirectoryName(sourceAbsPath);
BuildSection synthetic = new(
    OutputDir:   cliOverride ?? sourceDir,
    CacheDir:    null,    // → 走默认 ${output_dir}/.cache
    DistDir:     null,    // → 走默认 ${output_dir}/dist
    Mode:        cliMode  ?? "interp",
    Incremental: true);
```

CLI `--output-dir <path>` 优先级最高，覆盖默认。`--cache-dir` / `--dist-dir` CLI 标志为 Open Question 推迟（当前不引入，必要时单独 spec）。

### `[policy]` 受管字段映射

```csharp
static readonly string[] KnownFields = {
    "build.dist_dir",       // 替代 build.out_dir
    "build.output_dir",     // 新
    "build.cache_dir",      // 新
    "build.mode",
    "build.incremental",
    // profile.* 不变
};
```

`PolicyFieldPath.GetValue` switch 同步：

```csharp
"build.dist_dir"   => m.Build.DistDir,
"build.output_dir" => m.Build.OutputDir,
"build.cache_dir"  => m.Build.CacheDir,
```

## Testing Strategy

- **单元测试**：
  - `ProjectManifestTests`：未设 → 三字段都是 null（raw section）；显式覆盖一个/两个/三个的解析。
  - `ResolvedManifestTests` (新或扩 BuildSectionTests)：默认值计算 — 仅 manifest 路径 / 显式覆盖单字段 / 显式覆盖全部；单文件路径 = source dir。
  - `WorkspaceManifestTests`：workspace 同 schema；member 继承 + 覆盖。
  - `CentralizedBuildLayoutTests`：`${output_dir}` 模板变量插值；member 继承覆盖矩阵。
  - `PolicyFieldPathTests`：新 KnownFields；旧 `build.out_dir` 不再 known。
- **Golden / e2e**：
  - 仓库内一个 stdlib zpkg build 流程跑通，验证产物落入新 `dist_dir`；cache 落入新 `cache_dir`。
  - `xtask test compiler` 全绿（dotnet test）。
  - `xtask test stdlib z42.core` 验证 stdlib 仍能编译 + 测试。
  - manifest hygiene WS008：老 `out_dir` 字段触发 unknown-key warning + Levenshtein 建议 `dist_dir`。
- **VM 验证**：不涉及 VM 改动，但 `xtask test vm` 跑一遍确认 stdlib zpkg 仍正确执行（路径变化不影响 zpkg 内容）。
