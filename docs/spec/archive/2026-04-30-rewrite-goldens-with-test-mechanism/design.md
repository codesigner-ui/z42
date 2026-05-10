# Design: Rewrite Goldens with Test Mechanism

## Architecture (R5 完成后)

```
src/runtime/tests/                          # VM 端到端 (cargo test 调度)
├── vm_core/                                # ⭐ stdout-based; 不依赖 stdlib
│   ├── runner.rs                           # cargo test harness
│   └── <NN>_<name>/{source.z42, source.zbc, expected_output.txt}
└── zbc_compat.rs                           # 跨语言 zbc 契约（保留）

src/libraries/<lib>/                        # stdlib 各库
├── src/                                    # 库源码
└── tests/                                  # ⭐ [Test] + Assert (z42-test-runner)
    ├── README.md
    └── <topic>.z42

tests/                                       # ⭐ 跨模块/跨库
├── README.md
└── integration/
    └── <NN>_<name>.z42                     # [Test] + TestIO

src/compiler/z42.Tests/                     # 编译器测试（保留 C# xUnit）
└── ...                                      # 不动（自举前不迁）
```

## Decisions

### Decision 1: 归属判定算法

```python
def classify(source_z42: str) -> str:
    """Returns 'vm_core' | 'stdlib:<name>' | 'integration'."""
    deps = parse_using_imports(source_z42)
    # 仅 z42.core 隐式 prelude 不算依赖
    deps -= {"z42.core"}
    
    if not deps:
        return "vm_core"
    
    # 例外：仅用 Console.WriteLine 的 z42.io 依赖归 vm_core
    if deps == {"z42.io"} and only_uses_console_writeline(source_z42):
        return "vm_core"
    
    if len(deps) == 1:
        lib = next(iter(deps))
        return f"stdlib:{lib}"
    
    return "integration"
```

### Decision 2: 重写转换规则

按 golden 类型分类（详见父 redesign-test-infra 设计文档的"重写转换规则"段）。

#### 类型 A：仅 println 字面量

**判定**：源码只含 `Console.WriteLine("literal")` 调用。

**保留 stdout-based**（vm_core）或转 captureStdout assert（stdlib）。

```z42
// AFTER: src/libraries/z42.io/tests/println_basic.z42
// @test-tier: stdlib:z42.io
import z42.test.{Test, Assert, TestIO};
import z42.io.Console;

[Test]
fn test_println_string_literal() {
    let captured = TestIO.captureStdout(|| {
        Console.WriteLine("Hello, World!");
    });
    Assert.eq(captured, "Hello, World!\n");
}
```

#### 类型 B：纯计算 + 打印结果

**判定**：源码含函数定义 + 算术循环 + `Console.WriteLine(result)`。

**重写为多个 [Test]**，每个测一个边界。

```z42
// AFTER: src/libraries/z42.core/tests/recursion.z42
// @test-tier: stdlib:z42.core
import z42.test.{Test, Assert};

fn fib(n: i32) -> i32 {
    if (n <= 1) return n;
    return fib(n - 1) + fib(n - 2);
}

[Test]
fn test_fib_base_cases() {
    Assert.eq(fib(0), 0);
    Assert.eq(fib(1), 1);
}

[Test]
fn test_fib_recursive() {
    Assert.eq(fib(10), 55);
    Assert.eq(fib(20), 6765);
}
```

#### 类型 C：异常 / panic 路径

**判定**：源码含可能 panic 的操作（`/0`, `null` 解引用），expected_output 无内容或含错误。

**重写为 `Assert.throws<E>`**：

```z42
// AFTER: src/libraries/z42.core/tests/arithmetic_errors.z42
// @test-tier: stdlib:z42.core
import z42.test.{Test, Assert};

[Test]
fn test_int_divide_by_zero() {
    Assert.throws<DivisionByZero>(|| {
        let _ = 1 / 0;
    });
}
```

#### 类型 D：stdlib API 验证

**判定**：源码使用 stdlib 类的 method（push / pop / iter 等）。

**重写为多个细粒度 [Test]**：

