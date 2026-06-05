# Proposal: z42.toml 添加 `[tests]` / `[bench]` 段 + 多文件测试约定

> 状态：📋 DRAFT（2026-06-06）｜类型：lang/compiler/build feat｜责任人：User + Claude

## Why

三个问题驱动：

1. **test-only deps 泄漏**：z42.test 现在必须出现在 22 个 stdlib 包的 `[dependencies]` 段，导致它（连同其依赖图）进入 release zpkg 元数据 — 等价 Cargo 不区分 `[dependencies]` 与 `[dev-dependencies]`，是产品质量倒退。
2. **多文件测试无路可走**：单文件测试 OK（`tests/foo.z42`），但当一个测试需要辅助代码（fixture builder / mock / shared assert）时，目前要么把辅助代码塞进 `src/`（污染产品 API），要么手工合并到一个巨大的 `.z42` 文件。dist 测试已有 dir-mode（`<name>/source.z42`），但 unit 测试 / bench / examples 没沿用。
3. **per-package bench 缺位**：bench 完全没有 per-package 维度，全靠 `bench/scenarios/*.z42` 全局位置。0.3.B 主线要在每个 z42.compiler.<sub> 子包写 lexer/parser throughput micro-bench，没有 schema 支撑。

## 设计

本 spec 与刚落地的 `restructure-build-output-dirs` 三字段输出模型（`output_dir` / `cache_dir` / `dist_dir`）对齐 — tests/bench 的产物作为 `output_dir` 下并列子树。

---

### 1. 约定（自动发现，无需 z42.toml 改动）

```
<package>/
├── z42.toml
├── src/                              ← 产品代码
├── tests/
│   ├── foo_basic.z42                 ← 单文件测试（每文件独立编译 + 独立 run）
│   ├── bar_errors.z42                ← 同上
│   └── integration_roundtrip/        ← 多文件测试（dir-mode）
│       ├── source.z42                ← 入口（含 Main）；约定名，不可改
│       ├── helpers.z42               ← 同 dir 自动 include（递归含子目录）
│       └── fixtures/                 ← 非 .z42 数据文件（运行时相对路径读取）
├── bench/                            ← 与 tests/ 同构
│   ├── lexer_throughput.z42
│   └── e2e_pipeline/
│       └── source.z42
└── examples/                          ← 约定预留；本 spec 不实现（future iteration）
```

**约定规则**：

1. `tests/*.z42` 顶层文件 → 各自独立测试程序
2. `tests/<name>/source.z42` 入口 + 同目录递归 `*.z42` → 合成一个多文件测试程序
3. `bench/*.z42` 与 `bench/<name>/source.z42` → 同 1/2 规则
4. 子目录非 `.z42` 文件（fixture / data）随测试产物打包，运行时 cwd 切到 `<test_dir>`，相对路径读取
5. dir 内 `_` 前缀的 `.z42` 文件（如 `_helpers.z42`）是辅助；不是 test/bench 入口

