# Spec: 0.4.x 退出标准

## ADDED Requirements

### Requirement: 0.4.x 四流退出标准

0.4.x 在以下五条全部达成时退出（进入 0.5.x）。

#### Scenario: P 流（perf）退出
- **WHEN** P 流声明完成
- **THEN** P1（JIT 算术拆箱）+ P2（inline cache + quickening）落地，且各有 bench 证明收益（非回归）
- **AND** P 流触及的库全部 baseline 化，perf CI 对其退化 >10% 时 fail

#### Scenario: B 流（bench）退出
- **WHEN** B 流声明完成
- **THEN** 独立 `z42.bench` 包存在 + `z42c bench` 命令 GA（反射驱动 `[Benchmark]` 发现）
- **AND** e2e bench 从 informational 升级为硬门禁，PR 自动评论回归 diff

#### Scenario: S 流（syntax）退出
- **WHEN** S 流声明完成
- **THEN** `params` / `init` + 表达式体属性 / 索引器 / 命名实参 / `partial` class 全部 spec→GREEN
- **AND** stdlib 或自举编译器代码 dogfood 验证至少一处真实使用

#### Scenario: L 流（lib）退出
- **WHEN** L 流声明完成
- **THEN** JSON `Deserialize<T>` 泛型 serde 可用（依赖 G 流）+ CLI 值校验/全局 flag/shell 补全落地
- **AND** stdlib 模块划分审计清单逐项清零 + `z42-doc` 无错产出

#### Scenario: G 流（泛型前置）退出
- **WHEN** G 流声明完成
- **THEN** 运行期泛型实例化 + 泛型方法 Invoke + `MakeGenericType` + `Activator.CreateInstance<T>` 落地
- **AND** L 流 `Deserialize<T>` 招牌依赖被满足

#### Scenario: roadmap 引用一致性（本规划变更自身验证）
- **WHEN** roadmap.md 更新完成
- **THEN** 原 0.4.7「z42.bench」与 0.5.x「反射泛型扩展」条目被移除，无残留悬挂引用
- **AND** Feature→Version 映射 §15 反射行、依赖图反射链、横向工作流 z42c bench 启用版本、GREEN 演进、Toolchain 矩阵均与新 0.4.x 段一致

## Pipeline Steps

不适用（规划文档，非语言特性）。各子版本 spec 落地时各自声明受影响 pipeline 阶段。
