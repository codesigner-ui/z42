# Design: B0 — z42c 自举编译器骨架

## Architecture

```
src/
├── compiler/                       ← C# bootstrap（0.3.x 仍 default 编译器）
└── z42c/                           ← 新增：z42 自举编译器（独立 workspace）
    ├── z42.workspace.toml
    ├── README.md
    ├── core/        → z42c.core.zpkg      (lib)   镜像 z42.Core
    ├── ir/          → z42c.ir.zpkg        (lib)   镜像 z42.IR
    ├── syntax/      → z42c.syntax.zpkg    (lib)   镜像 z42.Syntax  (Lexer+Parser+AST)
    ├── project/     → z42c.project.zpkg   (lib)   镜像 z42.Project (manifest reader)
    ├── semantics/   → z42c.semantics.zpkg (lib)   镜像 z42.Semantics (TypeCheck+Codegen)
    ├── pipeline/    → z42c.pipeline.zpkg  (lib)   镜像 z42.Pipeline (编排)
    └── driver/      → z42c.driver.zpkg    (exe)   镜像 z42.Driver  (= z42c 入口别名)
```

**包间依赖图（镜像 C# `src/compiler/README.md` 邻接表）**：

```
core ──────────────► (无依赖)
ir   ──────────────► (无依赖)
syntax    ◄── core
project   ◄── ir
semantics ◄── core, syntax, ir
pipeline  ◄── core, syntax, semantics, ir, project
driver    ◄── pipeline, ir, core
```

`driver` 是唯一 exe；其余 6 个 lib。用户入口 `z42c` = `z42c.driver.zpkg` 别名。

**构建数据流（骨架阶段：占位，无真实逻辑）**：`z42c build --workspace --release`（在 `src/z42c/`）→ 拓扑序编译 7 子包 → `artifacts/build/z42c/<member>/<profile>/dist/z42c.<sub>.zpkg`。

## Decisions

### Decision 1：根目录 `src/z42c/`（消解命名冲突）
**问题：** User 说 `src/z42c`，父规划 2026-06-06 决议写 `src/z42.compiler/`。
**决定：** 用 `src/z42c/`（User 2026-06-07 裁决覆盖旧决议），保留 7 子包 1:1 镜像，产物 `z42c.<sub>.zpkg`。同步改正 roadmap + 父 proposal（单一真相来源）。

### Decision 2：独立 workspace，不并入 stdlib
**问题：** z42c 子包放进 `src/libraries/` 还是独立？
**决定：** 独立 `src/z42c/z42.workspace.toml`（`members=["*"]`，`[workspace.project].version="0.1.0"`，独立 versioning）。stdlib workspace 不动。理由：自举树与 stdlib 解耦，A 主线重组不冲击 B 代码；与父规划"独立顶级目录"一致。

### Decision 3：命名空间镜像 C# `Z42.*`
**决定：** `Z42.Core` / `Z42.Syntax` / `Z42.IR` / `Z42.Project` / `Z42.Semantics` / `Z42.Pipeline` / `Z42.Driver`，与 C# 项目命名空间逐一对应，便于 1:1 阅读对照。注意：byte-identical 目标是 **z42c 编译用户代码产出的字节** 与 C# z42c 一致，**不**要求 z42c 自身源码/内部名与 C# dll 相同——镜像命名纯为可维护性。

### Decision 4：受限写法约定（self-hosting.md 固化，全子系统遵守）
**决定：**
- **AST / IR 节点**：`class` 继承层级 + `virtual` 方法 / 抽象 `Visitor` 基类 dispatch；**不**用 record + `match`（match 排 0.7.x）。具体节点抽象形态留 syntax spec（参考 [[D-11]]）。
- **集合变换**：`for` / `foreach` + 显式累积；**不**用 LINQ（排 0.6.x）。
- **错误路径**：`throw` / `try-catch` + Exception 子类；**不**用 `Result<T,E>` + `?`（排 0.7.x）。
- **泛型**：用已落地 G1-G4 + 闭包核心；遇关联类型（G3a）等延后特性按"真卡点"处理。
**dogfood 规则：** 写到某处今天的 z42 子集**无法表达**（不是不优雅）→ 停下汇报，判定 L1/L2 可补 / 必须拉 L3，**禁止在编译器代码里写 workaround**（[[feedback_dogfood_fill_gaps]]）。骨架阶段占位代码只用最基础子集，不触发。

### Decision 5：无桥接 CLI parity
**问题：** driver 骨架是否 fallback 到 dotnet z42c.dll 以"先能跑"？
**决定：** **否**。driver `Main()` 只打印 banner（`z42c (self-host) 0.1.0 — bootstrap skeleton; no commands implemented yet`）。命令逐子版本解锁：lex/parse/manifest-check 起于 0.3.4，build 起于 0.3.9。绝不调 C# 实现作 fallback（父规划"无桥接"）。

