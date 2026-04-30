# Design: Migrate Tests by Ownership

## Architecture (迁移完成后)

```
src/
├── compiler/z42.Tests/                # 编译器单元 (xUnit, 不动)
│   └── ... (现状保留)
│
├── runtime/
│   ├── crates/<crate>/tests/          # VM 子系统单测 (cargo test)
│   └── tests/
│       ├── vm_core/                   # ⭐ 新：VM 端到端 (无 stdlib 依赖)
│       │   ├── runner.rs              # cargo test harness
│       │   └── <NN_name>/
│       │       ├── source.z42
│       │       ├── source.zbc
│       │       └── expected_output.txt
│       └── zbc_compat.rs              # 跨语言 zbc 契约 (保留)
│
├── libraries/
│   ├── z42.core/
│   │   ├── src/
│   │   └── tests/                     # ⭐ 新：z42.core 本地测试 (z42-test-runner)
│   │       ├── README.md
│   │       └── *.z42
│   ├── z42.collections/tests/         # 同上
│   ├── z42.math/tests/                # 同上
│   ├── z42.io/tests/                  # 同上
│   ├── z42.text/tests/                # 同上
│   └── z42.test/tests/                # 同上 (dogfooding)
│
└── runtime/tests/golden/run/          # ❌ 删除
    └── ...

tests/                                  # ⭐ 新：跨模块/跨库
└── integration/
    ├── README.md
    └── <NN_name>/
        ├── source.z42
        ├── source.zbc
        └── expected_output.txt
```

## Decisions

### Decision 1: 归属判定算法（锁定）

对每个现有用例 .z42，按以下顺序判定 tier：

```
1. 解析 .z42 的 import 语句
2. 收集所有依赖的 stdlib 库（去重）
3. 应用规则：
   ┌────────────────────────────────────────────────────┐
   │ deps == ∅                       → vm_core           │
   │ deps == {z42.io} 且仅用 println  → vm_core (例外)    │
   │ |deps| == 1                      → stdlib:<dep>     │
   │ |deps| >= 2                      → integration      │
   └────────────────────────────────────────────────────┘
```

**vm_core 例外 (Console.println)**：因为 println 是验证 VM 输出的最常用手段，含 println 的纯算术/控制流用例仍归 vm_core；判定脚本特殊处理 z42.io.Console.println / print。

### Decision 2: 迁移流程（一次切换）

```
1. 对每个 src/runtime/tests/golden/run/<case>/：
   a. 读 source.z42 → 算 tier
   b. 在 source.z42 顶部插入 // @test-tier: <tier>
   c. mv 整个目录到目标位置
2. rm -rf src/runtime/tests/golden/run/
3. 更新 zbc_compat.rs / regen-golden-tests.sh / test-vm.sh 路径
4. 跑 just test 全绿
5. CI 一次切换 (不留 fallback)
```

**不留兼容**：pre-1.0 原则，新旧路径不并存。

### Decision 3: 迁移工具（一次性脚本）

新建 [scripts/_migrate-tests.sh](scripts/_migrate-tests.sh)（下划线开头表示一次性工具，归档前删除或保留作为参考）：

```bash
#!/usr/bin/env bash
# 一次性迁移脚本，按归属移动 golden 用例
set -euo pipefail

for case_dir in src/runtime/tests/golden/run/*/; do
    src_file="$case_dir/source.z42"
    [[ -f "$src_file" ]] || continue

    # 解析 import 语句
    deps=$(grep -E '^import z42\.' "$src_file" | sed 's/import //;s/\..*//' | sort -u)
    dep_count=$(echo "$deps" | grep -c . || echo 0)

    # 判定 tier
    if [[ "$dep_count" == 0 ]]; then
        tier="vm_core"
        target="src/runtime/tests/vm_core/$(basename $case_dir)"
    elif [[ "$dep_count" == 1 ]] && [[ "$deps" == "z42.io" ]]; then
        # 检测是否仅用 Console.println
        if only_uses_println "$src_file"; then
            tier="vm_core"
            target="src/runtime/tests/vm_core/$(basename $case_dir)"
        else
            tier="stdlib:z42.io"
            target="src/libraries/z42.io/tests/$(basename $case_dir)"
        fi
    elif [[ "$dep_count" == 1 ]]; then
        lib=$(echo "$deps")
        tier="stdlib:$lib"
        target="src/libraries/$lib/tests/$(basename $case_dir)"
    else
        tier="integration"
        target="tests/integration/$(basename $case_dir)"
    fi

    # 插入 front-matter
    sed -i.bak "1i\\
// @test-tier: $tier
" "$src_file"
    rm "${src_file}.bak"

    # 移动目录
    mkdir -p "$(dirname $target)"
    git mv "$case_dir" "$target"
done

# 删除空的 golden/run/
rmdir src/runtime/tests/golden/run/ 2>/dev/null || true
```