```z42
// AFTER: src/libraries/z42.collections/tests/linkedlist.z42
// @test-tier: stdlib:z42.collections
import z42.test.{Test, Assert};
import z42.collections.LinkedList;

[Test]
fn test_new_list_is_empty() {
    let l = LinkedList<i32>();
    Assert.eq(l.length(), 0);
}

[Test]
fn test_pushback_increases_length() {
    let l = LinkedList<i32>();
    l.pushBack(1);
    l.pushBack(2);
    Assert.eq(l.length(), 2);
}

[Test]
fn test_iter_in_order() {
    let l = LinkedList<i32>();
    l.pushBack(1); l.pushBack(2); l.pushBack(3);
    let collected = LinkedList<i32>();
    for (e in l) collected.pushBack(e);
    Assert.elementsEqual(collected, [1, 2, 3]);
}
```

#### 类型 E：跨 stdlib (integration)

```z42
// AFTER: tests/integration/io_collections_text.z42
// @test-tier: integration
// @test-deps: z42.io, z42.collections, z42.text
import z42.test.{Test, Assert, TestIO};
import z42.collections.LinkedList;
import z42.io.Console;
import z42.text.StringBuilder;

[Test]
fn test_collection_to_string() {
    let l = LinkedList<string>();
    l.pushBack("a"); l.pushBack("b");
    let sb = StringBuilder();
    for (e in l) { sb.append(e); sb.append("|"); }
    Assert.eq(sb.build(), "a|b|");
}
```

### Decision 3: 半自动转换器

[scripts/_rewrite-goldens.py](scripts/_rewrite-goldens.py)：

```python
#!/usr/bin/env python3
"""One-shot golden test rewriter (R5)."""
import re
from pathlib import Path

GOLDEN_DIR = Path("src/runtime/tests/golden/run")

def parse_imports(source: str) -> set[str]:
    return set(re.findall(r'using\s+(\S+);', source))

def detect_type(source: str, expected: str) -> str:
    """Returns: 'A_println' | 'B_compute' | 'C_exception' | 'D_api' | 'E_integration' | 'manual'."""
    deps = parse_imports(source) - {"z42.core"}

    if "1 / 0" in source or "1/0" in source:
        return "C_exception"
    if not deps:
        if has_only_println_literals(source):
            return "A_println"
        return "B_compute"
    if len(deps) >= 2:
        return "E_integration"
    # Single stdlib dep
    if has_method_calls_on_stdlib(source, deps):
        return "D_api"
    return "B_compute"

def generate_test_z42(case_dir: Path, classification: str, target: Path) -> str:
    # Per-classification template; emits skeleton with TODOs.
    ...

def main():
    manual_review = []
    for case in GOLDEN_DIR.iterdir():
        if not case.is_dir(): continue
        source = (case / "source.z42").read_text()
        expected = (case / "expected_output.txt").read_text() if (case / "expected_output.txt").exists() else ""

        cls = detect_type(source, expected)
        target = decide_target_path(case.name, cls)
        try:
            generated = generate_test_z42(case, cls, target)
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(f"// AUTO-GENERATED REVIEW REQUIRED\n{generated}")
        except Exception as e:
            manual_review.append((case.name, str(e)))

    if manual_review:
        print(f"\n{len(manual_review)} cases need manual review:")
        for name, reason in manual_review:
            print(f"  - {name}: {reason}")

if __name__ == "__main__":
    main()
```

### Decision 4: 工具/脚本更新

#### vm_core/runner.rs

```rust
// src/runtime/tests/vm_core/runner.rs
use std::path::Path;

#[test]
fn vm_core_golden_tests() {
    let dir = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/vm_core");
    for entry in std::fs::read_dir(dir).unwrap() {
        let case = entry.unwrap().path();
        if !case.is_dir() { continue; }
        run_golden_case(&case);
    }
}

fn run_golden_case(case: &Path) {
    // 复用现有 zbc_compat 的逻辑（已抽出）
}
```

#### test-vm.sh 改造

```bash
# 新版只扫 vm_core/
for case_dir in src/runtime/tests/vm_core/*/; do
    # ...
done
```

#### test-cross-zpkg.sh 改造

```bash
# 改为 z42-test-runner 调度 tests/integration/
cargo run -p z42-test-runner --release -- tests/integration/
```

#### scripts/build-stdlib-tests.sh（新）

```bash
#!/usr/bin/env bash
# Compile stdlib library tests/*.z42 → .zbc into a tmp dir
set -euo pipefail
LIB="$1"
OUT_DIR="${2:-/tmp/stdlib-tests-$LIB}"
mkdir -p "$OUT_DIR"
for src in "src/libraries/$LIB/tests/"*.z42; do
    name=$(basename "$src" .z42)
    dotnet run --project src/compiler/z42.Driver -c Release -- \
        "$src" --emit zbc -o "$OUT_DIR/$name.zbc"
done
echo "$OUT_DIR"
```

