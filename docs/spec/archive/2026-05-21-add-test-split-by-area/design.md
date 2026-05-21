# Design: Scope-aware test-all.sh

## Architecture

```
当前：
  STAGES=(
    "dotnet build|..."
    "cargo build (release)|..."
    "dotnet test|..."
    "VM goldens|./scripts/test-vm.sh"
    "cross-zpkg|./scripts/test-cross-zpkg.sh"
    "stdlib [Test]|./scripts/test-stdlib.sh"
  )
  for entry in "${STAGES[@]}"; do run; done

本 spec 后：
  SCOPE=full|runtime|compiler|stdlib|auto

  case "$SCOPE" in
    full)     STAGES=( all 6 )
    runtime)  STAGES=( cargo + test-vm + cross-zpkg + stdlib )
    compiler) STAGES=( dotnet build + dotnet test + test-vm + cross-zpkg + stdlib )
    stdlib)   STAGES=( test-vm + cross-zpkg + stdlib )
    auto)     resolve_scope_from_git; recurse with resolved SCOPE
  esac

  for entry in "${STAGES[@]}"; do run; done
```

## Decisions

### Decision 1: auto-detect baseline = `HEAD` (uncommitted)

**问题**：auto-detect 对比哪个 baseline？

**选项**：
- A `HEAD` — 未提交 + 已提交但未 push 的最新；用户本地视角
- B `HEAD~1..HEAD` — 上一次提交 + 当前 working tree
- C `origin/main..HEAD` — PR 视角，包含所有 branch commit
- D `git diff` (uncommitted only)

**决定**：**D (`git diff --name-only` uncommitted-only)**. 理由：
- iterating workflow 一般是改文件 → 跑测试 → 改文件 → 跑测试 → commit
- 测 uncommitted 改动 = 测当前正在调的东西
- 已提交的部分理应已通过测试（不该重测）
- 简单 + fast — 一次 git diff 调用

包含已 staged + 未 staged：`git diff --name-only HEAD` (覆盖 staged + unstaged 改动)。

### Decision 2: 命名 — `--scope=<value>` 单参数

**决定**：`--scope=runtime` 风格而非 `--runtime-only`。理由：
- unified 参数 (单一 entry point)，比平铺多个 bool flag 清晰
- 易于扩展（未来加 `--scope=docs-only` 等不变动核心解析）
- 与 cargo / rustc / kubectl 等工具的 `--<noun>=<value>` 习惯对齐

### Decision 3: default = `full`（保留兼容）

**问题**：default 改成 auto 让 user 自动受益？

**决定**：**default = full**. 理由：
- 兼容 user 现有脚本 / muscle memory / CI 系统
- auto 有误判风险（"我以为只改了 runtime 但其实编辑了 csproj 路径"）— 安全派别 prefer full
- 用户读 docs 学到 `--scope=runtime` 后，主动 opt-in 才用
- workflow.md 阶段 8 明记：commit 前最终 GREEN 必须 full（或 auto 等价于 full）

### Decision 4: scope 文件分类

按文件路径前缀分类：

| 路径前缀                 | 影响 scope |
|--------------------------|-----------|
| `src/compiler/**`        | compiler  |
| `src/runtime/**`         | runtime   |
| `src/libraries/**`       | stdlib    |
| `examples/**`            | stdlib    |
| `src/tests/**`           | runtime（VM tests）|
| `scripts/**`             | full（脚本可能影响任意 stage）|
| `docs/**`                | docs-only — 跳过所有 stage |
| `.claude/**`             | docs-only |
| 其他（Cargo.toml / .csproj / .github / 顶层）| full（保守）|

`docs-only` scope：所有 stage 都跳过；输出 "✅ ALL GREEN (0 stages — docs-only)"。

### Decision 5: `--quick` 与 `--scope` 组合

**问题**：现有 `--quick` 跳过 rebuild。如果 `--scope=runtime --quick`？

**决定**：兼容。`--quick` 影响 test-vm.sh 内部（不重新 build VM）；与 scope 选择 stage 集合正交。可以同时用。

## Implementation Notes

### 主流程改造

