# Design: 扩展 z42.toml workspace manifest schema（C1）

## Architecture

```
                       ┌────────────────────────────────┐
                       │  z42c CLI / build orchestrator │
                       └──────────────┬─────────────────┘
                                      │ 调用
                          ┌───────────▼────────────┐
                          │  ManifestLoader        │  (新增, z42.Project)
                          │  - 发现 workspace 根    │
                          │  - 解析 workspace 文件  │
                          │  - 解析 member 文件     │
                          │  - 应用共享/继承规则    │
                          │  - 校验段限制 / 错误码  │
                          └─┬──────────────────────┘
                            │ 产出
                     ┌──────▼──────────────────────┐
                     │  ResolvedManifest（每 member 一份）│
                     │  - project（合并后）        │
                     │  - sources / build / deps   │
                     │  - 来源链（每字段）         │
                     └─────────────────────────────┘
```

**ManifestLoader** 是 C1 的核心新增模块，承担：

- 文件发现（向上找 `z42.workspace.toml`）
- TOML 解析（沿用 `Tomlyn` 库，与现行 [src/compiler/z42.Project/](../../../src/compiler/z42.Project/) 保持一致）
- 字段验证、段限制检查、错误码报告
- 应用 `[workspace.project]` / `[workspace.dependencies]` 的引用语义（`xxx.workspace = true`）
- glob 展开 + exclude / default-members 过滤
- 产出每个 member 的 `ResolvedManifest`（带字段来源链，给 C4 的 `info --resolved` 使用）

**不在 C1 的合并逻辑**（留给后续）：

- include 链合并（C2）
- policy 强制覆盖（C3）

C1 阶段，每个 member 的最终配置 = workspace 共享继承 + member 自身字段，**不含** include 链与 policy。

---

## Decisions

### Decision 1: workspace 根文件名固定为 `z42.workspace.toml`

**问题**：现行文档对工作区根文件名表述不一致；编译器需要稳定标识符以区分 virtual manifest 与 member manifest。

**选项**：
- A. 保持灵活，任何 `*.z42.toml` 都可包含 `[workspace]` 段
- B. 固定名 `z42.workspace.toml`，仅此文件可含 `[workspace]` 段

**决定**：选 B。理由：
- 编译器发现 workspace 根的成本从"读所有 manifest 头部"降为"按文件名匹配"
- 与现行 member 命名 `<name>.z42.toml` 形成清晰对照
- 防止意外把某 member manifest 升级为 workspace 根
- 与 D2（根 manifest 必须 virtual）配合，规则简洁一致

### Decision 2: virtual manifest 强制（D2）

**问题**：root manifest 是否允许同时含 `[workspace]` 和 `[project]`？

**选项**：
- A. 允许共存（现行文档隐含支持）
- B. 强制拆分，`z42.workspace.toml` 仅 virtual

**决定**：选 B。理由：
- workspace 根的语义职责是"协调"，与 member 的"产出"职责正交
- 共存会带来"该 manifest 既贡献产物、又管理其他 member"的双重身份，逻辑复杂、错误信息易误导
- 拆分后 z42c 实现单一：发现到 `z42.workspace.toml` → workspace 模式；发现到 `<name>.z42.toml` → member 模式
- 对用户成本极小（多一个 `<name>.z42.toml` 文件 + 一行 members 引用）

### Decision 3: 依赖语法对齐 Cargo（key 层面引用）

**问题**：成员引用 workspace 共享依赖的语法。

**选项**：
- A. 保留现行 `version = "workspace"`（值层面）
- B. 改为 `dep.workspace = true`（key 层面）

**决定**：选 B。理由：
- 现行写法把"引用"塞进 `version` 字段值，与 `path` / `git` 等其他字段并列时语义混乱
- `xxx.workspace = true` 是独立 key，与其他字段平级；可与局部修饰共存（`{ workspace = true, optional = true }`）
- Cargo 验证过的设计，z42 用户从 Rust 迁移时认知零成本
- pre-1.0 阶段切换无兼容包袱（按"不为旧版本提供兼容"规则）

### Decision 4: members 支持 glob

**问题**：`members` 是否支持 glob 模式。

**选项**：
- A. 仅显式数组
- B. 显式 + glob 混用，配合 exclude

**决定**：选 B。理由：
- monorepo 加新 member 时无需改根 manifest（与 Cargo 一致）
- glob 限定为目录级（不允许文件级 `**/*.z42.toml`），避免"在错位置放 manifest 被意外纳入"
- 配合 `exclude` 排除沙盒目录（如 `experiments/*`）

**实现要点**：
- glob 实现用 .NET 的 `Microsoft.Extensions.FileSystemGlobbing` 或简易自实现（`*` / `**` 即可，无需完整 brace expansion）
- 解析时记录原始 pattern 供 `WS007 OrphanMember` 诊断使用

### Decision 5: `[workspace.project]` 字段集合

**问题**：哪些字段允许通过 `[workspace.project]` 共享。

**选项**：
- A. 全部 `[project]` 字段
- B. 限制为元数据字段（`version` / `authors` / `license` / `description`）