### Decision 5: 迁移后老路径删除

```bash
rm -rf src/runtime/tests/golden/run/
```

pre-1.0 原则；不留兼容期。

### Decision 6: stdlib 各库最低原生测试

在迁移基础上，每个 stdlib 库**至少加 1 个完全新的测试文件**（不来自迁移），确保覆盖核心 API：

| 库 | 新增文件（最少） | 覆盖 |
|----|-----------------|------|
| z42.core | string_basics.z42 | string concat / length / substring |
| z42.collections | linkedlist.z42 | push/pop/iter/length（如未从迁移得到） |
| z42.math | math_basics.z42 | abs / min / max / sqrt |
| z42.io | console.z42 | WriteLine 多类型 |
| z42.text | stringbuilder.z42 | append / build |
| z42.test | self.z42 | dogfooding（Assert.eq pass/fail） |

### Decision 7: front-matter 规范

每个 .z42 测试文件首行必须含：
```
// @test-tier: vm_core | stdlib:<lib> | integration
```

可选：
```
// @test-deps: <comma-separated>
// @test-tag: <comma-separated>
```

R5 完成后 grep 验证：

```bash
grep -L '@test-tier' src/runtime/tests/vm_core/**/*.z42 \
                    src/libraries/*/tests/*.z42 \
                    tests/integration/*.z42
# 应返回空
```

## Implementation Notes

### 迁移执行顺序

1. 跑 `python3 scripts/_rewrite-goldens.py` 生成所有重写文件骨架
2. 检查 manual review list（预计 5-15 个）
3. 人工 review 自动生成的文件（重点是分类是否正确、断言是否到位）
4. 跑 `just test-vm`（vm_core）+ `just test-stdlib`（per lib）+ `just test-integration` 验证全绿
5. 删除老 src/runtime/tests/golden/run/
6. 更新 zbc_compat.rs / test-vm.sh / test-cross-zpkg.sh / regen-golden-tests.sh
7. 加每库最低 1 个原生测试
8. 文档同步

### 转换器的启发式准确率目标

- ≥ 70% 用例自动生成可直接使用（仅 polish）
- ~20% 用例需要拆细 [Test] 或调整 assertion
- ~10% 进入 manual list（保留为 stdout-based 或彻底重写）

低于 70% → 改进启发式；> 30% manual → 接受手工兜底（pragmatic）。

### 编译器 golden 引用

[src/compiler/z42.Tests/GoldenTests.cs](src/compiler/z42.Tests/GoldenTests.cs) 当前可能引用 `src/runtime/tests/golden/run/<case>/source.z42`。R5 实施时需修改 GoldenTests 引用路径到 `src/runtime/tests/vm_core/<case>/source.z42`（仅 vm_core 范围）；不引用 stdlib/integration（因为已不再是 source.z42 + expected_output.txt 格式）。

### CI 改造留独立任务

R5 完成后 CI 仍跑 `just test`（compose 各 tier）。CI matrix 按归属并行（5+ jobs）的改造单独立 R5.1，避免 R5 范围爆炸。

## Testing Strategy

### 迁移本身的验证

- ✅ 用例数：迁移后总数 ≥ 109 (= 103 迁移 + 6 各库新增)
- ✅ `find src/runtime/tests/golden/run/` 返回空
- ✅ 每个迁移后 .z42 顶部含 `@test-tier`
- ✅ `just test-vm` / `just test-stdlib` / `just test-integration` 各自全绿
- ✅ `just test-changed` 增量测试逻辑正确（仅 z42.io 改动 → 仅跑 z42.io tests）
- ✅ `./scripts/regen-golden-tests.sh` 重生后仍全绿
- ✅ 编译器 GoldenTests 引用路径更新后跑通
- ✅ 总耗时（all tests） 与迁移前 ±10% 内（接受小幅变化）

### 与 R3 runner 的端到端

每个迁移后的 stdlib 测试文件都可以被 `cargo run -p z42-test-runner -- src/libraries/<lib>/tests/` 跑通。pretty / TAP / JSON 输出格式正确。

### 跨平台一致性（与 P4 联动）

R5 完成后，vm_core 的 5 个最简用例可作为 P4.2 (wasm) / P4.3 (android) / P4.4 (ios) 的 cross-platform consistency test set。
