# z42.Bench

## 职责

C# 编译器吞吐基准 (BenchmarkDotNet)。测量 Lex / Parse / TypeCheck / Codegen 四个阶段的耗时与内存分配，按输入规模（small / medium）分组。

不属于回归测试体系；仅在显式 `just bench-compiler` / `dotnet run -c Release` 时编译运行。

## 核心文件

| 文件 | 职责 |
|------|------|
| `Program.cs` | BDN 入口 (`BenchmarkSwitcher.FromAssembly(...).Run(args)`) |
| `CompileBenchmarks.cs` | 4 个 `[Benchmark]` 方法 + 输入参数化 (`Input` ∈ {small, medium}) + GlobalSetup |
| `Inputs/small.z42` | ~50 行 z42 源；纯算术 + 控制流 + 递归（无 stdlib 依赖） |
| `Inputs/medium.z42` | ~250 行 z42 源；同样 stdlib-free，覆盖更多 helper / 边界 |

## 入口点

- `dotnet run --project src/compiler/z42.Bench -c Release -- [BDN args]`
- `just bench-compiler [args]` —— 包装上面的命令，shebang recipe + `set -f` 防止 shell glob 展开
- `just bench-compiler-all` —— 跑全部 benchmark，等价于 `--filter '*'`

常用 BDN 参数：

| 参数 | 用途 |
|------|------|
| `--filter '*'` | 跑所有 benchmark |
| `--filter '*Lex*'` | 子串过滤 |
| `--list flat` | 仅列出，不运行 |
| `--job dry` | 每 benchmark 1 次 iter（快速 sanity check，~15 s 全跑） |
| `--job short` | 减少 iter 但仍含统计（~1 min 全跑） |
| `--job medium` 默认 | 完整统计（~3-5 min 全跑） |

## 依赖关系

→ z42.Core / z42.Syntax / z42.Semantics / z42.IR / z42.Pipeline （所有编译器层）

## 输入文件约定

- 必须 stdlib-free（仅 int 原语 + 控制流）；避免 stdlib 加载噪声进入基线
- 编译产出复制到 bin output dir：`<None Include="Inputs\**\*.z42" CopyToOutputDirectory="PreserveNewest" />`
- 新增 size：扔个 `Inputs/<NAME>.z42`，再在 `CompileBenchmarks.InputSources` 加该名

## 后续

- P1.D：CI 加 `just bench-compiler-all` step + baseline diff（与 Rust criterion 共用 baseline 框架）
- 长期：在 Inputs/ 加 large（~5000 行，过程生成 stress test）
- 长期：替换 `IrGen.Generate` 为完整 pipeline `Compile` + `IrPassManager` 子分阶段拆分