**入口名约定 `source.z42` 是硬规则** — 与 dist 测试现有 dir-mode（[xtask_test_dist.z42:241](../../../../scripts/xtask_test_dist.z42#L241)）同构，复用同一发现 + 编译路径。

---

### 2. z42.toml 配置

#### 2.1 典型 stdlib 包（90%+ 命中）

```toml
[project]
name    = "z42.json"
version = "0.1.0"
kind    = "lib"

[dependencies]
"z42.core" = "0.1.0"
"z42.text" = "0.1.0"

# 测试配置（约定 + dev-deps 隔离）
[tests]
# 字段全可省，全走约定；显式覆盖示例（少用）：
# include = ["tests/*.z42", "tests/*/source.z42"]
# exclude = ["tests/_skip/*"]

[tests.dependencies]
"z42.test" = "0.1.0"   # 仅测试合入；release zpkg 元数据**不**含

# Bench 配置（同构）
[bench]
[bench.dependencies]
"z42.test" = "0.1.0"   # Bencher 在 z42.test 包内
```

#### 2.2 复杂场景的显式覆盖

```toml
# 一个测试 / bench 路径不规则或独享 dep 时
[[test]]
name = "json_perf_corpus_1k"
src  = "tests/perf/runner.z42"
sources = ["tests/perf/*.z42", "tests/perf/_lib/*.z42"]   # 显式 include 集
[test.dependencies]
"z42.compression" = "0.1.0"           # 仅这个测试用

[[bench]]
name = "manifest_parse"
src  = "bench/manifest_parse.z42"
# 无 [bench.dependencies] → 继承 [bench.dependencies] + [tests.dependencies]
```

#### 2.3 字段语义表

| 段 | 字段 | 类型 | 默认 | 必填 | 说明 |
|---|------|:---:|------|:---:|------|
| `[tests]` | `include` | string[] | `["tests/*.z42", "tests/*/source.z42"]` | 否 | 测试源 glob |
| `[tests]` | `exclude` | string[] | `[]` | 否 | 排除 glob |
| `[tests.dependencies]` | `<pkg>` | string | — | 否 | dev-dep；仅测试编译合入 |
| `[bench]` | 同 `[tests]` | — | 路径换 `bench/` | — | — |
| `[bench.dependencies]` | `<pkg>` | string | — | 否 | bench-only dev-dep |
| `[[test]]` | `name` | string | — | **是** | 显式测试名（filter 用）|
| `[[test]]` | `src` | string | — | **是** | 入口（相对包根）|
| `[[test]]` | `sources` | string[] | dir-mode 自动 / 单文件 = `[src]` | 否 | 编译单元包含集 |
| `[[test]].dependencies` | `<pkg>` | string | — | 否 | 该 test 独享 dev-dep |
| `[[bench]]` | 同 `[[test]]` | — | — | — | — |

**KnownTopLevelKeys 扩充**：`"tests"`, `"bench"`, `"test"`（数组）, `"benchmark"`（数组）。`[[test]]` 而非 `[[tests]]` 与 Cargo 一致。

#### 2.4 用户决策树（写测试时的认知路径）

```
我要写测试 →
├── 单文件，约定够用      → 放 tests/foo.z42         （0 行 z42.toml 改动）
├── 多文件共享 helper     → 放 tests/foo/source.z42 + tests/foo/_helpers.z42  （0 行 z42.toml）
├── 需要 production 之外 dep → 写 [tests.dependencies] 加一行
└── 路径不规则 / 单 test dep → 写 [[test]] 块
```

90%+ 场景命中前两条 = **零配置**。

---

### 3. 同文件 test + bench（**本 spec 不支持**）

共享 fixture 的需求走 **dir-mode 拆文件** 方案：

```
src/libraries/z42.yaml/
├── tests/parse/
│   ├── source.z42                    ← 测试入口
│   └── _fixtures.z42                 ← YAML 测试数据构造
└── bench/parse/
    ├── source.z42                    ← bench 入口
    └── _fixtures.z42                 ← 与 tests 端复制（pre-1.0 阶段可接受）
```

未来 iteration（0.4 / 0.5）若有需要可考虑：
- 双声明 `[[test]]` + `[[bench]]` 指同一 src + `Std.Test.IsBenchMode()` 切分
- Go-style 自动双发现

→ 单独 spec，本 spec 不囊括。

---

### 4. 新增 ManifestErrors

> **代码分配**（与现有 ManifestErrors.cs 已用代码不冲突）：
> - WS012：warning，sits in 已有 WS007/WS008/WS009 warning 组旁边的空位
> - WS040-WS043：errors，新开块跟在 WS030-WS039 workspace 错误后
> - 注：z42 manifest 体系一律用 `WS` 前缀（区分严重度靠 message 里的 `error[WSxxx]:` vs `warning[WSxxx]:`），不用 `E04xx`

| 码 | 严重度 | 文案 | 触发 |
|---|:---:|------|------|
| WS012 | warning | `warning[WS012]: test-only dep '{name}' in [dependencies] — move to [tests.dependencies] / [bench.dependencies] to keep it out of release zpkg metadata` | `z42.test` 等出现在 `[dependencies]` |
| WS040 | error | `error[WS040]: [[test]] missing required field 'name'` | `[[test]]` 无 name |
| WS041 | error | `error[WS041]: [[test]] missing required field 'src'` | `[[test]]` 无 src |
| WS042 | error | `error[WS042]: [[test]] / [[bench]] entries with duplicate name '{name}'` | 重名 |
| WS043 | error | `error[WS043]: [[test]].src '{path}' does not exist` | path 不存在 |

WS012 是 warning（pre-1.0 一次切干净；migration 期内提示但不阻塞）。WS040-043 是 hard error。

---

### 5. 编译模型

#### 5.1 dir-mode 合成 mini-manifest

1. xtask 发现 `tests/<name>/source.z42`
2. 生成 synthetic mini-manifest（写到 cache 内，**不**进 git）：
   ```toml
   [project]
   name = "<lib>.test.<name>"
   version = "<parent.version>"
   kind = "exe"

   [sources]
   include = ["tests/<name>/**/*.z42"]

   [dependencies]
   # 三层合并（见 5.2）

   [build]
   output_dir = "<parent.output_dir>/tests"   # 强制；不可被 [[test]] 覆盖
   ```
3. 调用现有 PackageCompiler 跑编译；产物落 `<parent.output_dir>/tests/dist/<lib>.test.<name>.zpkg`
4. xtask test runner 调用产物（环境变量 `Z42_MODE=test`）

#### 5.2 依赖三层合并

```
final_deps = parent.[dependencies]
          ∪ parent.[tests.dependencies]    (测试时；bench 时合 [bench.dependencies])
          ∪ this_[[test]].dependencies     (若有匹配 [[test]] 块)
```

**冲突解决**：[[test]] > [tests] > [dependencies]（精确覆盖广泛）。同名同版本不算冲突。

#### 5.3 Release 产物边界

`xtask build`（非 test 路径）**忽略**所有 `[tests]` / `[bench]` / `[[test]]` / `[[bench]]` 字段；release zpkg 元数据只含 `[dependencies]`。

---

### 6. 编译产物输出目录

与 `restructure-build-output-dirs` 三字段（`output_dir` / `cache_dir` / `dist_dir`）对齐 — tests / bench 在每个 package 的 `output_dir` 下并列两个独立子树。

#### 6.1 完整目录树

```
artifacts/build/libraries/<lib>/<profile>/    ← <lib> 的 output_dir（默认根）
├── cache/                                     ← 生产构建中间产物（不变）
│   ├── <file>.zbc
│   └── ...
├── dist/                                      ← 生产分发产物（不变）
│   └── <lib>.zpkg                             ← 只放生产 zpkg
├── tests/                                     ← ★ 测试子树
│   ├── cache/                                 ← 测试编译中间产物
│   │   ├── <test_name>/                       ← 每测试独立 cache 子目录
│   │   │   └── <file>.zbc
│   │   └── ...
│   └── dist/                                  ← 测试最终产物
│       ├── <lib>.test.<test_name>.zpkg        ← 单文件测试
│       └── <lib>.test.<dir_name>.zpkg         ← dir-mode 测试
└── bench/                                     ← ★ bench 子树（与 tests 同构）
    ├── cache/
    └── dist/
        └── <lib>.bench.<bench_name>.zpkg

artifacts/build/libraries/dist/<profile>/      ← 全 stdlib 生产 zpkg 聚合（不变）
├── z42.core.zpkg
├── z42.io.zpkg
└── ...                                        ← **绝不**出现 .test. / .bench. zpkg
```

#### 6.2 路径角色 + clean / CI 矩阵

| 路径 | 角色 | `xtask clean` 命中 | CI release artifact |
|------|------|:---:|:---:|
| `<lib>/<profile>/cache/` | 生产中间产物 | `clean` | 否 |
| `<lib>/<profile>/dist/<lib>.zpkg` | **生产 zpkg** | `clean` | **✅**（聚合 → `libraries/dist/`）|
| `<lib>/<profile>/tests/cache/` | 测试中间产物 | `clean tests` 独立 | 否 |
| `<lib>/<profile>/tests/dist/*.zpkg` | 测试可执行 | `clean tests` 独立 | 否 |
| `<lib>/<profile>/bench/cache/` | bench 中间 | `clean bench` 独立 | 否 |
| `<lib>/<profile>/bench/dist/*.zpkg` | bench 可执行 | `clean bench` 独立 | 否 |
| `libraries/dist/<profile>/` | 全包聚合视图 | `clean` | **✅**（release tarball 源）|

#### 6.3 zpkg 命名规则（硬约束）

| 产物类型 | zpkg 文件名 | manifest 包名 |
|---------|-----------|--------------|
| 生产 | `z42.json.zpkg` | `z42.json` |
| 单文件测试 | `z42.json.test.parse_basic.zpkg` | `z42.json.test.parse_basic`（合成）|
| dir-mode 测试 | `z42.json.test.integration_merge_keys.zpkg` | `z42.json.test.integration_merge_keys` |
| 单文件 bench | `z42.json.bench.parse_throughput.zpkg` | `z42.json.bench.parse_throughput` |
| dir-mode bench | `z42.json.bench.e2e_pipeline.zpkg` | `z42.json.bench.e2e_pipeline` |

**`.test.` / `.bench.` infix 是硬规则** — 文件名视觉区分 + CI 守门正则的 anchor。

#### 6.4 xtask 命令与目录关系

| 命令 | 写入 | 读取 deps |
|------|------|---------|
| `xtask build stdlib` | `<lib>/<profile>/{cache,dist}/` + `libraries/dist/<profile>/` | `[dependencies]` |
| `xtask test stdlib` | `<lib>/<profile>/tests/{cache,dist}/` | `[dependencies]` + `[tests.dependencies]` + `[[test]].dependencies` |
| `xtask bench stdlib` | `<lib>/<profile>/bench/{cache,dist}/` | `[dependencies]` + `[bench.dependencies]` + `[[bench]].dependencies` |
| `xtask clean` | 删 `<lib>/<profile>/{cache,dist}/`（保留 tests/bench）| — |
| `xtask clean tests` | 删 `<lib>/<profile>/tests/` | — |
| `xtask clean bench` | 删 `<lib>/<profile>/bench/` | — |
| `xtask clean all` | 删整个 `artifacts/build/` | — |

**关键不变量**：`xtask build` 和 `xtask test` 互不写对方的目录。物理隔离 + 命名隔离 + CI 守门三重。

#### 6.5 CI 守门（硬性）

新增 `.github/workflows/release-guard.yml`（或并入 `build-and-test`）的最末 step：

```bash
# 1. 生产 dist 不许出现测试 / bench zpkg
find artifacts/build/libraries/dist -type f \( \
    -name '*.test.*.zpkg' -o -name '*.bench.*.zpkg' \
\) | grep . && {
  echo "::error::test/bench zpkg leaked into production dist/"
  exit 1
} || true

# 2. tests/dist 反向不许出现生产 zpkg
find artifacts/build/libraries -path '*/tests/dist/*' -type f -name '*.zpkg' \
  ! -name '*.test.*.zpkg' | grep . && {
  echo "::error::production zpkg appeared in tests/dist/"
  exit 1
} || true
```

#### 6.6 不暴露 `tests_dir` / `bench_dir` 字段

测试 / bench 路径**强制**为 `<output_dir>/tests/` 和 `<output_dir>/bench/`，不开 manifest 字段。理由：

- 多一个 dial = 多一处可能漂移
- 与 CI 守门正则强耦合（守门假定固定路径）
- 真要改 → 改 `output_dir` 即可，两子树跟着走

唯一可调字段：`[build].output_dir` / `cache_dir` / `dist_dir` 三个（已有）— 整体迁移时一并改。

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
- [ ] xtask 新增子命令：`clean tests` / `clean bench`（独立可清）
- [ ] 测试产物落 `<lib>/<profile>/tests/dist/`；bench 产物落 `<lib>/<profile>/bench/dist/`；生产 `dist/` 零污染
- [ ] zpkg 命名硬约束生效：`.test.` / `.bench.` infix 全覆盖
- [ ] CI 守门：`find dist/ -name '*.test.*.zpkg' -o -name '*.bench.*.zpkg'` 零命中
- [ ] 22 stdlib z42.toml 完成 [tests.dependencies] 迁移
- [ ] 至少一个 stdlib 包改造为含 dir-mode 多文件测试 demo（推荐 z42.compression 或 z42.crypto）
- [ ] WS010 在迁移完成后扫描全 stdlib 零触发
- [ ] dotnet test + xtask test stdlib 全部绿
- [ ] docs/design/compiler/project.md 同步新 schema + 输出目录布局（与 restructure-build-output-dirs 段衔接）

## Open Questions

无 —— 设计已在 2026-06-06 通过 AskUserQuestion 全采纳推荐方案；配置与输出目录细节经第二轮 review（2026-06-06）锁定。

**已记录 future-iteration 候选**（不入本 spec）：

- 同文件 test + bench 支持（双声明 / Go-style 自动发现 / harness flag 三选一）— 0.4 / 0.5 单独 spec
- examples/ 子树实现 — 约定已留路径，schema 待 0.4 stdlib v1 一并加

## 与其他 spec 关系

- 0.3 B 主线 spec（`add-bootstrap-easy-subsystems`）将复用本 spec 落地的约定
- 0.3 A 主线（stdlib 重组）会触碰 22 个 z42.toml，与本 spec 的迁移工作可在同一 PR 完成（A1 包重组 commit 链中执行）
- features.md attribute 机制 spec（0.3.C3 前置）与本 spec 无直接耦合
