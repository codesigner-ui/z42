# Design: Unify Package Format (.zpkg)

## Architecture

```
.z42 源文件
   │
   ▼ z42c 逐文件编译
   │
   ├─ 粒度 1（文件级）
   │    .z42 → .cache/<rel>.zbc      ← 编译原子，VM 可直接加载
   │
   └─ 粒度 2（工程级）
        .zpkg（mode = indexed）      ← 开发态：索引 .cache/*.zbc
        .zpkg（mode = packed）       ← 发布态：内联所有 .zbc
```

```
VM 加载入口
   ├─ .zbc   → 单文件执行（快速迭代 / 单文件发布更新）
   └─ .zpkg  → 工程执行
                ├─ mode=indexed → 从磁盘读取各 .zbc，合并符号表
                └─ mode=packed  → 解包内联 ZbcFile，合并符号表
```

## Decisions

### Decision 1: 合并 .zmod / .zbin → .zpkg

**问题：** `.zmod` 和 `.zbin` 是同一语义（工程包）的两种存储形态，但用不同扩展名维护，工具链需双轨感知。

**选项：**
- A: 保留 `.zmod` / `.zbin` 双扩展名，仅在内部统一类型 — 减少文件改动，但 API 概念仍然分裂
- B: 合并为 `.zpkg`，`mode` 字段区分 `indexed` / `packed` — 概念统一，VM 加载器简化

**决定：** 选 B。扩展名是对外契约，统一后 VM / 工具链只需感知一种工程格式。

### Decision 2: `pack` 优先级链

**问题：** exe 和 lib 的打包需求可能不同；profile 级别也需要覆盖。

**优先级（高 → 低）：**

```
[profile.debug/release].pack
  ↓
[[exe]].pack  /  (lib 工程暂无 [[lib]]，用 [project].pack)
  ↓
[project].pack
  ↓
内置默认值：debug=false, release=true
```

**默认值（未显式配置时）：**

| profile | pack 默认 | 产出 |
|---------|-----------|------|
| debug   | false     | indexed zpkg |
| release | true      | packed zpkg |

### Decision 3: `[package]` → `[project]` section 名对齐

**问题：** `docs/design/project.md` 用 `[package]`，但 `ProjectTypes.cs` 中 `Z42Proj.Project` 对应 TOML `[project]`。

**决定：** 统一用 `[project]`（与代码一致），同步更新 `docs/design/project.md`。

### Decision 4: `BuildConfig` 移除 `Emit` 字段

**问题：** 工程构建唯一产物是 `.zpkg`，`emit` 配置项已无意义（单文件模式的 `--emit` 仍保留在 CLI flag 层面）。

**决定：** 从 `BuildConfig` 删除 `Emit`；CLI `--emit` flag 仅在单文件模式生效。

## `.zpkg` JSON 格式（Phase 1）

```json
{
  "name": "hello",
  "version": "0.1.0",
  "kind": "exe",
  "mode": "indexed",
  "entry": "Hello.main",
  "exports": [
    { "symbol": "Hello.main", "kind": "func" }
  ],
  "dependencies": [],
  "files": [
    {
      "source": "src/Hello.z42",
      "bytecode": ".cache/src/Hello.zbc",
      "source_hash": "sha256:e3b0c44298fc1c149...",
      "exports": ["Hello"]
    }
  ],
  "modules": []
}
```

packed 模式：

```json
{
  "name": "hello",
  "version": "0.1.0",
  "kind": "exe",
  "mode": "packed",
  "entry": "Hello.main",
  "exports": [...],
  "dependencies": [],
  "files": [],
  "modules": [
    { /* ZbcFile — Hello.z42 完整内容 */ }
  ]
}
```

**字段说明：**