注意：脚本完成后**保留**在 scripts/ 下，便于审计；归档时不删除（与归档一起留作参考）。

### Decision 4: stdlib 各库的「最低 1 个原生测试」

| 库 | 测试主题 | 文件名 |
|----|---------|--------|
| z42.core | string 基础操作 | `string_basics.z42` |
| z42.collections | LinkedList push/pop/iter | `linkedlist.z42` |
| z42.math | abs / min / max / sqrt | `math_basics.z42` |
| z42.io | Console.println 多类型 | `console.z42` |
| z42.text | StringBuilder append | `stringbuilder.z42` |
| z42.test | Assert.eq pass/fail | `self.z42` （dogfood） |

每个文件 ≥ 3 个 `[Test]` 函数，覆盖正常 + 边界。

### Decision 5: vm_core 测试仍走 cargo test

vm_core 用例**不**用 z42-test-runner（避免 vm_core 自身的循环依赖），走 cargo test 路径：

[src/runtime/tests/vm_core/runner.rs](src/runtime/tests/vm_core/runner.rs)：

```rust
// 复用现有 src/runtime/tests/zbc_compat.rs 的 golden test 逻辑
// 但只扫描 vm_core/ 目录而非 golden/run/
use std::fs;
use std::path::Path;

#[test]
fn vm_core_golden_tests() {
    let vm_core_dir = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/vm_core");
    for entry in fs::read_dir(vm_core_dir).unwrap() {
        let case = entry.unwrap().path();
        if !case.is_dir() { continue; }
        run_golden_case(&case);
    }
}

fn run_golden_case(case: &Path) {
    let zbc = case.join("source.zbc");
    let expected = fs::read_to_string(case.join("expected_output.txt")).unwrap();

    // interp
    let actual = run_z42vm(&zbc, "interp");
    assert_eq!(actual, expected, "case {}: interp mismatch", case.display());

    // jit (若 JIT 可用)
    if cfg!(feature = "jit") {
        let actual = run_z42vm(&zbc, "jit");
        assert_eq!(actual, expected, "case {}: jit mismatch", case.display());
    }
}
```

### Decision 6: stdlib 测试走 z42-test-runner

每个 stdlib 库的 `tests/*.z42` 由 z42-test-runner 调度（P2 已搭）：

```bash
# just test-stdlib z42.core 内部
cargo run -p z42-test-runner -- src/libraries/z42.core/tests/
```

需要先编译 .z42 → .zbc：

```just
test-stdlib lib="":
    @if [ -z "{{lib}}" ]; then \
        for d in src/libraries/*/tests; do \
            lib=$(basename $(dirname $d)); \
            ./scripts/build-stdlib-tests.sh $lib; \
            cargo run -p z42-test-runner -- "$d"; \
        done; \
    else \
        ./scripts/build-stdlib-tests.sh {{lib}}; \
        cargo run -p z42-test-runner -- "src/libraries/{{lib}}/tests/"; \
    fi
```

