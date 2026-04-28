# Design: z42-test-runner + Test Metadata

## Architecture

```
                     ┌──────────────────────────────┐
                     │  z42-test-runner [paths]      │
                     │  CLI 入口 (clap)              │
                     └──────────────┬───────────────┘
                                    │
              ┌─────────────────────┼─────────────────────┐
              ▼                     ▼                     ▼
        ┌───────────┐        ┌───────────┐        ┌───────────┐
        │ discover  │        │  runner   │        │  format   │
        │           │        │           │        │  tap/json │
        │ scan .zbc │ ─────► │ load+call │ ─────► │ /pretty   │
        │ find Test │        │ catch fail│        │           │
        └───────────┘        └───────────┘        └───────────┘
                                    │
                                    ▼
                     ┌──────────────────────────────┐
                     │  z42 runtime (Interpreter)   │
                     │  load_zpkg + call_function   │
                     └──────────────────────────────┘

Library side (z42.test):
   ┌────────────────────────────────────────────┐
   │  [Test] / [Skip] / [Ignore] attributes     │
   │  Assert.eq / Assert.throws / Assert.near   │
   │  TestFailure exception type                │
   └────────────────────────────────────────────┘
```

## Decisions

### Decision 1: 工具定位 ——「z42 程序的 host 工具」

z42-test-runner 是**宿主端 Rust 工具**（不是 z42 库），与 [z42vm](src/runtime/src/bin/) 平行。原因：

1. 测试发现需读 .zbc 文件元数据 → 适合 Rust 直接读
2. 需 catch z42 异常 / panic → host 边界更可靠
3. CLI 体验（参数解析、TTY 检测、彩色输出）→ Rust crate 生态成熟

**与 z42vm 的关系**：runner 链接 `z42-runtime` crate（path 依赖），复用 Interpreter API；不通过 subprocess 调用 z42vm。

### Decision 2: `[Test]` attribute 契约

#### IR 元数据形式

```
attribute "z42.test.Test"                      // 标记该函数为测试
attribute "z42.test.Skip" reason="..."         // 跳过该测试（带可选理由）
attribute "z42.test.Ignore"                    // 永久忽略（不计入统计）
```

attribute name 为 string；可选参数为 key-value 对。复用现有 IR attribute 元数据机制（不新增二进制格式）。

#### z42 源码侧

```z42
import z42.test.{Test, Skip, Assert};

[Test]
fn test_addition() {
    Assert.eq(1 + 1, 2);
}

[Test]
[Skip(reason: "blocked by issue #123")]
fn test_known_broken() {
    // ...
}
```

#### 函数签名约束

测试函数必须满足：
- `fn() -> void`（无参、无返回值）
- 不能是泛型（runner 无法推断类型实参）
- 不能是实例方法（必须 free function 或 static method）

违反时 runner 报错：`"test function 'foo' has invalid signature: must be fn() -> void"`

### Decision 3: z42.test 库 API（锁定）

[src/libraries/z42.test/src/Assert.z42](src/libraries/z42.test/src/Assert.z42)：

```z42
public class Assert {
    // 相等性
    public static fn eq<T: Equatable>(actual: T, expected: T) -> void;
    public static fn notEq<T: Equatable>(actual: T, expected: T) -> void;

    // 布尔
    public static fn isTrue(value: bool) -> void;
    public static fn isFalse(value: bool) -> void;

    // 异常
    public static fn throws<E: Exception>(action: fn() -> void) -> E;

    // 浮点近似
    public static fn near(actual: f64, expected: f64, epsilon: f64 = 1.0e-9) -> void;

    // 主动失败
    public static fn fail(message: string) -> never;
}
```

`Assert.throws` 返回捕获到的异常实例，便于进一步断言异常字段。

[src/libraries/z42.test/src/Failure.z42](src/libraries/z42.test/src/Failure.z42)：

```z42
public class TestFailure : Exception {
    public actual: string;
    public expected: string;
    public location: string;  // file:line
}
```

### Decision 4: CLI 接口（锁定）

```
z42-test-runner [PATHS...] [OPTIONS]

PATHS              一个或多个 .zbc 文件或包含 .zbc 的目录（递归）

OPTIONS:
  --format <FMT>   输出格式：tap | json | pretty (默认 pretty 在 TTY，否则 tap)
  --filter <RE>   只跑名字匹配正则的测试
  --no-color       关闭颜色（pretty 模式）
  --quiet, -q      只输出汇总
  --verbose, -v    输出每个测试的详细信息
  --jobs <N>       并行度（默认 1，本 spec 仅支持 1）
  --timeout <SEC>  单测超时（默认 60；超时计为失败）
  --help, -h
  --version, -V

退出码：
  0  全部通过
  1  有测试失败
  2  发现错误（编译失败、IO 错误等）
  3  无测试可运行
```

