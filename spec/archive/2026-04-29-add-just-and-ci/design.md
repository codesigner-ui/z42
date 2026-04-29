# Design: just Task Runner & GitHub Actions CI

## Architecture

```
                 ┌────────────────────────────┐
                 │       justfile (root)      │
                 │                            │
                 │  test │ bench │ build      │
                 │  clean │ ci │ platform     │
                 └────────────┬───────────────┘
                              │ calls
            ┌─────────────────┴─────────────────┐
            ▼                                   ▼
   ┌────────────────────┐            ┌────────────────────┐
   │  scripts/*.sh      │            │  cargo / dotnet    │
   │  (现有 7 个脚本)    │            │  直接命令          │
   └────────────────────┘            └────────────────────┘

CI: .github/workflows/ci.yml
   ┌─────────────────────────────────────────┐
   │  trigger: pull_request | push to main   │
   │  matrix: linux-x64 / macos-aarch64 /    │
   │          windows-x64                    │
   │  steps: install just → just build →     │
   │         just test (linux/mac only)      │
   └─────────────────────────────────────────┘
```

## Decisions

### Decision 1: 任务编排器选 `just`（已在父 spec 锁定）

**问题**：7 个 shell 脚本无统一入口，新成员上手难。

**选项**：A. just（轻量、零编译）；B. cargo xtask（每次编译）；C. Makefile（语法古怪）

**决定**：选 **A**。理由见父 [redesign-test-infra/design.md](spec/changes/redesign-test-infra/design.md) Decision 4。

### Decision 2: justfile 任务命名约定

**形式**：`<top-verb>` 或 `<top-verb> <subtarget> [<sub-subtarget>]`

- `test` / `test compiler` / `test vm` / `test stdlib` / `test stdlib <lib>` / `test changed` / `test integration`
- `bench` / `bench vm` / `bench compiler` / `bench --baseline <ref>`
- `build` / `build runtime` / `build compiler` / `build stdlib`
- `clean` / `clean runtime` / `clean compiler`
- `ci` （= `build && test`，预留 P1 加 bench --quick）
- `platform <name> <action>` （P4 占位）

**禁止形式**：
- ❌ `test-compiler`（连字符复合名）
- ❌ `compiler-test`（动词不在前）
- ❌ `Test`（大小写不一致）

**理由**：动词在前、空格分隔，对应 just 的位置参数语义；与 cargo / dotnet CLI 约定一致；`just --list` 输出按动词归组易读。

### Decision 3: 现有脚本的处理 ——「保留 + 接入」

