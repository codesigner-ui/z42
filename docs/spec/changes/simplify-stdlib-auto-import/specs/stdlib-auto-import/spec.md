# Spec: 标准库自动可用

## ADDED Requirements

### Requirement: stdlib 无需声明即可用

#### Scenario: 空依赖用 stdlib 编译通过
- **WHEN** 用户项目 `[dependencies]` 为空，源码 `using Std.IO; using Std.Math;` 并调用 `Console.WriteLine` / `Math.Sqrt`
- **THEN** 编译成功，无"未声明依赖"错误（现状已如此，本变更固化为约定 + 回归测试）

#### Scenario: z42.test 框架也自动可用
- **WHEN** 空依赖，源码 `using Std.Test; new Bencher(...)`（z42.test 独有符号）
- **THEN** 编译成功

### Requirement: WS013 —— 冗余 stdlib 依赖声明警告

#### Scenario: 用户项目声明 stdlib 依赖 → 警告
- **WHEN** 一个 `[project].name` 不以 `z42.` 开头的项目，在 `[dependencies]` 或 `[tests.dependencies]` 声明了 `z42.io`（或任一 `z42.*`）
- **THEN** 报 WS013 警告：该 stdlib 依赖冗余、可删（stdlib 自动可用）

#### Scenario: stdlib 包之间 inter-dep 不警告
- **WHEN** stdlib 包 `z42.io`（name 以 `z42.` 开头）在 `[dependencies]` 声明 `z42.time`
- **THEN** **不**报 WS013（这是 workspace build 排序需要的合法 inter-dep）

### Requirement: Std.* 命名空间保留

#### Scenario: 第三方包声明 Std.* → 编译错误
- **WHEN** 一个非 `z42.*` 包的源文件声明 `namespace Std.Foo;`
- **THEN** 编译报错（E0605）：`Std.*` 是官方 stdlib 保留命名空间

#### Scenario: stdlib 包声明 Std.* → 通过
- **WHEN** `z42.io` 包内声明 `namespace Std.IO;`
- **THEN** 通过（官方 stdlib 合法使用）

## MODIFIED Requirements

### Requirement: 依赖声明约定

**Before:** 约定/示例鼓励"用啥 stdlib 声明啥"（虽然编译器本就自动加载，声明是冗余的）。

**After:** 约定明确为"stdlib 永不声明（Rust-std 模型）；`[dependencies]` / `[tests.dependencies]`
只用于第三方"。示例 / 模板移除冗余 stdlib 声明；lint（WS013）主动引导。

## Pipeline Steps
- [x] 依赖解析（已有 isStdlib 旁路，不改）
- [ ] Manifest lint（新增 WS013）
- [ ] Namespace 校验（新增 Std.* 保留 E0605）
- [ ] 文档 + 示例清理