新建 [scripts/build-stdlib-tests.sh](scripts/build-stdlib-tests.sh)：编译某 lib 的 tests/*.z42 → .zbc。

### Decision 7: integration 测试组织

[tests/integration/](tests/integration/) 形式与 vm_core 相同（保持 source.z42 + expected_output.txt）：

```
tests/integration/
├── README.md
├── 01_io_collections/
│   ├── source.z42
│   ├── source.zbc
│   └── expected_output.txt
└── 02_text_math/
    ├── ...
```

由 [scripts/test-cross-zpkg.sh](scripts/test-cross-zpkg.sh) 改造后驱动（仍是 shell harness）。

### Decision 8: CI 矩阵改造

[.github/workflows/ci.yml](.github/workflows/ci.yml) 矩阵从 1 维（os）变 2 维（os × test-target）：

```yaml
strategy:
  fail-fast: false
  matrix:
    os: [ubuntu-latest, macos-latest]   # windows 仍只跑 smoke
    target: [compiler, vm_core, stdlib, integration]
    exclude:
      - os: macos-latest
        target: integration   # 节省 macOS 配额
```

每个 job 只跑对应 target：

```yaml
- name: Run tests for target ${{ matrix.target }}
  run: just test-${{ matrix.target }}
```

总并行度：~6–7 jobs（vs 之前 3 jobs）。

### Decision 9: 老路径删除策略

迁移完成后**立即删除** `src/runtime/tests/golden/run/`，**不留 deprecation 期**。

理由：
- pre-1.0 原则
- 防止后续新写测试又错放到老路径
- git history 仍可回溯

### Decision 10: front-matter 字段全集

P3 落地后，所有测试 .z42 文件**必须**含 `@test-tier`；其余字段可选：

```z42
// @test-tier: stdlib:z42.collections
// @test-deps: z42.core, z42.collections
// @test-feature: linked-list-iter
// @test-tag: smoke,stdlib

import z42.test.{Test, Assert};
import z42.collections.LinkedList;

[Test]
fn test_iter_empty() { /* ... */ }
```

迁移工具自动填 `@test-tier`；其余字段后续手工补。

## Implementation Notes

### 迁移顺序建议

1. 先跑 _migrate-tests.sh 一次（所有 103 个用例自动归位）
2. 人工 review 归属错误的用例（预计 5–10 个边界 case，例如同时用 collections 和 io 但实际只是辅助打印）
3. 删 golden/run/
4. 更新 zbc_compat.rs / regen / test-vm.sh
5. 补 6 个 stdlib 库的最低 1 个原生测试
6. 跑全绿
7. CI 矩阵改造
8. 文档同步

### zbc_compat.rs 处理

[src/runtime/tests/zbc_compat.rs](src/runtime/tests/zbc_compat.rs) 当前承担两种职责：
- 跨语言 zbc 解码契约（Rust 解码 C# 产物）—— **保留**
- 端到端 golden 用例驱动 —— **迁出**到 vm_core/runner.rs

迁移后 zbc_compat.rs 只剩契约测试，文件应缩小约 70%。

### 新写测试如何选 tier

新写的测试默认按依赖判定：

- 不 import 任何 stdlib → `vm_core`
- 仅 import 1 个 → `stdlib:<lib>`
- import ≥ 2 → `integration`

由开发者手动加 `@test-tier`（无自动机制；CI 检测缺失时报警）。

### CI 失败定位

CI matrix 拆分后，一旦某 target 失败：
- 失败 job 名直接显示 `os=ubuntu / target=stdlib` 等
- 开发者只需本地跑 `just test-stdlib` 即可复现
- 比之前"全 fail"更精准

## Testing Strategy

迁移本身的验证：

- ✅ 迁移前后测试用例数一致（103 个 + 6 新增 = 109+）
- ✅ 迁移前后所有用例**全绿**（行为不变）
- ✅ `find src/runtime/tests/golden/run/` 返回空（彻底删除）
- ✅ `just test-vm` / `just test-stdlib` / `just test-integration` 各自独立可跑且全绿
- ✅ `just test-changed` 在仅修改 z42.io 时只触发 z42.io tests + 反向依赖
- ✅ CI 矩阵 5+ jobs 全绿
- ✅ 每个 stdlib 库的 README 列出 tests/ 内容
- ✅ 所有迁移后 .z42 顶部都有 `@test-tier`（grep 检查）
- ✅ `scripts/regen-golden-tests.sh` 重生所有 .zbc 后仍全绿
