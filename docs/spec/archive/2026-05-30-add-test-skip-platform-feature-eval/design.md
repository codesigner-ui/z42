# Design: `[Skip(platform:)] / [Skip(feature:)]` runtime evaluation

## Architecture

```
                 [TIDX 字段（已存在）]
                 ┌─────────────────────────┐
                 │ skip_platform: Option<String>
                 │ skip_feature : Option<String>
                 │ skip_reason  : Option<String>
                 │ flags.SKIPPED                  │
                 └────────────┬────────────────────┘
                              │ discover.rs 透传到
                              ▼
                 ┌─────────────────────────┐
                 │ DiscoveredTest{        │
                 │   skip_platform,        │
                 │   skip_feature,         │
                 │   skip_reason,          │
                 │   …                     │
                 │ }                       │
                 └────────────┬────────────┘
                              │
   ┌──────────────────────────┼──────────────────────────┐
   ▼                          ▼                          ▼
runner.rs               exec.rs                    parallel.rs
(in-proc)               (subprocess legacy)        (subprocess parallel)
   │                          │                          │
   └─────── skip_eval::decide_skip(test, &SkipEnv) ──────┘
                              │
              ┌───────────────┴───────────────┐
              ▼                               ▼
       Some(reason)                       None
       → Outcome::Skipped             → run test normally
```

`SkipEnv` 一次构造（main.rs 启动时），通过引用传给所有 worker / 子进程入口，
不放进 thread-local 或 global static — 显式参数让单元测试可以
construct 任意 env 做参数化测试。

## Decisions

### Decision 1: 平台名值的来源

**问题**：runner 应该如何知道"当前在哪个平台上跑"？

**选项：**

- A. 在 runner 启动时执行一段 z42 代码调 `Std.Platform.OS()` 拿值（与 stdlib 同源）
- B. Runner Rust 端直接读 `std::env::consts::OS`（与 stdlib 同源 — 因为 stdlib
   builtin 实现就是 `std::env::consts::OS.to_string()`）
- C. 让用户在 CLI / 配置文件里手动声明

**决定**：B。Why：
- A 引入了 runner→z42 bootstrap 的额外依赖（每次启动多一次 VM call），同时
  当 stdlib 未链接 / Platform 类未注册时启动会失败
- stdlib builtin 和 Rust runner 都读同一个 `std::env::consts::OS` 编译期常量，
  值天然一致 — 跳过 B→A 的间接层
- C 留给"override"通道：`--platform <NAME>` CLI flag + `Z42_TEST_PLATFORM`
  env var，**只在覆盖时**才走用户输入；自动检测路径不依赖

### Decision 2: Compound skip 条件的合取/析取

**问题**：`[Skip(platform: "ios", feature: "jit")]` 应当何时跳过？

**选项：**

- A. AND — 仅当 ON ios AND jit 不可用时跳过（两个条件都成立才跳）
- B. OR — ON ios OR jit 不可用，任一成立就跳

**决定**：B (OR)。Why：
- 直觉对齐：用户给 `[Skip(platform: ios, feature: jit)]` 的语义是
  "iOS 上 **或** JIT 不可用时，这个 test 都不该跑"。AND 会让"在 iOS 上但 JIT 又
  可用"的环境意外跑过去
- 反例：用户其实想 AND 时可以拆成两条 [Skip] attribute（虽然 validator 不允许重复
  Skip — 留 future work：重复 Skip 是否 AND？）
- pytest-style `@pytest.mark.skipif(condition1 or condition2, ...)` 也是 OR

### Decision 3: 未知 feature 名的处理

**问题**：用户写 `[Skip(feature: "quantum_entanglement")]`，注册表里没这个 feature
名，怎么办？

**选项：**

- A. 默认 available（无视未知 feature），test 照跑 — fail-open
- B. 默认 unavailable（未知 feature 名 → 触发跳过）— fail-closed
- C. 报硬错 — `error: unknown feature "quantum_entanglement"`，halt run

**决定**：B (fail-closed)。Why：
- A 在打错字（`"multithreading"` typo 成 `"multi-threading"`）时静默吞掉用户意图，
  test 在本该跳的平台上跑挂，难定位
- C 阻塞整个 test run，破坏 "runner 是工具，要鲁棒" 的基本期望；测试代码里的
  typo 不应该让其他 100 个 test 也跑不起来
