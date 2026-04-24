# Design: add-multi-exe-target

## Architecture

```
ProjectManifest
  ├── Project      (metadata)
  ├── Sources      (shared glob)
  ├── Build        (output config)
  ├── Profiles     (debug/release)
  └── ExeTargets   List<ExeTarget>   ← NEW
        ├── Name
        ├── Entry
        └── Src?   (override Sources if present)

BuildCommand.Run()
  ├── 旧路径：Project.Kind == Exe  → 单目标，行为不变
  └── 新路径：ExeTargets.Count > 0 → 多目标循环
        ├── --exe <name> filter
        └── 每个 target: ResolveSourceFiles(target) → CompileFile(...)
```

## Decisions

### Decision 1: ExeTarget 的源文件解析

`ExeTarget.Src` 为 null 时继承 `manifest.Sources`；
有值时创建独立 `Matcher` 只用自身 glob。
提取 `ResolveSourceFiles(ExeTarget?)` 重载统一处理。

### Decision 2: 产物命名

多目标模式：`dist/<exe.Name>.zbc`（使用 exe 的 name，不用 project name）
单目标模式：`dist/<project.Name>.zbc`（保持现有行为）

### Decision 3: TOML 数组表解析

Tomlyn 的 `[[exe]]` 对应 `TomlTableArray`（`model["exe"]` 是 `TomlArray`，每项是 `TomlTable`）。
需验证每项都有 `name` 和 `entry`。