### Decision 5: TAP 13 输出格式

```
TAP version 13
1..3
ok 1 - test_addition
not ok 2 - test_subtraction
  ---
  message: "expected 2, got 3"
  severity: fail
  data:
    actual: 3
    expected: 2
    location: "src/libraries/z42.core/tests/arith.z42:42"
  ...
ok 3 - test_skipped # SKIP blocked by issue #123
```

### Decision 6: JSON 输出格式（锁定）

```json
{
  "schema_version": 1,
  "runner": "z42-test-runner",
  "started_at": "2026-04-29T10:00:00Z",
  "finished_at": "2026-04-29T10:00:03Z",
  "summary": {
    "total": 10,
    "passed": 8,
    "failed": 1,
    "skipped": 1,
    "ignored": 0,
    "duration_ms": 3000
  },
  "tests": [
    {
      "name": "test_addition",
      "module": "z42.core.tests.arith",
      "status": "passed",
      "duration_ms": 12
    },
    {
      "name": "test_subtraction",
      "module": "z42.core.tests.arith",
      "status": "failed",
      "duration_ms": 5,
      "failure": {
        "message": "expected 2, got 3",
        "actual": "3",
        "expected": "2",
        "location": "src/libraries/z42.core/tests/arith.z42:42",
        "stack_trace": "..."
      }
    },
    {
      "name": "test_known_broken",
      "module": "z42.core.tests.arith",
      "status": "skipped",
      "skip_reason": "blocked by issue #123"
    }
  ]
}
```

JSON Schema 落到 [docs/design/test-runner.md](docs/design/test-runner.md)（不单独建 .json schema 文件，避免过度工程）。

### Decision 7: 元数据 front-matter 规范

每个 `.z42` 测试源文件**首行注释**声明 tier：

```z42
// @test-tier: stdlib:z42.collections
// @test-deps: z42.core, z42.collections
// @test-feature: linked-list-iter

import z42.test.{Test, Assert};
import z42.collections.LinkedList;

[Test]
fn test_iter_empty() { /* ... */ }
```

合法 front-matter 字段：

| 字段 | 必填 | 取值 |
|------|-----|------|
| `@test-tier` | ✅ | `vm_core` \| `stdlib:<lib>` \| `integration` |
| `@test-deps` | 可选 | 逗号分隔的依赖库名 |
| `@test-feature` | 可选 | 自由文本，标注被测特性 |
| `@test-tag` | 可选 | 逗号分隔标签（用于 --filter 增强） |

front-matter 由 `scripts/test-changed.sh` 与未来工具消费；runner 本身不使用 front-matter（只看 IR attribute）。

### Decision 8: scripts/test-changed.sh 接口

```bash
./scripts/test-changed.sh [--base <ref>] [--head <ref>] [--dry-run]

# 默认 base=main, head=HEAD
# 输出受影响测试集到 stdout（JSON），exit 0
# --dry-run 只列出，不执行
```

输出格式：

```json
{
  "compiler": true,           // 是否需要跑 compiler 测试
  "vm_core": true,            // 是否需要跑 vm_core
  "stdlib": ["z42.io", "z42.collections"],   // 受影响的 stdlib 库
  "integration": false        // 是否需要 integration
}
```

`just test-changed` 消费这个 JSON，触发对应子集。

#### 受影响计算规则

| 改动路径前缀 | 影响 |
|-------------|------|
| `src/compiler/**` | compiler=true, vm_core=true, stdlib=*all*, integration=true（编译器是基座） |
| `src/runtime/src/interp/**` 或 `src/runtime/src/gc/**` | vm_core=true, stdlib=*all*, integration=true |
| `src/runtime/src/jit/**` | vm_core=true（仅 JIT 相关） |
| `src/runtime/crates/<crate>/**` | 该 crate 单测 + vm_core |
| `src/libraries/<lib>/**` | stdlib=[<lib>] + 反向依赖该库的库 + integration |
| `docs/**` 或 `spec/**` | 全 false（仅 lint） |
| `scripts/**` 或 `justfile` 或 `.github/**` | compiler=true, vm_core=true（保守起见） |

反向依赖关系硬编码在 `scripts/test-changed.sh` 顶部（数量小，~6 个库）；后续可改为读 .z42.toml 自动算。

### Decision 9: runner 内部执行流程

