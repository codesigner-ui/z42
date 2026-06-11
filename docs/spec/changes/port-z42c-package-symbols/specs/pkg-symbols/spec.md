# Spec: pkg-symbols

## ADDED Requirements

#### Scenario: 跨文件互引
- **WHEN** 包内 a.z42 定义类 E、b.z42 `new E()` 并访问其字段
- **THEN** 0 错；执行正确；multifile 工程双构建逐字节（gate 第 6 工程）

#### Scenario: arr.Length
- **WHEN** `int[] xs = ...; xs.Length`
- **THEN** typecheck int；ArrayLen 指令；与 C# 同源字节一致

#### Scenario: 自举首包
- **WHEN** `z42c build` 编译 z42c.core 全 7 文件
- **THEN** 0 错产出 z42c.core.zpkg（冒烟；对账下轮）
