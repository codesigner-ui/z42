# Proposal: z42.toml 添加 `[tests]` / `[bench]` 段 + 多文件测试约定

> 状态：📋 DRAFT（2026-06-06）｜类型：lang/compiler/build feat｜责任人：User + Claude

## Why

三个问题驱动：

1. **test-only deps 泄漏**：z42.test 现在必须出现在 22 个 stdlib 包的 `[dependencies]` 段，导致它（连同其依赖图）进入 release zpkg 元数据 — 等价 Cargo 不区分 `[dependencies]` 与 `[dev-dependencies]`，是产品质量倒退。
2. **多文件测试无路可走**：单文件测试 OK（`tests/foo.z42`），但当一个测试需要辅助代码（fixture builder / mock / shared assert）时，目前要么把辅助代码塞进 `src/`（污染产品 API），要么手工合并到一个巨大的 `.z42` 文件。dist 测试已有 dir-mode（`<name>/source.z42`），但 unit 测试 / bench / examples 没沿用。
3. **per-package bench 缺位**：bench 完全没有 per-package 维度，全靠 `bench/scenarios/*.z42` 全局位置。0.3.B 主线要在每个 z42.compiler.<sub> 子包写 lexer/parser throughput micro-bench，没有 schema 支撑。

## 设计

### 约定（无需 z42.toml 改动；自动发现）

```
<package>/
├── z42.toml
├── src/                              ← 产品代码
├── tests/
│   ├── foo_basic.z42                 ← 单文件测试（每文件独立编译 + 独立 run）
│   ├── bar_errors.z42                ← 同上
│   └── integration_roundtrip/        ← 多文件测试（dir-mode）
│       ├── source.z42                ← 入口（含 Main）
│       ├── helpers.z42               ← 同 dir 自动 include
│       └── fixtures/                 ← 非 .z42 数据文件（运行时相对路径读取）
├── bench/
│   ├── lexer_throughput.z42          ← 单文件 bench
│   └── e2e_pipeline/                 ← 多文件 bench（与测试同构）
│       └── source.z42
└── examples/                          ← 可选；约定预留（本 spec 不做实现）
```

**约定规则**：

1. `tests/*.z42` 顶层文件 → 各自独立测试程序
2. `tests/<name>/source.z42` 入口 + 同目录所有 `*.z42`（**递归**包含子目录）→ 合成一个多文件测试程序
3. `bench/*.z42` 与 `bench/<name>/source.z42` → 同 1/2 规则
4. dir-mode 的子目录 `tests/<name>/fixtures/` 等若不含 `*.z42` → 仅作数据载体；含 `*.z42` 则递归 include 到该测试编译单元

