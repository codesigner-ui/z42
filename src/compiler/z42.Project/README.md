# z42.Project — 项目清单管理

## 职责

读取 `.z42.toml` 文件，向 Driver 提供可直接使用的构建配置。
`z42.Compiler` 对此模块一无所知——编译器只接受源码字符串，不关心文件从哪里来。

## 核心文件

| 文件 | 职责 |
|------|------|
| `ProjectManifest.cs` | 发现（`Discover`）、加载（`Load`）`.z42.toml`；解析 `[[exe]]` 多目标；解析 glob 模式得到源文件列表；解析 pack 优先级链 |
| `ZpkgBuilder.cs` | 将多个 `ZbcFile` 组装为 `ZpkgFile`（packed/indexed 两种模式） |

## 入口点

- `ProjectManifest.Discover(dir)` — 在目录树中查找 `.z42.toml`
- `ProjectManifest.Load(path)` — 加载并解析清单，返回构建目标列表

## 依赖关系

依赖 `z42.IR`（使用 `Z42Proj` / `ProjectMeta` 等 TOML 数据模型），不依赖 `z42.Compiler`。

**与 z42.IR/PackageTypes.cs 的分工**：`PackageTypes.cs` 是 TOML 的纯数据模型，`ProjectManifest.cs` 是在此基础上的发现与解析逻辑。