**决定**：选 B。理由：
- `name` / `kind` / `entry` 是每个 member 的身份标识，强制每个 member 自己声明（防止意外冲突）
- 元数据字段（`version` / `license` 等）多 member 共享是正常诉求，集中可有效降低重复
- 与 Cargo 的 `[workspace.package]` 行为一致

### Decision 6: 路径字段支持有限模板变量（D8）

**问题**：MSBuild 提供 `$(SolutionDir)` / `$(ProjectDir)` 等宏 + 用户自定义 properties + 属性函数，z42 是否引入对等机制？

**选项**：
- A. 不引入任何变量替换（Cargo 风格，路径全部相对 manifest）
- B. 引入完整 MSBuild 风格（含用户自定义 + 函数）
- C. 引入**有限的内置只读变量**（4 个），仅在白名单路径字段允许，禁止用户自定义和函数

**决定**：选 C。理由：
- 完全无变量（A）会让 include 路径在 monorepo 中变成 `../../presets/...` 这种脆弱表达；移动 member 时 include 全失效
- 完全 MSBuild（B）已被前期讨论排除（与"声明式"哲学冲突，调试困难，manifest 变 DSL）
- 有限版本（C）以 1% 的复杂度吃掉 99% 的实际需求：4 个变量已覆盖"workspace 集中布局 + profile 分流产物"两类场景
- 仅路径字段允许，标量元数据（version/name 等）禁用，避免变量污染语义字段

**变量集合**：

| 变量 | 含义 | 来源 |
|---|---|---|
| `${workspace_dir}` | workspace 根绝对路径 | `z42.workspace.toml` 文件位置推断 |
| `${member_dir}` | 当前 member 目录绝对路径 | member manifest 文件位置 |
| `${member_name}` | 当前 member `[project] name` | member manifest |
| `${profile}` | 当前激活 profile 名 | CLI `--profile` 或默认 `debug` |

**语法**：
- 占位 `${name}`（`${env:NAME}` 占位但 C1 暂不支持，报 WS037）
- 字面量 `$` 写 `$$`
- 嵌套不允许（`${a${b}}` 报 WS038）
- 未闭合报 WS038

**允许字段白名单**：

- `include` 数组各元素
- `[workspace.build] out_dir / cache_dir`（C1 解析占位，C3 实施）
- `[workspace.dependencies] xxx.path` 与 `[dependencies] xxx.path`
- `[sources] include / exclude` 中的 glob 模式

**禁止字段**：标量元数据（`version` / `name` / `kind` / `entry` / `description` / `license` / `authors`）、`members` glob 模式（避免歧义）。

**展开时机**：manifest 加载完成后一次性展开所有路径字段；`ResolvedManifest` 中存的是展开后的字符串。`--resolved` 反查（C4 提供）会同时显示原模板与展开值。

---

## Implementation Notes

### 模块拆分

```
src/compiler/z42.Project/
├── ManifestLoader.cs                    (新增) 入口，发现 + 解析 + 合并
├── WorkspaceManifest.cs                 (新增) z42.workspace.toml 数据模型
├── MemberManifest.cs                    (新增) <name>.z42.toml 数据模型
├── ResolvedManifest.cs                  (新增) 合并后的最终配置 + 字段来源链
├── ManifestErrors.cs                    (新增) WS003/005/007/030-039 错误码定义
├── GlobExpander.cs                      (新增) members glob 展开
├── PathTemplateExpander.cs              (新增) ${var} 路径变量展开
├── ZpkgReader.cs                        (现有) 不动
├── ZpkgWriter.cs                        (现有) 不动
└── ...
```

每个新文件 ≤ 300 行（按 [.claude/rules/code-organization.md](../../../.claude/rules/code-organization.md) 软限制）。

### 字段来源链

`ResolvedManifest` 不仅记录最终值，还记录每个字段来自哪个文件：

```csharp
public sealed record FieldOrigin(string FilePath, string FieldPath, OriginKind Kind);

public enum OriginKind {
    MemberDirect,         // member 自身声明
    WorkspaceProject,     // 来自 [workspace.project]（via .workspace = true）
    WorkspaceDependency,  // 来自 [workspace.dependencies]
}

public sealed record ResolvedManifest(
    ProjectInfo Project,
    SourcesConfig Sources,
    BuildConfig Build,
    Dictionary<string, ResolvedDependency> Dependencies,
    Dictionary<string, FieldOrigin> Origins);
```

C4 的 `z42c info --resolved` 直接消费此字段。

### 错误码处理

错误统一通过 `ManifestException` 抛出，附 `WSxxx` 码、文件路径、字段路径。`WS007 OrphanMember` 是 warning 级，通过 `ILogger`（暂用 console）输出，不阻塞构建。

### PathTemplateExpander 实现要点

```csharp
public sealed class PathTemplateExpander
{
    public sealed record Context(
        string WorkspaceDir,
        string MemberDir,
        string MemberName,
        string Profile);

    // allowedField 用于 WS039 诊断（如 "[project].version"）
    public string Expand(string template, Context ctx, string fieldPath);
}
```

