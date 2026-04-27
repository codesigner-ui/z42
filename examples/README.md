# examples

## 职责

z42 语言示例集合：单文件演示 + 工程化 workspace 模板。新接手者用来快速理解
语法面貌；维护者用来回归常见组合场景。

## 单文件示例

| 文件 | 演示主题 | 当前状态 |
|------|---------|---------|
| `hello.z42` | 最小程序：`namespace`、`using`、字符串插值、`Greet` 函数 | ✅ Phase 1 可跑 |
| `types.z42` | 基本类型与变量（sbyte/byte/short/int/long、float/double、char、string、bool） | ✅ Phase 1 可跑 |
| `oop.z42` | 类、接口、`record`、继承 | ✅ Phase 1 可跑 |
| `patterns.z42` | `enum` 与模式匹配 / discriminated union | ✅ Phase 1 可跑 |
| `string_members.z42` | `string.Length` / `string.IsEmpty`（M7 stdlib 成员演示） | ✅ Phase 1 可跑 |
| `exceptions.z42` | `try / catch / throw` 异常处理（Phase 2 将替换为 `Result<T, E>` + `?`） | ✅ Phase 1 可跑 |
| `generics.z42` | 泛型类、Lambda、委托、LINQ 风格 | 🚧 设计目标；泛型基础已落地（L3-G1/G3a/G4h），完整跑通需 L3 收尾 |
| `async.z42` | `async / await`、`Task<T>`、`CancellationToken` | 📋 L3 设计目标，**当前不可执行** |

> ✅ = 当前编译器/VM 可端到端执行；🚧 = 部分特性可用，整体待完善；📋 = 仅作语法
> 设计参考，编译器尚未支持。

### 运行单文件示例

```bash
# 编译为 .zbc，再用 VM 执行
dotnet run --project src/compiler/z42.Driver -- examples/hello.z42 --emit zbc -o /tmp/hello.zbc
cargo run --manifest-path src/runtime/Cargo.toml -- /tmp/hello.zbc
```

> `examples/hello.z42.toml` 是单文件项目清单的演示（z42.toml 格式参考）；
> `examples/hello.z42ir.json` 是早期 debug IR 的 legacy 产物（formats.rs 注释
> 仍提及，但 VM loader 现仅接 `.zbc` / `.zpkg`）。

## Workspace 模板

`workspace-*` 演示多 member、跨包依赖、policy、include preset 等工程化场景。
每个目录有自己的 `z42.workspace.toml`（顶部注释说明目的）。

| 目录 | 演示主题 |
|------|---------|
| `workspace-basic/` | 最小 workspace：virtual manifest + members glob + default-members + 共享元数据 + 集中 dependencies |
| `workspace-full/` | 多 member 跨依赖：`hello` → `utils` → `core` 三层 lib + app 混合 |
| `workspace-with-policy/` | `[policy]` 强制策略 + `[workspace.build]` 集中产物布局 |
| `workspace-with-presets/` | `[include]` 机制：member 显式拉取共享 preset 配置块 |

### 运行 workspace 示例

```bash
cd examples/workspace-basic
dotnet run --project ../../src/compiler/z42.Driver -- build --workspace
```

> workspace 工程化命令（`info` / `build --workspace` / `build -p <member>`）
> 由 C4 提案逐步落地，部分子命令可能仍在路上 —— 各 `z42.workspace.toml`
> 顶部注释会标明所需里程碑。

## 关联文档

- 语言整体语法：[../docs/design/language-overview.md](../docs/design/language-overview.md)
- 工程文件 / 项目格式：[../docs/design/project.md](../docs/design/project.md)
- Phase / 里程碑路线：[../docs/roadmap.md](../docs/roadmap.md)
