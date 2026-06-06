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

### D4: `Std.*` 命名空间保留（hard error）
**非 `z42.*` 包**声明 `namespace Std.*`（或 `Std.Exceptions` 等）→ **编译错误**（如 E0918）。
理由：防止第三方 shadow stdlib（Rust 同样保留 `std`/`core`/`alloc`）。强度选**硬错误**（保留语义要硬，否则
auto-import 的"Std.* 一定是官方 stdlib"假设不成立）。

### D5: 关键——区分用户项目 vs stdlib 自身 inter-deps
stdlib 包**自己**为 workspace build 排序声明 inter-dep（z42.io → z42.time，编 z42.io 需 z42.time.zpkg 先在）。
这些**必须保留**。所以 WS013 的规则是：
> **声明方是非 `z42.*` 包**时才警告。`z42.*` 包声明 `z42.*` 依赖 = 合法 inter-dep，不警告。

（镜像 WS012 对 `.test.` / `.bench.` 合成 harness 的豁免逻辑。）

## Implementation Notes
- WS013 注册：`WorkspaceCatalog.cs` 的 `Z42Errors` 字典（WS012 旁）；发射点 = WS012 同处的 manifest lint
  （遍历 `[dependencies]` / `[tests.dependencies]` entries 时，`declarer.Name` 不以 `z42.` 开头 + entry 名以
  `z42.` 开头 → 发 WS013）。
- E0918（Std.* 保留）：namespace 校验点（`SingleFileCompiler.cs` / `BuildTarget.cs` 编译单元处理 namespace 时，
  或 semantic 层），`package.Name` 非 `z42.*` + 声明的 namespace 以 `Std` 开头 → 报错。具体注入点 impl 时 pin。
- WS013 / E0918 的具体码号 impl 时在注册表取下一个空号确认。

## Testing Strategy
- **WS013 触发**：用户项目（name 非 z42.*）`[dependencies] "z42.io"` → WS013。
- **WS013 不触发**：stdlib 包 z42.io 的 `[dependencies] "z42.time"`（inter-dep）→ 无警告。
- **E0918**：用户包声明 `namespace Std.Foo` → 编译错误；`namespace Std.*` 在 z42.* 包内 → 通过。
- **auto-import 回归**：空 `[dependencies]` + `using Std.IO/Std.Math/Bencher` → 编译通过（已验，作回归）。
- C# 单测（z42.Tests）+ GREEN（dotnet test + cargo test + vm/lib）。
