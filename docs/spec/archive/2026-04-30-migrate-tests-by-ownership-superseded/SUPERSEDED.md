# SUPERSEDED → R5 (rewrite-goldens-with-test-mechanism)

> 状态：🟠 SUPERSEDED（未实施，被 R5 替换） | 标记：2026-04-29

本 spec 的原方案（保留 stdout-based golden 格式，仅按归属物理迁移）被替换为：在 R1+R2+R3 基础上**重写**为 `[Test]` + `Assert` 形式（仅 stdlib / integration tier；vm_core 仍保留 stdout-based）。

## 替换 sub-spec

[rewrite-goldens-with-test-mechanism/](../rewrite-goldens-with-test-mechanism/) (R5)

## 与原 spec 的差异

| 维度 | 原 spec (此目录) | R5 替换 |
|------|---------------|--------|
| vm_core 用例 | 物理迁移 + front-matter 标注 | **保留 stdout-based** + front-matter 标注（避免 z42.test 循环依赖） |
| stdlib 用例 | 物理迁移，保 source.z42 + expected_output.txt | **重写为 `[Test]` + `Assert`**，按 API 拆多个测试函数 |
| integration 用例 | 物理迁移 | 重写为 `[Test]` + `Assert` + `TestIO.captureStdout` |
| stdlib 各库本地测试 | 各补 ≥ 1 个原生测试 | 同（本就是从重写衍生） |
| 转换工具 | 无（手动迁移） | `scripts/_rewrite-goldens.py` 半自动 |
| 依赖前置 | 仅 P0/P2（即 add-just-and-ci + add-z42-test-runner） | R1 + R2 + R3 + R4 全部（编译时 attribute + 完整 z42.test API + 新 runner） |

## 为什么替换

原方案只能"按归属物理分组"，但用例本身仍是"打印 stdout 字符串再字面量比对"。这意味着：
- assertion 表达力不变（仍只能 grep stdout）
- 一个用例 = 一个程序，无法测细粒度行为
- 无法表达"应抛 X 异常"、"浮点近似相等"、"集合无序相等"等

R5 同时完成"归属分流" + "重写为细粒度 [Test]"，一次到位避免重复劳动。

## 处理建议

- 不实施本 spec
- 实施 R5 替代（依赖 R1+R2+R3 先落地）
- 本目录保留作为审计记录
