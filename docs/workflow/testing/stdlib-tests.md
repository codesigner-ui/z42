# stdlib 内部测试

每个 stdlib 包都有自带的 `[Test]`-注解 z42 测试文件，存于 `src/libraries/<lib>/tests/`。由 `z42-test-runner` 工具调度。

## 命令

```bash
z42 xtask.zpkg test lib             # 6 个库全跑
z42 xtask.zpkg test lib z42.math    # 仅指定库
```

或 `just`：

```bash
just test-stdlib
just test-stdlib z42.math
```

## 测试发现机制

1. 编译期 — 每个测试 `.z42` 文件含 `[Test]` / `[Benchmark]` / `[Skip]` / `[ShouldThrow<E>]` 注解的 free function
2. C# 编译器把这些 metadata 写到 zbc 的 TIDX section
3. `z42-test-runner` 从 zbc 读 TIDX，按 entry fork 子进程跑（每个 test 独立 VM 实例 + 独立栈）
4. 按 stderr 内容分类 Pass / Skip / Fail

详细设计见 [`docs/design/testing/testing.md`](../../design/testing/testing.md) "R 系列实施进度" 段。

## Runner 输出格式

```bash
z42 xtask.zpkg test lib                            # 默认按 TTY 自动选 pretty / tap
z42 xtask.zpkg test lib --format pretty            # 人类可读
z42 xtask.zpkg test lib --format tap               # TAP 13（CI 友好）
z42 xtask.zpkg test lib --format json              # JSON（custom schema）
z42 xtask.zpkg test lib --filter <SUBSTR>          # 子串过滤
```

## 加新测试

```z42
// src/libraries/<lib>/tests/MyFeatureTests.z42
namespace Std.<Lib>.Tests;

[Test]
public static void test_basic_case() {
    var actual = SomeFunc(1, 2);
    Assert.Equal(3, actual);
}

[Test]
[ShouldThrow<ArgumentException>]
public static void test_throws_on_invalid_input() {
    SomeFunc(-1, 0);
}
```

写完后 `z42 xtask.zpkg test lib <lib>` 即可发现。

## 与 C# 单测的区别

| 维度 | C# 单测（`unit-tests.md`）| stdlib `[Test]`（本文）|
|------|---|---|
| 写在 | C# `*Tests.cs` | z42 `*Tests.z42` |
| 测什么 | 编译器内部（lexer / parser / TC / IR）| stdlib 源码（运行时行为）|
| Runner | xUnit | z42-test-runner |
| 加入 GREEN | ✅ | ✅ |