| 字段 | 类型 | 说明 |
|------|------|------|
| `name` | string | 包名 |
| `version` | string | SemVer |
| `kind` | `"exe" \| "lib"` | 可执行 or 库 |
| `mode` | `"indexed" \| "packed"` | 存储模式 |
| `entry` | string? | kind=exe 时有值 |
| `exports` | `ZpkgExport[]` | 公开符号表 |
| `dependencies` | `ZpkgDep[]` | 外部依赖 |
| `files` | `ZpkgFileEntry[]` | mode=indexed 时有值 |
| `modules` | `ZbcFile[]` | mode=packed 时有值 |

## `z42.toml` 变更

**新增 `pack` 字段，三层优先级：**

```toml
[project]
name    = "hello"
version = "0.1.0"
kind    = "exe"
entry   = "Hello.main"
pack    = false          # 全局默认（最低优先级）

[[exe]]
name  = "tool"
entry = "Tool.main"
pack  = true             # 目标级覆盖（中优先级）

[profile.debug]
pack = false             # profile 级覆盖（最高优先级）

[profile.release]
pack = true
```

**移除 `[build].emit`：** 工程构建唯一产物 `.zpkg`，无需配置。

## C# 类型变更

```csharp
// 新增
public enum ZpkgMode { Indexed, Packed }

public sealed record ZpkgFileEntry(
    string       Source,
    string       Bytecode,
    string       SourceHash,
    List<string> Exports
);

public sealed record ZpkgExport(string Symbol, string Kind);
public sealed record ZpkgDep(string Name, string? Version, string? Path);

public sealed record ZpkgFile(
    string           Name,
    string           Version,
    ZpkgKind         Kind,       // ZpkgKind = exe | lib (重用或新建)
    ZpkgMode         Mode,
    string?          Entry,
    List<ZpkgExport> Exports,
    List<ZpkgDep>    Dependencies,
    List<ZpkgFileEntry> Files,
    List<ZbcFile>    Modules
) { public static readonly int[] CurrentVersion = [0, 1]; }

// 删除
// ZmodManifest, ZmodFileEntry, ZmodKind (若只被这两个用)
// ZbinFile, ZbinExport, ZbinDep

// ProjectTypes.cs 变更
public enum EmitKind { Ir, Zbc, Zasm }  // 去掉 Zmod, Zbin
public sealed class BuildConfig {
    // 移除 Emit 字段
    public ExecModeConfig Mode        { get; set; } = ExecModeConfig.Interp;
    public bool           Incremental { get; set; } = true;
    public string         OutDir      { get; set; } = "dist";
}
public sealed class ProfileConfig {
    public ExecModeConfig? Mode     { get; set; }
    public int?            Optimize { get; set; }
    public bool?           Debug    { get; set; }
    public bool?           Strip    { get; set; }
    public bool?           Pack     { get; set; }  // 新增
}
```

## Rust 类型变更

```rust
// formats.rs 新增
#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ZpkgMode { Indexed, Packed }

#[derive(Debug, Serialize, Deserialize)]
pub struct ZpkgFileEntry {
    pub source:      String,
    pub bytecode:    String,
    pub source_hash: String,
    pub exports:     Vec<String>,
}

#[derive(Debug, Serialize, Deserialize)]
pub struct ZpkgFile {
    pub name:         String,
    pub version:      String,
    pub kind:         ZpkgKind,   // exe | lib
    pub mode:         ZpkgMode,
    pub entry:        Option<String>,
    pub exports:      Vec<ZpkgExport>,
    pub dependencies: Vec<ZpkgDep>,
    pub files:        Vec<ZpkgFileEntry>,
    pub modules:      Vec<ZbcFile>,
}

// loader.rs
// load_zmod + load_zbin → load_zpkg
// 支持 .zpkg 扩展名；删除 .zmod / .zbin 分支
```

## Testing Strategy

- 单元测试：`ProjectManifestTests` — 验证 `pack` 字段解析和优先级链
- Golden test：新增 `emit=zpkg-indexed` / `emit=zpkg-packed` 场景
- VM 验证：`./scripts/test-vm.sh` 覆盖 `.zbc` 和 `.zpkg` 加载
