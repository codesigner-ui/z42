# SUPERSEDED → R1 + R2 + R3 + R4

> 状态：🟠 SUPERSEDED（未实施，被 R 系列替换） | 标记：2026-04-29

本 spec 的原方案（**运行时**扫 .zbc 发现 `[Test]` attribute）被替换为**编译时**测试发现路线（参 Rust libtest / Go testing）。

## 替换映射

| 原 spec 范围 | 替换 sub-spec |
|------|---------|
| 运行时 attribute 扫描 → 编译时 IR `TestIndex` section | [add-test-metadata-section/](../add-test-metadata-section/) (R1) |
| z42.test 库 Assert API 扩展 + TestIO + Setup/Teardown | [extend-z42-test-library/](../extend-z42-test-library/) (R2) |
| z42-test-runner 工具实现（消费 TestIndex 而非扫描方法表） | [rewrite-z42-test-runner-compile-time/](../rewrite-z42-test-runner-compile-time/) (R3) |
| 编译期校验测试函数签名 + 异常类型 | [compiler-validate-test-attributes/](../compiler-validate-test-attributes/) (R4) |

## 为什么替换

原方案（运行时发现）的不足：
1. 错签名（如 `[Test] fn bad(x: i32)`）要等到 runner 启动时才报错
2. 每次 runner 启动需扫整个 zbc method table
3. 与 Rust libtest 的成熟模式（编译时收集到元数据表）偏离

R 系列方案对齐 Rust libtest / Go testing：
- `[Test]` 编译时收集到 zbc 的 `TestIndex` section
- runner 直接读 section（O(1) 启动）
- 编译期校验签名 + 期望异常类型存在

## 处理建议

- 不实施本 spec
- 实施 R1 → R2 → R3 → R4 替代
- 本目录保留作为审计记录（不归档到 archive/，因为没有 archive 一个未实施的 spec 的语义）

详见 [redesign-test-infra/](../redesign-test-infra/)（已归档为 [2026-04-29-redesign-test-infra/](../../archive/2026-04-29-redesign-test-infra/)，不存在；保留在 changes/ 是 draft）以及 R 系列各 sub-spec 的 proposal.md 的 "Why" 段。