### Decision 6：byte-identical gate 本变更不激活
**问题：** 骨架无真实产物，gate 测什么？
**决定：** `test compiler-z42` 在 B0 只做 **smoke**：编译 workspace + 断言 7 个 `z42c.<sub>.zpkg` 全部产出。逐字节对账（vs C# 产物）等 0.3.3 core+syntax 有真实输出再上，默认 per-PR 触发。

### Decision 7：workspace 兄弟包解析（自举 dogfood 发现 #1 — 根因修复）
**问题：** `BuildLibsDirs`（[PackageCompiler.BuildTarget.cs:257](../../../../src/compiler/z42.Pipeline/PackageCompiler.BuildTarget.cs#L257)）把 `artifacts/build/libraries/` 硬编码为唯一会扫描的 workspace 布局根。stdlib 兄弟依赖能解析纯因 stdlib 恰好输出到那里；z42c 输出到 `artifacts/build/z42c/`，`z42c.syntax` 找不到 `z42c.core`。
**裁决（User 2026-06-07）：** **根因修复，从当前 workspace 解析已在 toml 声明的兄弟依赖**（本地项目都能找到；远程下载依赖留后续，现无整体包管理）。**不用 Z42_LIBS 绕过。**
**实现（精准、隔离、零字节漂移）：**
- `WorkspaceBuildOrchestrator.Build` 收集本 workspace 全体成员的 `EffectiveDistDir`（排序去重）→ 透传给每个成员的编译。
- 链路 `CompileMember(Func 增第 3 形参)` → `RunResolved(workspaceLibDirs)` → `BuildTarget(workspaceLibDirs)` → `BuildLibsDirs(projectDir, workspaceLibDirs)`。
- `BuildLibsDirs` 在既有扫描**之后**、按**规范化 full-path 去重**追加这些目录，并**排序**（common-pitfalls §1 确定性）。
- **零字节漂移保证**：stdlib 等已输出到被扫描根的 workspace，其成员 dist 早已在 `dirs` 里 → 规范化去重后**不新增任何条目、顺序不变** → nsMap / BuildDepIndex 内容与顺序不变。只有输出到别处的 workspace（z42c）才真正新增兄弟目录。
- **依赖必须在 toml 声明**：`ScanLibsForNamespaces` 的 `declaredDeps` 过滤保证只有声明的兄弟包可见（未声明即不可解析）。z42c 各子包已在 manifest 声明兄弟依赖。
- **作用域隔离**：单工程构建 `workspaceLibDirs=null` → 行为完全不变；非 workspace 路径零影响。
> 远程/下载依赖（registry / git URL）= 明确 out-of-scope（features 未定整体包管理），后续单独 spec。

### Decision 8：xtask dispatch
**决定：** `scripts/xtask.z42` 内联两个 helper（无需新源文件）：
- `_buildCompilerZ42()`：mirror `_buildStdlibCore` 形态，对 `src/z42c/` 跑 `z42c build --workspace --release`。
- `_testCompilerZ42()`：先 `_buildCompilerZ42()`，再断言 `artifacts/build/z42c/*/release/dist/z42c.*.zpkg` 7 个齐全；缺任一 → 非零退出。
- `_build` 路由加 `compiler-z42`；`_test` 路由加 `compiler-z42`；`build all` 末尾追加（runtime + compiler + stdlib + **compiler-z42**）；help 补两行。
- **不**纳入默认 `test`（all）gate——0.3.x 期间 z42c-selfhost 是 opt-in soak，对既有 GREEN 零干扰。

## Implementation Notes

- 占位 `.z42` 文件：`namespace Z42.<Sub>; class <Sub>Skeleton { string Name() { return "z42c.<sub>"; } }`（最小可编译，无新特性）。
- driver `Main.z42`：`using Std;` + `Console.WriteLine(banner)` + `Environment.Exit(0)`。
- workspace `default-members` 必须拓扑序（core/ir 先于依赖方），即便 z42c 会 topo-sort，显式序作 fallback（沿用 stdlib workspace 约定 + [[reference_xtask_test_standalone_vm_path]] 类确定性原则）。
- 子包 manifest `[dependencies]` 用 `"z42c.core" = "0.1.0"` 形态声明兄弟依赖。

## Testing Strategy

- **骨架编译**：`z42 xtask.zpkg build compiler-z42` → 7 个 `z42c.<sub>.zpkg` 产出（无编译错误）。
- **driver 可运行**：`z42 z42c.driver.zpkg`（或 `z42 run`）打印 banner，exit 0。
- **smoke test**：`z42 xtask.zpkg test compiler-z42` 断言 7 zpkg 齐全；人为删一个 → 失败（验证 gate 有效）。
- **无回归**：完整 `z42 xtask.zpkg test`（默认 gate，不含 compiler-z42）仍全绿——证明自举树对既有工作零影响。
- **依赖解析**：syntax（dep core）/ pipeline（dep 5 个）能跨包编译通过，验证 workspace 兄弟依赖发现。
