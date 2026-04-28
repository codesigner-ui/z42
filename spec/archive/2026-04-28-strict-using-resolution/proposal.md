# Proposal: strict-using-resolution — using 严格按 package 解析 + 命名冲突诊断

## Why

当前实现：编译器把所有已加载 zpkg 的所有 namespace 全部自动注入符号表，
`using` 声明只用于写 dep map 和发警告，不参与可见性过滤。后果：

1. 第三方包若声明 `namespace Std;` 或 `namespace Std.Foo`，类会被静默
   注入，可能覆盖 core 类型，无任何报错
2. 用户漏写 `using Std.IO;` 也能直接用 Console（隐式可见），与 stdlib.md
   "z42.io 必须 using 才加载" 的设计不一致
3. 多包导出同名类型时 first-wins 静默通过，重命名 / 重构时易踩坑
4. PackageCompiler 已写好的 `LoadForUsings` 是 dead code，design 与实现脱节

## What Changes

- **z42.core 是唯一隐式 prelude**：硬编码 package name 白名单，自动激活
- 其他所有包（含 stdlib z42.io / z42.collections / z42.text / z42.math /
  z42.test）必须显式 `using <namespace>;` 才能激活
- `using X;` 激活所有声明了 `namespace X` 类型的非 prelude 包
- **类型名冲突 = 编译错误**：两个被激活的包声明同一 `(namespace, class-name)` →
  报 E0210（NamespaceCollision），列出两个 package 路径
- **未解析 using = 编译错误**（升级自警告）：报 E0211（UnresolvedUsing）
- **保留前缀软警告**：非白名单包声明 `namespace Std` / `Std.*` → 报 W0212
  （ReservedNamespace），不阻断构建（避免破坏外部包调试），但建议改名
- `LoadForUsings` 接到 PackageCompiler / SingleFileCompiler 主路径，
  替代 `LoadAll`；删除 ImportedSymbolLoader 内的硬编码 `allowedNs.Add("Std")`
  （改为通过 prelude 包列表传入）

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Pipeline/PackageCompiler.cs` | MODIFY | LoadExternalImported 改用 cu.Usings + prelude；接 LoadForUsings |
| `src/compiler/z42.Pipeline/SingleFileCompiler.cs` | MODIFY | 同上 |
| `src/compiler/z42.Semantics/TypeCheck/ImportedSymbolLoader.cs` | MODIFY | 删除硬编码 Std 注入；加 (pkg, ns, name) 冲突检测；返回 collisions |
| `src/compiler/z42.Project/PackageMeta.cs` (TBD locate) / ZpkgReader.cs | MODIFY | 暴露 zpkg → packageName 映射给 Loader |
| `src/compiler/z42.Pipeline/PackageCompiler.cs` (TsigCache) | MODIFY | TsigCache 持有 path → packageName |
| `src/compiler/z42.Core/Diagnostics/DiagnosticCodes.cs` | MODIFY | 新增 E0210 / E0211 / W0212 |
| `src/compiler/z42.Core/PreludePackages.cs` | NEW | 硬编码 prelude 包名单（`{ "z42.core" }`）|
| `src/compiler/z42.Tests/ImportedSymbolLoaderTests.cs` | MODIFY | 加冲突 + prelude + unresolved 用例 |
| `src/compiler/z42.Tests/UsingResolutionTests.cs` | NEW | 端到端 using 解析行为 |
| `src/runtime/tests/golden/run/*` | MODIFY | 给所有用到 Std.IO / Std.Math / Std.Text / Std.Collections 次级集合的 test 补 using |
| `docs/design/namespace-using.md` | MODIFY | 重写"多文件编译"章节为生效语义 |
| `docs/design/stdlib.md` | MODIFY | "Module Auto-load Policy" 表格说明 prelude 白名单机制 |
| `docs/design/error-codes.md` | MODIFY | 加 E0210 / E0211 / W0212 |

**只读引用：**
- `docs/design/philosophy.md` — 确认 "implicit prelude" 表述
- `src/compiler/z42.Pipeline/PipelineCore.cs` — 理解 cu.Usings 流向
- `spec/archive/2026-04-28-fix-using-prelude-include/tasks.md` — 撤销该 hack 的来由

## Out of Scope

- `using Alias = Foo.Bar;` 类型别名（仍按 namespace-using.md 记录但不解析）
- 把 List/Dictionary 从 z42.core 的 Std.Collections 移走（保持物理驻留与
  stdlib.md 当前定义一致）
- 多版本 / SemVer 冲突（包管理器层面，不在本变更）
- IDE / LSP 集成（独立 backlog）

## Open Questions

无（决策已确认 2026-04-28：z42.core 唯一 prelude 硬编码；W0212 软警告；
golden test 一次性补齐 using；不调整 List/Dictionary 物理位置）。