**不删除任何 [scripts/*.sh](scripts/)**。justfile 内部直接调用：

```just
test-vm mode="interp":
    ./scripts/test-vm.sh {{mode}}

test-cross-zpkg mode="interp":
    ./scripts/test-cross-zpkg.sh {{mode}}

test-compiler:
    dotnet test src/compiler/z42.Tests/z42.Tests.csproj

test:
    @just test-compiler
    @just test-vm
    @just test-cross-zpkg
```

**理由**：
- 本 spec 不引入行为变化；脚本是已验证的工作流，重写有回归风险
- 后续 Phase 视情况把脚本逻辑内联到 justfile 或保留外置
- 用户仍可直接 `./scripts/test-vm.sh interp`，向后兼容

### Decision 4: justfile 完整骨架（锁定）

```just
# z42 task runner
# Usage: just <task> [args]
# Run `just --list` for all tasks

default:
    @just --list

# ──── Build ────
build: build-runtime build-compiler

build-runtime:
    cargo build --manifest-path src/runtime/Cargo.toml

build-compiler:
    dotnet build src/compiler/z42.slnx

build-stdlib *args:
    ./scripts/build-stdlib.sh {{args}}

# ──── Test ────
test: test-compiler test-vm test-cross-zpkg

test-compiler:
    dotnet test src/compiler/z42.Tests/z42.Tests.csproj

test-vm mode="interp":
    ./scripts/test-vm.sh {{mode}}

test-cross-zpkg mode="interp":
    ./scripts/test-cross-zpkg.sh {{mode}}

# P2 接入
test-changed:
    @echo "P2 待实施：增量测试" && exit 1

# P3 接入
test-stdlib lib="":
    @echo "P3 待实施：stdlib 本地测试 ({{lib}})" && exit 1

test-integration:
    @echo "P3 待实施：integration 测试" && exit 1

# ──── Bench (P1 占位) ────
bench *args:
    @echo "P1 待实施：benchmark ({{args}})" && exit 1

# ──── Platform (P4 占位) ────
platform name action:
    @echo "P4 待实施：platform {{name}} {{action}}" && exit 1

# ──── Misc ────
clean:
    cargo clean --manifest-path src/runtime/Cargo.toml
    dotnet clean src/compiler/z42.slnx
    rm -rf artifacts/

ci: build test
    @echo "✅ CI passed"

regen-golden:
    ./scripts/regen-golden-tests.sh

package mode="release":
    ./scripts/package.sh {{mode}}

test-dist mode="interp":
    ./scripts/test-dist.sh {{mode}}
```

**变量约定**：
- 默认值用 `task arg="default"` 形式（与 `make` 不同）
- 透传所有参数用 `*args` 然后 `{{args}}`
- 子任务调用用 `@just <name>`（前缀 `@` 静默命令打印）

### Decision 5: CI workflow 完整骨架

`.github/workflows/ci.yml`：

```yaml
name: CI

on:
  pull_request:
    branches: [main]
  push:
    branches: [main]

jobs:
  build-and-test:
    strategy:
      fail-fast: false
      matrix:
        os: [ubuntu-latest, macos-latest, windows-latest]
    runs-on: ${{ matrix.os }}

    steps:
      - uses: actions/checkout@v4

      - name: Install just
        uses: extractions/setup-just@v2
        with:
          just-version: '1.36.0'

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'

      - name: Setup Rust
        uses: dtolnay/rust-toolchain@stable

      - name: Cache cargo
        uses: actions/cache@v4
        with:
          path: |
            ~/.cargo/registry
            ~/.cargo/git
            src/runtime/target
          key: ${{ runner.os }}-cargo-${{ hashFiles('src/runtime/Cargo.lock') }}

      - name: Cache NuGet
        uses: actions/cache@v4
        with:
          path: ~/.nuget/packages
          key: ${{ runner.os }}-nuget-${{ hashFiles('**/*.csproj') }}

      - name: Build
        run: just build

      # Test 仅在 unix 上跑（现有脚本为 bash）
      - name: Test (unix)
        if: matrix.os != 'windows-latest'
        run: just test

      - name: Smoke test (windows)
        if: matrix.os == 'windows-latest'
        run: |
          dotnet test src/compiler/z42.Tests/z42.Tests.csproj
          cargo test --manifest-path src/runtime/Cargo.toml
```

### Decision 6: Windows 矩阵的处理

Windows runner 上**不跑** `just test`（会失败，因为脚本是 bash）。退化为：
- `dotnet test`（直接命令，Windows 原生支持）
- `cargo test`（直接命令，Windows 原生支持）

**理由**：现有脚本（`test-vm.sh` 等）调用 bash-isms（数组循环、$(...)）。让它们 Windows 兼容是 P3 的事（迁移到内联 just recipes 时一并解决）。本 spec 只验证 Windows 能编译。

### Decision 7: 缓存键策略

| 缓存对象 | key | 失效条件 |
|---------|-----|---------|
| cargo registry + git + target | `<os>-cargo-<hash(Cargo.lock)>` | 依赖版本变 → 失效 |
| NuGet packages | `<os>-nuget-<hash(**/*.csproj)>` | csproj 变 → 失效 |
| stdlib 构建产物 | 不缓存（构建时间短，<10s） | -- |

P1 引入 baseline JSON 后再加 `bench/baselines/` 缓存。

## Implementation Notes

### just 安装

文档说明（[docs/dev.md](docs/dev.md) 新增）：

```bash
# macOS
brew install just

# Linux
cargo install just  # 或包管理器

# Windows
scoop install just
# or: choco install just
```

### CI 触发与并发

- `pull_request` 与 `push to main` 共用一个 job
- 不开 `workflow_dispatch`（本 spec 范围内不需要手动触发）
- 不设置 `concurrency:` 取消旧 run（保守起步，后续按需加）

### CI 失败时的处理

CI fail 时 PR 自动阻塞 merge（GitHub branch protection 在仓库设置里配，**不在本 spec 范围**）。

## Testing Strategy

本 spec 的"测试"是验证基础设施本身能用：

- ✅ 本地 `just --list` 列出所有 task，无解析错误
- ✅ 本地 `just build` 等价于现有 `dotnet build && cargo build`
- ✅ 本地 `just test` 等价于现有 `dotnet test && ./scripts/test-vm.sh && ./scripts/test-cross-zpkg.sh`
- ✅ 本地 `just bench` / `just test changed` / `just platform x y` 输出 "Pn 待实施"，exit 1
- ✅ 现有 `./scripts/test-vm.sh interp` 直接运行行为不变
- ✅ 推一个 dummy 提交到 PR 分支，CI 三平台矩阵全绿（Windows 跑 smoke）
- ✅ CI cache hit 在第二次 PR 上生效（手测）

无新增单元测试（不引入逻辑代码）。
