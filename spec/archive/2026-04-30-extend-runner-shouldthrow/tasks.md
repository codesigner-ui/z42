# Tasks: Runner ShouldThrow Runtime Check (A2)

> 状态：🟢 已完成 | 归档：2026-04-30 | 创建：2026-04-30
> 类型：refactor + small feature；最小化模式（仅 tasks.md，不需独立 proposal/design/spec）
> 依赖：A1（add-generic-attribute-syntax，✅ 2026-04-30）已经把 expected_throw_type 写到 zbc

## 变更说明

z42-test-runner 读 TIDX 的 `expected_throw_type` 字段，比对实际抛出异常类型，实现 [ShouldThrow<E>] 运行时校验。同时把 dogfood.z42 中两个 `[Skip]` 占位替换为 `[ShouldThrow<E>]` —— A1 留下的最后一段闭环。

## 原因

A1 编译期把 `[ShouldThrow<E>]` 信息写入 zbc 但 runner 不消费——dogfood 仍跳过两个负向测试。本变更让 runner 真正比对，使 z42.test 自检完整闭环。

## 文档影响

- `docs/design/testing.md` R4.B 段："runtime 比对延后到 A2 spec" 删掉，改写为"已实现"
- `docs/roadmap.md` M6 段：A2 标记完成

## 实现思路

### Runner 侧分类逻辑（[src/toolchain/test-runner/src/main.rs](src/toolchain/test-runner/src/main.rs)）

`DiscoveredTest` 加 `expected_throw: Option<String>`（仅当 `entry.flags.contains(SHOULD_THROW)` 且 `entry.expected_throw_type` 非空时填充）。

`run_one` 分类：

| should_throw | exit | stderr 匹配 expected | 结果 |
|---|---|---|---|
| None | 0 | — | Passed |
| None | ≠0 | stderr 含 SkipSignal | Skipped |
| None | ≠0 | stderr 含 TestFailure | Failed (assertion) |
| None | ≠0 | other | Failed (other exception) |
| Some(E) | 0 | — | **Failed**（expected E, got nothing） |
| Some(E) | ≠0 | 类型匹配 E | **Passed**（throw 符合预期） |
| Some(E) | ≠0 | 类型不匹配 | **Failed**（expected E, got X） |

### 类型匹配

stderr 形如 `Error: uncaught exception: Std.TestFailure: <msg>`。

匹配规则（按优先级）：
1. 提取 stderr 中 `uncaught exception: <FQ_TYPE>:` 段的 `<FQ_TYPE>`（如 `Std.TestFailure`）
2. 比较 `expected_throw_type` 与 `<FQ_TYPE>`：
   - 完整字符串相等 → 匹配
   - `expected` 等于 `<FQ_TYPE>` 最后一段（短名 `TestFailure`）→ 匹配
   - 否则不匹配

避免假阳性：不能用 `stderr.contains("TestFailure")` —— 否则 `MyTestFailure` 也会命中。

## 检查清单

- [x] 1.1 [src/toolchain/test-runner/src/main.rs](src/toolchain/test-runner/src/main.rs) `DiscoveredTest` struct 加 `expected_throw: Option<String>` 字段
- [x] 1.2 同文件 `from_artifact` 中：当 `entry.flags.contains(TestFlags::SHOULD_THROW)` 且 `entry.expected_throw_type.is_some()` 时填充
- [x] 1.3 同文件 `run_one`：`expected_throw is Some` 路径单独处理：
    - exit success → `Failed("expected to throw `{expected}`, but no exception was thrown")`
    - exit fail + 类型匹配 → `Passed { duration_ms }`
    - exit fail + 类型不匹配 → `Failed("expected to throw `{expected}`, got `{actual}`")`
- [x] 1.4 同文件加 helper `extract_thrown_type(stderr) -> Option<String>` 提取 `uncaught exception:` 后第一个 `:` 之前的标识符
- [x] 1.5 同文件加 helper `type_matches(expected, actual_fq) -> bool`：完整匹配 OR `expected` 等于 `actual_fq` 短名（最后一段）
- [x] 1.6 单元测试 `#[cfg(test)] mod tests` 在 main.rs 末尾（或独立 main_tests.rs，参 runtime-rust.md）：
    - extract_thrown_type 5 case：成功 / 多行 / 缺前缀 / 短名 / 全限定
    - type_matches 5 case：完整匹配 / 短名匹配 / 不匹配 / 短名不同 / 空字符串
- [x] 2.1 [src/libraries/z42.test/tests/dogfood.z42](src/libraries/z42.test/tests/dogfood.z42#L49-L59) 把 `[Skip(...)]` → `[ShouldThrow<TestFailure>]` / `[ShouldThrow<SkipSignal>]`
- [x] 2.2 在 dogfood.z42 顶部说明里把"待 R4 [ShouldThrow]"措辞改成"R4.B 完整闭环"
- [x] 3.1 `cargo build -p z42-test-runner --release` 通过
- [x] 3.2 `cargo test -p z42-test-runner` 通过（runner 单测）
- [x] 3.3 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 814/814 不回归
- [x] 3.4 `./scripts/test-vm.sh` 104/104 × 2 不回归
- [x] 3.5 `./scripts/test-stdlib.sh` —— **预期变化**：dogfood 现在 7 passed / 0 skipped（而非之前 5/2）
- [x] 3.6 `./scripts/test-cross-zpkg.sh` 1/1 不回归
- [x] 4.1 [docs/design/testing.md](docs/design/testing.md) R4.B 段"当前不做的"删掉 runtime 比对一行
- [x] 4.2 [docs/roadmap.md](docs/roadmap.md) M6 A2 标记完成
- [x] 5.1 commit + push + 归档本 spec 到 archive

## 备注

- runner 改动只用 `expected_throw_type` 的 resolved 字符串字段（R1.C 已 resolve 在 loader 里）；TIDX 字段 `expected_throw_type_idx` 不直接读
- 类型匹配不做 namespace 解析或 inheritance walk —— 例：`[ShouldThrow<Exception>]` 不会匹配 `Std.TestFailure`（即使 TestFailure 继承 Exception）。如未来需要，独立 spec 加 inheritance check（要求 runner 知道完整类型层次）
- 当前测试层面 dogfood 替换后的两个测试将作为"runner ShouldThrow 路径"的端到端验证