- B 默认安全：未知 feature 名当成 "我们这环境也不支持"，宁可多跳几个也别少跳；
  同时打一行 stderr `note: unknown feature "X" — treating as unavailable` 给
  用户提示

### Decision 4: Feature registry 的初始集

**问题**：v1 注册哪些 feature 名？

**初始 4 个**（与 examples/test_demo.z42 已经用到的对齐 + 常见诉求）：

| Feature | Available when | Why included |
|---------|----------------|--------------|
| `interp` | 始终 true | 显式声明的"基线"；用户写 `[Skip(feature: interp)]` 期望永不跳 (除非未来 interp 编译进不去；本 spec 设 true) |
| `jit` | 始终 true | interp+JIT 都编译进 z42vm（运行模式选择是 per-method，不是编译期），所以"JIT 不可用"在当前 build 不存在 |
| `multithreading` | `cfg!(not(target_arch = "wasm32"))` | wasm 单线程，其他平台多线程；test_demo.z42:24 用例 |
| `filesystem` | `cfg!(not(target_arch = "wasm32"))` | 同上；test_demo.z42:28 用例 |

> **不在 v1 引入**：`async`、`gc-precise`、`debug-symbols`、`network` —
> 当前没用例；deny-by-default 让用户写到时再扩注册表。

### Decision 5: Skip reason 字符串内容

**问题**：runner 输出"why skipped"时，reason 应当包含哪些信息？

**决定**：按触发条件分类：

| 触发条件 | 输出格式 |
|---------|---------|
| platform 匹配 | `"skipped on <platform>: <user reason>"` 或 `"skipped on <platform>"` |
| feature 不可用 | `"skipped (feature '<name>' unavailable): <user reason>"` 或 `"skipped (feature '<name>' unavailable)"` |
| compound 两条件都成立 | `"skipped on <platform>; feature '<name>' unavailable: <user reason>"` |
| 无条件（仅 reason）| `<user reason>` 直出（兼容旧 R1.A 输出） |
| 无 reason 也无条件 | `"skipped"` 保留 |

Why：reason 字段是给**人**读的输出。失败排查时第一眼看到的就是这行，必须直接
回答 "为什么跳了"。把"触发条件"加进 reason 前缀 = 不读 IR / 不查源码就能定位。

### Decision 6: `SkipEnv` 的可见性 / 生命周期

**问题**：把当前平台 + features 表打包成什么类型？

**决定**：

```rust
pub struct SkipEnv {
    pub current_platform: String,        // 小写，e.g. "linux"
    pub available_features: HashSet<String>,
}
impl SkipEnv {
    pub fn detect() -> Self { … }        // 从 cfg!() + env
    pub fn with_platform(self, p: String) -> Self { … }  // CLI override
}
```

- `pub`：unit tests 可以直接构造 `SkipEnv { current_platform: "ios".into(), available_features: hashset!{"jit"} }`
  做参数化矩阵
- `detect()`：boot 路径，读 `std::env::consts::OS` + cfg flags
- `with_platform`：builder pattern 给 main.rs 应用 CLI override
- 不放进 `LoadedRunner` —— skip 决策应当在 test 被分发到 worker **之前**就完成；
  也避免 runner.rs 里塞太多无关字段

## Implementation Notes

### `skip_eval::decide_skip` 核心算法

```rust
pub fn decide_skip(test: &DiscoveredTest, env: &SkipEnv) -> Option<String> {
    // 没有 SKIPPED flag → 完全不考虑（test 正常跑）
    if !test.flags.contains(TestFlags::SKIPPED) {
        return None;
    }

    let plat_match = test.skip_platform.as_ref()
        .map(|p| p == &env.current_platform);
    let feat_unavail = test.skip_feature.as_ref()
        .map(|f| !env.available_features.contains(f));

    let triggered = match (plat_match, feat_unavail) {
        (None, None)                  => true,                 // 无条件
        (Some(p), None)               => p,
        (None, Some(f))               => f,
        (Some(p), Some(f))            => p || f,               // OR
    };

    if !triggered { return None; }                             // 不满足条件 → 正常跑

    Some(format_reason(test, env, plat_match, feat_unavail))   // 构造人话 reason
}
```

边界：

- `flags.SKIPPED = true` 但 platform 不匹配且 feature 可用 → **跑** test
  （正面验证：runner.rs/exec.rs 不能预先依赖 flag 来判定 "肯定跳"）
- `flags.SKIPPED = false` 但 skip_platform=Some — 不可能（compiler 已保证 flag
  随 attr 同步设置）；defensive 返回 None