```
1. 解析 CLI args
2. 收集所有 .zbc 文件（递归 paths）
3. 对每个 .zbc：
   a. 用 z42-runtime decoder 加载
   b. 扫描所有方法，提取带 z42.test.Test attribute 的
   c. 对每个测试方法：
      - 应用 --filter 正则
      - 检查 [Skip] / [Ignore]
      - new Interpreter，加载依赖 zpkg
      - call_function(test_method, []) 包在 catch_unwind 里
      - 测时（std::time::Instant）
      - 收集 TestResult（passed / failed / skipped）
4. 选择 formatter（tap / json / pretty）
5. 输出 + exit code
```

### Decision 10: justfile 替换 P0 占位

```just
# 替换 P0 占位
test-changed:
    #!/usr/bin/env bash
    affected=$(./scripts/test-changed.sh)
    if echo "$affected" | jq -e '.compiler' >/dev/null; then just test-compiler; fi
    if echo "$affected" | jq -e '.vm_core' >/dev/null; then just test-vm; fi
    for lib in $(echo "$affected" | jq -r '.stdlib[]'); do
        cargo run -p z42-test-runner -- "src/libraries/$lib/tests/"
    done
    if echo "$affected" | jq -e '.integration' >/dev/null; then just test-integration; fi

# 新增（被 P3 后续填充）
test-stdlib lib="":
    @if [ -z "{{lib}}" ]; then \
        for d in src/libraries/*/tests; do cargo run -p z42-test-runner -- "$d"; done; \
    else \
        cargo run -p z42-test-runner -- "src/libraries/{{lib}}/tests/"; \
    fi

test-integration:
    @echo "P3 待实施：integration 测试目录" && exit 1
```

## Implementation Notes

### z42.test 与 runner 的协议

- runner 不直接知道 z42.test 库的存在
- runner 只识别 `z42.test.Test` 字符串 attribute
- assertion 失败 → z42.test.Failure 抛出 → runner catch（通过 z42 runtime 的异常机制）
- runner 把 Failure 的字段（actual / expected / location）映射到 TestResult

### 编译器侧支持

- `[Test]` 是普通 attribute；编译器**不需要**特殊处理（已有 attribute 通用机制）
- 唯一可能的改动：[src/compiler/z42.IR/Metadata/AttributeKind.cs](src/compiler/z42.IR/Metadata/AttributeKind.cs) 注册已知 attribute name 列表（若有此机制）；若没有，跳过

### Workspace 结构调整

[src/runtime/Cargo.toml](src/runtime/Cargo.toml) 已是 workspace（C1 引入），加入 member：

```toml
[workspace]
members = [
    "crates/z42-abi",
    "crates/z42-rs",
    "crates/z42-macros",
    "../toolchain/test-runner",
]
```

注意 `../toolchain/test-runner` 需相对路径（在 src/toolchain/ 下）；workspace 跨目录引用是 cargo 支持的。

### CLI 参数解析用什么 crate

- `clap` v4 (derive 风格)
- 已是事实标准，与现有 z42 CLI 一致

### 输出 JSON 的库

- `serde_json`
- 不引入新依赖（已在 workspace 用了）

## Testing Strategy

### z42-test-runner 自身的测试

[src/toolchain/test-runner/tests/integration_test.rs](src/toolchain/test-runner/tests/integration_test.rs)：

- 用最小的 fixture .zbc（手写 / 编译 hello-world）验证：
  - discover 能找到 [Test] 方法
  - runner 调用通过的测试 → status: passed
  - runner 调用 panic 的测试 → status: failed
  - --filter 正则过滤生效
  - --format tap 输出符合 TAP 13
  - --format json 输出符合 schema
  - exit code 在不同状态下正确

### z42.test 库的测试

[src/libraries/z42.test/tests/](src/libraries/z42.test/tests/) 占位目录（实际测试用例 P3 / 后续补）。

### scripts/test-changed.sh 的测试

shell 单测放在 [tests/scripts/test-changed.bats](tests/scripts/test-changed.bats)（用 bats-core 框架）—— 但本 spec **不强制**引入 bats，可以人工验证：

- 模拟 git diff 触及 `src/libraries/z42.io/foo.z42` → 输出 `stdlib: ["z42.io"]`
- 模拟 git diff 触及 `src/runtime/src/gc/heap.rs` → 输出 `vm_core: true`

### Spec 验证矩阵

| Scenario | 验证方式 |
|----------|---------|
| 测试发现 | runner 集成测试 |
| TAP 输出 | runner 集成测试 + 人工 diff |
| JSON 输出 | runner 集成测试 + JSON Schema 校验 |
| 失败 exit 码 | runner 集成测试 |
| --filter | runner 集成测试 |
| z42.test API | z42.test 自身的 .z42 测试（runner 自举） |
| test-changed.sh 规则 | 人工模拟 git diff 验证 |
