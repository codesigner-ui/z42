# Spec: z42c 自举编译器骨架（B0）

## ADDED Requirements

### Requirement: src/z42c workspace 与 7 子包骨架

#### Scenario: workspace 编译产出 7 个 zpkg
- **WHEN** 在 `src/z42c/` 执行 `z42c build --workspace --release`（经 `z42 xtask.zpkg build compiler-z42`）
- **THEN** 拓扑序编译 `core / ir / syntax / project / semantics / pipeline / driver` 7 子包
- **AND** 在 `artifacts/build/z42c/<member>/release/dist/` 产出 `z42c.core.zpkg` / `z42c.ir.zpkg` / `z42c.syntax.zpkg` / `z42c.project.zpkg` / `z42c.semantics.zpkg` / `z42c.pipeline.zpkg` / `z42c.driver.zpkg`
- **AND** 无编译错误

#### Scenario: 兄弟依赖跨包解析
- **WHEN** 编译 `syntax`（声明 dep `z42c.core`）与 `pipeline`（声明 dep core/syntax/semantics/ir/project）
- **THEN** 同 workspace 内先编译的依赖产物被自动发现，跨包编译通过

#### Scenario: driver 是 exe 且无桥接
- **WHEN** 运行 `z42c.driver.zpkg`（`z42 run` / launcher）
- **THEN** 打印 banner（含 `z42c (self-host)` + 版本 + `bootstrap skeleton; no commands implemented yet`）并以 exit 0 结束
- **AND** 不调用 dotnet / C# z42c.dll 作任何 fallback

### Requirement: workspace 兄弟包从当前 workspace 解析（编译器根因修复）

#### Scenario: 输出到非 libraries 根的 workspace 解析声明的兄弟依赖
- **WHEN** 构建一个输出到 `artifacts/build/<X>/`（X ≠ libraries）的 workspace，其某成员在 toml 声明了对兄弟成员的依赖
- **THEN** 该成员编译时能从**当前 workspace** 的成员产物解析到已声明的兄弟依赖（不再仅限 `artifacts/build/libraries/`）
- **AND** 未在 toml 声明的兄弟包**不可见**（`declaredDeps` 过滤）

#### Scenario: orchestrator 透传兄弟 dist 目录
- **WHEN** `WorkspaceBuildOrchestrator.Build` 编译各成员
- **THEN** 把全体成员的 `EffectiveDistDir`（排序去重）透传给每个成员的编译（`CompileMember` 第 3 形参）

#### Scenario: stdlib 等既有 workspace 零字节漂移
- **WHEN** 构建输出已落在被扫描根（`artifacts/build/libraries/`）的 workspace（如 stdlib）
- **THEN** 追加的兄弟目录经规范化 full-path 去重后**不新增条目、libsDirs 顺序不变** → 既有产物字节不变

### Requirement: xtask compiler-z42 dispatch

#### Scenario: build compiler-z42
- **WHEN** `z42 xtask.zpkg build compiler-z42`
- **THEN** 编译 `src/z42c/` workspace（release），成功返回 0

#### Scenario: test compiler-z42 smoke 通过
- **WHEN** `z42 xtask.zpkg test compiler-z42`
- **THEN** 先编译 workspace，再断言 7 个 `z42c.<sub>.zpkg` 全部存在，返回 0

#### Scenario: test compiler-z42 检测缺失产物
- **WHEN** 7 个 zpkg 中任一缺失时执行 `test compiler-z42`
- **THEN** 以非零退出码失败（gate 有效性回归）

#### Scenario: build all 级联包含 compiler-z42
- **WHEN** `z42 xtask.zpkg build all`
- **THEN** 依次构建 runtime + compiler(C#) + stdlib + compiler-z42，任一失败即停并返回其退出码

### Requirement: 既有 GREEN gate 零回归

#### Scenario: 默认 test gate 不含 compiler-z42
- **WHEN** `z42 xtask.zpkg test`（默认 all gate）
- **THEN** 仍跑 compiler + vm + cross-zpkg + stdlib，全绿
- **AND** **不**包含 compiler-z42（z42c-selfhost 为 0.3.x 期间 opt-in soak）

## IR Mapping
无（本变更不新增 IR 指令 / 不改 zbc 格式；占位代码用既有语言子集）。

## Pipeline Steps
本变更不触及编译器 pipeline 实现，仅新增 z42 源码树 + 构建 dispatch：
- [ ] Lexer — 不涉及
- [ ] Parser / AST — 不涉及
- [ ] TypeChecker — 不涉及
- [ ] IR Codegen — 不涉及
- [ ] VM interp — 不涉及（占位代码经现有 pipeline 正常编译/运行）