### Reason formatter

`format_reason(test, env, plat_match, feat_unavail) -> String`：按 Decision 5
表格分类拼接，单元测试覆盖每条分支。

### 多 worker 子进程模式（parallel.rs / exec.rs）

`SkipEnv` 不需要序列化送子进程 — skip 决策在父进程完成，子进程只收到"已通过
skip eval"的 test 列表。**好处**：子进程不需要知道平台，避免分布式不一致；
**坏处**：CLI override 必须在父进程进入 worker pool 前应用（main.rs 顺序：
parse args → build SkipEnv → filter discover list → schedule workers）。

实施时把"对每个 test 调 decide_skip"放在 main.rs 的 schedule loop，命中 Skipped
直接 push Outcome::Skipped 到 results；子进程不感知 skip 概念。

## Testing Strategy

**单元测试** (`skip_eval_tests.rs`)：参数化矩阵覆盖 Decision 1–5：

| Case | flags.SKIPPED | skip_platform | skip_feature | current_platform | features      | Expected |
|------|---------------|---------------|--------------|------------------|---------------|----------|
| 1 — flag off | false        | -             | -            | linux            | {jit}         | None (跑) |
| 2 — uncond skip | true | -             | -            | linux            | {jit}         | Some (跳) |
| 3 — platform match | true | ios       | -            | ios              | {jit}         | Some (跳) |
| 4 — platform miss  | true | ios       | -            | linux            | {jit}         | None (跑) |
| 5 — feature avail  | true | -         | jit          | linux            | {jit}         | None (跑) |
| 6 — feature unavail| true | -         | jit          | linux            | {}            | Some (跳) |
| 7 — OR plat hit, feat hit  | true | ios | jit          | ios              | {}            | Some |
| 8 — OR plat miss, feat unavail | true | ios | jit       | linux            | {}            | Some |
| 9 — OR plat hit, feat avail | true | ios | jit         | ios              | {jit}         | Some |
| 10 — OR plat miss, feat avail | true | ios | jit       | linux            | {jit}         | None (跑) |
| 11 — reason 拼接 platform-only | true | ios | -        | ios | -| reason 含 "on ios" |
| 12 — reason 拼接 feature-only | true | - | jit         | linux | {} | reason 含 "feature 'jit' unavailable" |
| 13 — reason 拼接 compound | true | ios | jit           | ios | {} | reason 含 "on ios; feature 'jit' unavailable" |
| 14 — reason 拼接 plain | true | - | -                 | linux | -| reason == user reason or "skipped" |

**E2E** (`src/libraries/z42.test/tests/skip_platform_demo.z42`)：

- 1 个 platform-match 用例（写 `[Skip(platform: "<host-os>")]`，runner 必须跳）
- 1 个 platform-miss 用例（写 `[Skip(platform: "atari")]`，runner 必须跑通）
- 1 个 feature-gated 用例（写 `[Skip(feature: "quantum_entanglement")]`，
  unknown feature → 跳）

E2E 验证手段：跑 `./scripts/test-stdlib.sh` 输出包含 `not ok ... # SKIP` 和
`ok ...` 两类条目对应上述三个用例。

**CLI override** 覆盖：通过 unit test 在 `SkipEnv::detect` 之外手动 `with_platform`
即可验证；不需要专门跑 subprocess。

## Risks

- **`DiscoveredTest` 字段语义变化** — `skip_reason` 当前是 composite（"platform=ios;
  feature=jit; reason"），新 API 拆分后是 "用户原话"。下游 formatter（pretty / TAP /
  JSON）若直接消费 `skip_reason` 可能丢"触发条件"信息。**Mitigation**：把"触发条件"
  并入 `Outcome::Skipped { reason }`（由 `decide_skip` 生成），formatter 不变。
- **examples/test_demo.z42 行为改变** — 该文件原本 3 个 `[Skip]` 都"碰巧跳过"
  （runner 无条件跳）。spec 落地后，2/3 会取决于 host：linux 上跑 `test_demo`
  时，第 1 个 `Skip(platform: "ios")` 会**跑**（不再跳）。**Mitigation**：示例
  文件本就是 demo，跑过去成功是 OK 的；注释里说明新行为。
- **parallel mode 串行化 skip eval 增加 main 线程负担** — 实测可忽略（hashset
  contains + 字符串相等是 ns 级；test 数 < 10k）。
