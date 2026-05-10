# Design: add-project-manifest

## Architecture

```
Program.cs
  ├─ arg[0] == "build"  ──▶  BuildCommand.Run()
  │                              ├─ ProjectManifest.Discover()   找 *.z42.toml
  │                              ├─ ProjectManifest.Load()       解析 TOML
  │                              ├─ SourceResolver.Resolve()     展开 glob
  │                              └─ 逐文件走现有编译 pipeline
  └─ arg[0] == <file>   ──▶  现有单文件流程（不变）
```

## Decisions

### Decision 1: TOML 解析库

**问题：** C# 没有内置 TOML 支持，需要第三方库或手写解析器。

**选项：**
- Tomlyn：成熟、符合 TOML 1.0、支持直接反序列化到 C# 对象
- 手写：工作量大，不值得

**决定：** 使用 Tomlyn（`dotnet add package Tomlyn`）

### Decision 2: ProjectManifest 数据结构

直接映射 TOML 结构到 C# record，用 `[TomlyModel]` 特性驱动反序列化：

```csharp
sealed record ProjectManifest(
    ProjectSection  Project,
    SourcesSection? Sources,
    BuildSection?   Build,
    // ...
);
```

`name` 字段可选，Load 时若为 null 则从文件名推断。

### Decision 3: Glob 展开

**问题：** `[sources].include` 是 glob 模式，需要展开为文件列表。

**决定：** 使用 `Microsoft.Extensions.FileSystemGlobbing`（.NET 内置，无需额外依赖）

### Decision 4: 编译 pipeline 复用

BuildCommand 不重写编译逻辑，直接复用 Program.cs 中现有的 Lex → Parse → TypeCheck → Codegen → Emit 流程，提取为可调用的方法。