dir-mode 与现有 dist 测试 `src/tests/<cat>/<name>/source.z42`（[xtask_test_dist.z42:241](../../../../scripts/xtask_test_dist.z42#L241)）同构，复用同一发现 + 编译逻辑。

### z42.toml 新增 schema

```toml
[project]
name = "z42.text"
version = "0.1.0"
kind = "lib"

[dependencies]
"z42.core" = "0.1.0"

# 新增段 1: [tests] —— 默认 tests/ 目录的统一配置
[tests]
# include / exclude 仅在偏离约定时需要：
# include = ["tests/*.z42", "tests/*/source.z42"]
# exclude = ["tests/_skip/*"]

# tests-only deps（不进 release 产物，等价 Cargo [dev-dependencies]）
[tests.dependencies]
"z42.test" = "0.1.0"

# 新增段 2: [bench] —— 默认 bench/ 目录的统一配置
[bench]
[bench.dependencies]
"z42.test" = "0.1.0"   # Bencher 在 z42.test 中

# 新增段 3: [[test]] —— 显式覆盖（非约定布局时用）
[[test]]
name = "compile_perf_e2e"
src  = "tests/perf/runner.z42"                      # 入口
sources = ["tests/perf/*.z42", "tests/perf/_lib/*.z42"]   # 显式 include 集（可选；约定下 dir-mode 自动）
[test.dependencies]                                  # 该 test 独享 deps
"z42.compression" = "0.1.0"

# 新增段 4: [[bench]] —— 显式覆盖
[[bench]]
name = "manifest_parse"
src  = "bench/manifest_parse.z42"
```

**KnownTopLevelKeys 扩充**：`"tests"`, `"bench"`, `"test"`（数组）, `"benchmark"`（数组）。注意 `[[test]]` 而非 `[[tests]]` 与 Cargo 风格一致。

**字段约束**：

| 段 | 字段 | 类型 | 必填 | 说明 |
|---|------|:---:|:---:|------|
| `[tests]` | `include` | string[] | 否 | 默认 `["tests/*.z42", "tests/*/source.z42"]` |
| `[tests]` | `exclude` | string[] | 否 | glob |
| `[tests.dependencies]` | `<name>` | string | 否 | dev-dep；不合入 release 产物 |
| `[bench]` | 同 `[tests]` | — | — | — |
| `[[test]]` | `name` | string | **是** | 唯一标识（用于 filter） |
| `[[test]]` | `src` | string | **是** | 入口路径（相对包根） |
| `[[test]]` | `sources` | string[] | 否 | 额外 include glob；省略时仅编译 src |
| `[[test]].dependencies` | `<name>` | string | 否 | 该 test 独享 deps |
| `[[bench]]` | 同 `[[test]]` | — | — | — |

### 新增 ManifestErrors

| 码 | 文案 | 触发 |
|---|------|------|
| WS010 | `warning[WS010]: [tests] / [bench] dependencies referenced in [dependencies] — move to [{tests/bench}.dependencies] to avoid leaking into release zpkg` | `z42.test` 等出现在 `[dependencies]` |
| E0410 | `error[E0410]: [[test]] missing required field 'name'` | `[[test]]` 无 name |
| E0411 | `error[E0411]: [[test]] missing required field 'src'` | `[[test]]` 无 src |
| E0412 | `error[E0412]: [[test]] / [[bench]] entries with duplicate name '<n>'` | 重名 |
| E0413 | `error[E0413]: [[test]].src '<path>' does not exist` | path 不存在 |

WS010 是 warning（pre-1.0 一次切干净；migration 期内提示但不阻塞）。E04xx 是 hard error。

### 编译模型

**dir-mode 测试合成单元**：

1. xtask 发现 `tests/<name>/source.z42`
2. 生成 synthetic mini-manifest（在 build 临时 cache 内）：
   ```toml
   [project]
   name = "<lib>.test.<name>"
   version = "0.1.0"
   kind = "exe"
   entry = "<entry from source.z42 Main>"

   [sources]
   include = ["tests/<name>/**/*.z42"]

   [dependencies]
   # 合并：父包 [dependencies] + 父包 [tests.dependencies] + 该 [[test]].dependencies
   ```
3. 复用现有 PackageCompiler 跑编译；产物落 `artifacts/build/.../tests/<lib>.test.<name>.zbc`
4. xtask test runner 调用产物

**依赖解析顺序**（三层合并）：

```
final_deps = parent.[dependencies]
          ∪ parent.[tests.dependencies]    (测试时合入；bench 时合 [bench.dependencies])
          ∪ this_[[test]].dependencies     (若有匹配 [[test]] 块)
```

冲突解决：[[test]] > [tests] > [dependencies]（精确覆盖广泛）。

**Release 产物**：`xtask build`（非 test 路径）**忽略**所有 [tests] / [bench] / [[test]] / [[bench]] 字段；release zpkg 元数据只含 [dependencies]。

## 迁移

### 22 stdlib 包

每个包的 z42.toml 加一段（合并到本 spec 一并完成）：

```diff
 [dependencies]
 "z42.core" = "0.1.0"
-"z42.test" = "0.1.0"          # ← 从这里删
 "z42.text" = "0.1.0"

+[tests.dependencies]
+"z42.test" = "0.1.0"
```

具体每包 diff 通过脚本批量生成；逐包 commit 避免一次提交 22 文件。

### 现有测试

零代码变动 — 测试发现路径不变（`tests/*.z42` 仍工作），只是 z42.test 从 [dependencies] 挪到 [tests.dependencies]，运行时编译器自动合并 deps。

### 编译器自举（B 主线）落地时直接采用新约定

- z42.compiler.syntax 的 `tests/lexer/source.z42` + helpers → 多文件测试 dir-mode
- z42.compiler.syntax 的 `bench/lexer_throughput.z42` → per-package bench 约定
- 0.3.B0 spec 不需再为此特别开 schema 分支

## 落地节奏

| 阶段 | 工作 |
|------|------|
| 1 | spec 落地（本 commit） |
| 2 | C# 编译器：ManifestErrors WS010 + E0410-E0413；ProjectManifest 解析 [tests] / [bench] / [[test]] / [[bench]]；KnownKeys + warnings 系统集成 |
| 3 | xtask：dir-mode 发现 + synthetic mini-manifest 生成 + dep 三层合并 + test/bench dispatch |
| 4 | 22 stdlib 包迁移（每包 commit） |
| 5 | C# 编译器单元测试 + xtask integration 测试 |
| 6 | GREEN check（dotnet test + xtask test stdlib） |

每阶段独立 commit；按上述顺序提交。

## 退出标准

- [ ] z42.toml 新增 schema 全部支持（W/E 码 + parser + 字段约束）
- [ ] xtask test 在新约定 + 旧约定下都全绿
- [ ] 22 stdlib z42.toml 完成 [tests.dependencies] 迁移
- [ ] 至少一个 stdlib 包改造为含 dir-mode 多文件测试 demo（推荐 z42.compression 或 z42.crypto）
- [ ] WS010 在迁移完成后扫描全 stdlib 零触发
- [ ] dotnet test + xtask test stdlib 全部绿
- [ ] docs/design/compiler/manifest.md（若存在）或对应 design doc 同步新 schema 描述

## Open Questions

无 —— 设计已在 2026-06-06 通过 AskUserQuestion 全采纳推荐方案。

## 与其他 spec 关系

- 0.3 B 主线 spec（`add-bootstrap-easy-subsystems`）将复用本 spec 落地的约定
- 0.3 A 主线（stdlib 重组）会触碰 22 个 z42.toml，与本 spec 的迁移工作可在同一 PR 完成（A1 包重组 commit 链中执行）
- features.md attribute 机制 spec（0.3.C3 前置）与本 spec 无直接耦合
