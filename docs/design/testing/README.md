# design/testing/

z42 测试基础设施：测试框架（z42.test）、测试运行器（z42-test-runner）、跨平台测试。

## 职责

- 描述测试 attribute 体系（`[Test] [Benchmark] [Setup] [Teardown] [Skip] [Ignore] [ShouldThrow<E>]`）
- 描述编译期测试发现机制（TIDX section）
- 描述 z42-test-runner 工具（subprocess / 多格式输出 / filter / changed-only）
- 描述跨平台测试调度

## 核心文件

| 文件 | 职责 |
|------|------|
| [`testing.md`](testing.md) | R 系列测试基础设施：单机框架、attribute 校验、Std.Test.Assert / Bencher |
| [`test-runner-bootstrap.md`](test-runner-bootstrap.md) | z42-test-runner 工具实现：subprocess 模式、format / filter、changed-only 路由 |
| [`cross-platform-testing.md`](cross-platform-testing.md) | 同一 .zbc 多平台运行 + runner-as-library + 平台 Skip 机制 |

## 入口点

- 写新测试：[`testing.md`](testing.md)（Assert / TestIO / Bencher API）
- 改 runner：[`test-runner-bootstrap.md`](test-runner-bootstrap.md)
- 跨平台问题：[`cross-platform-testing.md`](cross-platform-testing.md)

## 依赖关系

- 上游：[`../compiler/error-codes.md`](../compiler/error-codes.md)（E0911–E0915 测试 attribute 校验码）
- 下游：`z42 xtask.zpkg test vm`、`z42 xtask.zpkg test lib`、CI 工作流