- 单趟扫描：`$$` → `$`、`${name}` → 查表、其他 `$` 字符 → WS038
- 嵌套检测：扫到 `${` 后再次撞 `${` 之前未闭合 → WS038
- 字段白名单由调用方控制（`ManifestLoader` 知道当前是哪个字段，决定要不要走 expander；非白名单字段直接搜索 `${` 出现就报 WS039）
- `${env:NAME}` 在 C1 阶段统一报 WS037，错误信息提示"未来版本支持"以便升级时无 schema breaking

### 测试策略

新增测试类（[src/compiler/z42.Tests/](../../../src/compiler/z42.Tests/)）：

| 测试类 | 覆盖 |
|------|------|
| `WorkspaceDiscoveryTests.cs` | 向上查找根 / 找不到根 / 错误文件名 (WS030) |
| `MembersExpansionTests.cs` | 显式数组 / glob / exclude / default-members / WS005 / WS007 / WS031 |
| `WorkspaceProjectInheritanceTests.cs` | `version.workspace = true` / WS032 / WS033 / 不允许共享的字段 |
| `WorkspaceDependencyInheritanceTests.cs` | `dep.workspace = true` / 表形式带 optional / WS034 / WS035（旧语法）|
| `MemberForbiddenSectionsTests.cs` | WS003（profile / workspace 段在 member 里） |
| `VirtualManifestTests.cs` | 根含 `[project]` 时 WS036 |
| `PathTemplateExpanderTests.cs` | 4 变量展开 / `$$` 转义 / WS037 / WS038 / WS039 / 字段白名单 |

每个测试类至少 5 个 case（正常 + 边界 + 错误）。

### 与现有代码的协同

[src/compiler/z42.Project/ZpkgReader.cs](../../../src/compiler/z42.Project/ZpkgReader.cs) 与 [src/compiler/z42.Project/ZpkgWriter.cs](../../../src/compiler/z42.Project/ZpkgWriter.cs) 不动 —— C1 仅在 manifest 解析层增加能力，不触及 zbc 二进制。

现行的"单 manifest 模式"（`<name>.z42.toml` 独立工程）继续走原解析路径；workspace 模式走新 `ManifestLoader`。两条路径在编译器入口（`PackageCompiler` / `Driver`）汇合为统一的 `ResolvedManifest`。

---

## Testing Strategy

### 单元测试

按上表 6 个测试类执行；每个错误码至少 1 个 case；正常路径至少 1 个 case；边界至少 1 个 case。

### Golden test

新增 `examples/workspace-basic/`：

```
workspace-basic/
├── z42.workspace.toml
├── libs/
│   └── greeter/
│       ├── greeter.z42.toml
│       └── src/Greeter.z42
└── apps/
    └── hello/
        ├── hello.z42.toml
        └── src/Main.z42
```

C1 阶段不需要端到端编译跑通（缺集中产物 + include 等），但 manifest 解析必须通过。可加 `examples/workspace-basic/expected_resolved.json` 作为 manifest 解析结果的 golden。

### 集成验证

C1 阶段不修改 z42c CLI 行为，验证仅限：

```bash
dotnet build src/compiler/z42.slnx                 # 编译通过
dotnet test src/compiler/z42.Tests/                 # 全绿
./scripts/test-vm.sh                                # 不应受影响（C1 不动 VM）
```

---

## Migration Notes

C1 阶段尚无现存 z42 monorepo 工程使用旧 workspace 语法（z42 仍处探索期，stdlib 也只是 Wave 5 占位），因此**不需要迁移脚本**。

所有现有的单工程 `<name>.z42.toml` 继续按"无 workspace"路径解析，行为完全不变。

---

## Open Risks

| 风险 | 缓解 |
|------|------|
| Tomlyn 对 `version.workspace = true` 这种点路径子表的解析支持 | 解析后手动后处理：检测 `XxxField` 是否为 `{ workspace = true }` 表 |
| glob 展开在跨平台路径分隔符的差异 | 统一规范化为 `/`，落到 `Path.GetFullPath` 时再交由 .NET 处理 |
| `default-members` 与 `-p` / `--workspace` 的优先级仍待 C4 落实 | C1 只解析字段；CLI 行为在 C4 spec 明确 |

---

## C2/C3/C4 衔接预留

C1 数据模型已为后续预留扩展点：

| 预留 | 说明 |
|------|------|
| `ResolvedManifest.Origins` 字典 | C2/C3 可补充 `IncludePreset` / `Policy` 来源类型 |
| `ManifestLoader` 阶段化设计 | C2 在"workspace 共享继承"和"member 字段合并"之间插入 include 链展开 |
| `WorkspaceManifest` 留 `[policy]` / `[workspace.build]` 解析占位（C1 解析但不应用）| 避免 C3 阶段再调整 schema 类型 |
| 错误码段位（WS001-007 / WS010-019 / WS020-029 / WS030-039） | 各 spec 互不冲突 |