```bash
SCOPE="full"
WITH_DIST=false
QUICK=false
for arg in "$@"; do
    case "$arg" in
        --with-dist) WITH_DIST=true ;;
        --quick)     QUICK=true ;;
        --scope=*)   SCOPE="${arg#--scope=}" ;;
        # ... existing help ...
    esac
done

# Resolve auto
if [ "$SCOPE" = "auto" ]; then
    SCOPE=$(resolve_scope_from_diff)
    echo "auto-detected scope: $SCOPE"
fi

# Build STAGES based on SCOPE
case "$SCOPE" in
    full)
        STAGES=(
            "dotnet build|dotnet build src/compiler/z42.slnx --nologo -v quiet"
            "cargo build (release)|cargo build --manifest-path src/runtime/Cargo.toml --release --quiet"
            "dotnet test|dotnet test src/compiler/z42.Tests/z42.Tests.csproj --nologo"
            "VM goldens|./scripts/test-vm.sh $($QUICK && echo '--no-rebuild' || true)"
            "cross-zpkg|./scripts/test-cross-zpkg.sh"
            "stdlib [Test]|./scripts/test-stdlib.sh"
        )
        ;;
    runtime)
        STAGES=(
            "cargo build (release)|..."
            "VM goldens|..."
            "cross-zpkg|..."
            "stdlib [Test]|..."
        )
        ;;
    compiler)
        STAGES=(
            "dotnet build|..."
            "dotnet test|..."
            "VM goldens|..."
            "cross-zpkg|..."
            "stdlib [Test]|..."
        )
        ;;
    stdlib)
        STAGES=(
            "VM goldens|..."
            "cross-zpkg|..."
            "stdlib [Test]|..."
        )
        ;;
    docs-only)
        STAGES=()
        ;;
    *)
        echo "unknown scope: $SCOPE (try full/runtime/compiler/stdlib/auto/docs-only)" >&2
        exit 2
        ;;
esac
```

### resolve_scope_from_diff helper

```bash
resolve_scope_from_diff() {
    local files
    files=$(git diff --name-only HEAD 2>/dev/null || true)
    if [ -z "$files" ]; then
        # No uncommitted changes — nothing to test? Fall back to full
        # so user explicitly asking for auto still gets coverage.
        echo "full"
        return
    fi

    local has_compiler=false has_runtime=false has_stdlib=false has_other=false
    while IFS= read -r f; do
        case "$f" in
            src/compiler/*)             has_compiler=true ;;
            src/runtime/*|src/tests/*)  has_runtime=true ;;
            src/libraries/*|examples/*) has_stdlib=true ;;
            docs/*|.claude/*)           ;; # docs-only, doesn't elevate scope
            *)                          has_other=true ;;
        esac
    done <<< "$files"

    if $has_other; then
        echo "full"; return
    fi
    if $has_compiler && $has_runtime; then
        echo "full"; return
    fi
    if $has_compiler; then
        echo "compiler"; return
    fi
    if $has_runtime; then
        echo "runtime"; return
    fi
    if $has_stdlib; then
        echo "stdlib"; return
    fi
    # Only docs touched
    echo "docs-only"
}
```

### docs-only output

```bash
if [ ${#STAGES[@]} -eq 0 ]; then
    echo "════════════════════════════════════════════════"
    echo "  ✅ ALL GREEN (0 stages — docs-only)"
    echo "════════════════════════════════════════════════"
    exit 0
fi
```

## Testing Strategy

- **Manual verification**: invoke each scope, eyeball that stages match
  expectations + output format unchanged
- **Auto-detect smoke**: with current `git diff` state, run `--scope=auto`
  + verify resolved scope matches expectation
- **No new automated tests** — bash scripts aren't unit-tested in this
  project; this is a workflow utility, not production code

## Deferred / Future Work

### `add-test-incremental-cache`
- Stage skip based on per-stage cache hashes (e.g. dotnet build skipped
  if compiler/ + Cargo.toml hashes match last run). More accurate than
  git-diff path matching. Larger spec — not v0.

### `add-ci-scope-aware`
- Make `.github/workflows/ci.yml` use `--scope=auto` for PR jobs (PR
  diff is well-defined as `origin/main..HEAD`). Speeds CI; reduces
  GitHub Actions minutes.
