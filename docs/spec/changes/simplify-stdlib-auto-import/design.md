# Design: 标准库自动可用

## Architecture

**机制已存在，本变更把它正式化。** stdlib 的自动可用今天已经在跑：

- 编译期：`BuildDepIndex` / `ScanLibsForNamespaces` 对 `meta.Name.StartsWith("z42.")` 的包
  **无条件可见**（[Helpers.cs:158](../../../../src/compiler/z42.Pipeline/PackageCompiler.Helpers.cs) /
  [BuildTarget.cs:333](../../../../src/compiler/z42.Pipeline/PackageCompiler.BuildTarget.cs)）。
- 运行期：`build_host_module` 按 `import_namespaces` 扫 NSPC 解析，不看 DEPS 元数据。

本变更**不新增解析机制**，而是加：① 文档说明；② lint 引导"别声明 stdlib"；③ `Std.*` 命名空间保留。

## Decisions

### D1: Rust-std 模型（方向 A）
stdlib（`Std.*` 命名空间 / `z42.*` 包）= 工具链自带，**永不在 manifest 声明**；版本跟工具链走。
`[dependencies]` / `[tests.dependencies]` 只用于**第三方**依赖。

### D2: "stdlib" 的判定
- 命名空间：`Std.*`（+ `z42.core` prelude）。
- 包名：`z42.*`。
两者等价指向同一组工具链包。

### D3: 新 lint —— 冗余 stdlib 依赖声明（WS013，warning）
**非 `z42.*` 包**在 `[dependencies]` / `[tests.dependencies]` 里声明了 `z42.*` 包 → 警告"冗余，可删"。
与 WS012 同发射点（manifest lint）、方向相反（WS012 管放错位，WS013 管"根本不该声明"）。

### D4: `Std.*` 命名空间保留（hard error — E0605）
**非 `z42.*` 包**声明 `namespace Std.*`（或 `Std.Exceptions` 等）→ **编译错误 E0605**。
理由：防止第三方 shadow stdlib（Rust 同样保留 `std`/`core`/`alloc`）。强度选**硬错误**（保留语义要硬，否则
auto-import 的"Std.* 一定是官方 stdlib"假设不成立）。

**码号定为 E0605**（非 design 初稿举例的 E0918）：`Diagnostic.cs` 头注释规定
`E06xx = Package / import resolution（namespace 解析）`，本规则的 warn 对应物 W0603（ReservedNamespace）就在
E06xx 段。把硬错误放 E0605（W0604 之后的下一空号）使「保留命名空间」的 warn / error 同族相邻，而非散到 E09xx
（Native / 测试框架）。初稿「如 E0918」是占位，Implementation Notes 早写明「impl 时取下一空号确认」——结果即 E0605。

**两层防线（互补，不冗余）**：
- **E0605（源码层，硬错误）**：编译本包源码时，非 `z42.*` 包某编译单元的 `namespace` 落在 `Std`/`Std.*` 前缀下
  → 阻止构建。从源头保证这种包根本产不出来。
- **W0603（依赖扫描层，warning）**：`ScanLibsForNamespaces` 扫到一个**已构建**第三方 zpkg 的 NSPC 占用了
  `Std.*` → 警告消费方。E0605 落地后正常流程不会再产出这种 zpkg，W0603 退化为对陈旧 / 外部产物的软网。

### D5: 关键——区分用户项目 vs stdlib 自身 inter-deps
stdlib 包**自己**为 workspace build 排序声明 inter-dep（z42.io → z42.time，编 z42.io 需 z42.time.zpkg 先在）。
这些**必须保留**。所以 WS013 的规则是：
> **声明方是非 `z42.*` 包**时才警告。`z42.*` 包声明 `z42.*` 依赖 = 合法 inter-dep，不警告。

（镜像 WS012 对 `.test.` / `.bench.` 合成 harness 的豁免逻辑。）

## Implementation Notes
- WS013 注册：`WorkspaceCatalog.cs` 的 `Z42Errors` 字典（WS012 旁）；发射点 = WS012 同处的 manifest lint
  （遍历 `[dependencies]` / `[tests.dependencies]` entries 时，`declarer.Name` 不以 `z42.` 开头 + entry 名以
  `z42.` 开头 → 发 WS013）。
- E0605（Std.* 保留）：注入点 = `PackageCompiler.BuildTarget.cs` 的 `TryCompileSourceFiles` Phase-0 解析循环
  （拿到每个 CU 的 `cu.Namespace` + `cu.Span` 处），调 `CheckReservedNamespaceDeclaration(packageName, ns, span)`
  → 返回 E0605 诊断则计入 parseErrors 并跳过该文件 → 整包构建失败。判定逻辑抽成 `internal` 纯函数
  （`!PreludePackages.IsStdlibPackage(pkg) && ns != null && PreludePackages.IsReservedNamespace(ns)`），
  镜像 `FindUsingDiagnostics` 的「internal for testing」做法，便于单测免整包构建。`packageName` 经新增的
  `TryCompileSourceFiles` 参数从 `BuildTarget(name, ...)` 透传。
- 码号已 pin（见 D4）：WS013（manifest lint）、E0605（namespace 保留，E06xx namespace 段）。

## Testing Strategy
- **WS013 触发**：用户项目（name 非 z42.*）`[dependencies] "z42.io"` → WS013。
- **WS013 不触发**：stdlib 包 z42.io 的 `[dependencies] "z42.time"`（inter-dep）→ 无警告。
- **E0605**：用户包声明 `namespace Std.Foo` → 编译错误；`namespace Std.*` 在 z42.* 包内 → 通过。
- **auto-import 回归**：空 `[dependencies]` + `using Std.IO/Std.Math/Bencher` → 编译通过（已验，作回归）。
- C# 单测（z42.Tests）+ GREEN（dotnet test + cargo test + vm/lib）。
