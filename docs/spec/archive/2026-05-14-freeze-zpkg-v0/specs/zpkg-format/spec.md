# Spec: `.zpkg` v0 wire format freeze

## ADDED Requirements

### Requirement: zpkg.md 专用文档存在且与代码一致

#### Scenario: 文档当前版本号

- **WHEN** 用户读 `docs/design/runtime/zpkg.md`
- **THEN** 文档当前版本显示 `major=0, minor=6`
- **AND** changelog 表覆盖 0.1 → 0.6 全部 minor 的引入内容
- **AND** 每行 changelog 含日期 + 触发 spec 的 archive 链接

#### Scenario: 内嵌 zbc 版本对齐说明

- **WHEN** zbc.md 描述 minor bump 流程
- **THEN** 显式说明 zbc minor bump → zpkg minor 必须 +1（耦合规则）
- **AND** zpkg.md 反向交叉引用 zbc.md changelog

### Requirement: ZpkgWriter 单一 truth source

#### Scenario: 当前 minor 由 writer 公开常量声明

- **WHEN** 代码引用 zpkg 当前 minor
- **THEN** 来自 `ZpkgWriter.VersionMajor` + `ZpkgWriter.VersionMinor`（C#）单一 const
- **AND** Rust `ZPKG_VERSION_MAJOR` + `ZPKG_VERSION_MINOR` 与 C# 常量一致（invariant test 校验等值）

### Requirement: 0.5 → 0.6 catch-up bump（补 zbc 1.5 漏 bump）

#### Scenario: ZpkgWriter.VersionMinor 升到 6

- **WHEN** 本 spec 实施完成
- **THEN** `ZpkgWriter.VersionMinor = 6`
- **AND** 该常量旁注释明确写 "inner zbc 1.5"（与 ZbcWriter.VersionMinor=5 对齐）
- **AND** `docs/design/runtime/zpkg.md` Minor changelog 0.6 行注明 catch-up 缘由（zbc 1.4 → 1.5 时 zpkg 漏 bump）

### Requirement: Strict-pin minor 匹配

#### Scenario: 同 minor 加载成功

- **WHEN** writer 写出 minor=6 的 zpkg
- **AND** reader 是同版本（reader's `ZPKG_VERSION_MINOR=6`）
- **THEN** reader 成功加载，无错误

#### Scenario: 旧 minor 拒收

- **WHEN** zpkg 文件 header minor=5
- **AND** reader 当前 `ZPKG_VERSION_MINOR=6`
- **THEN** reader 抛 `InvalidDataException`（C#）或 `bail!`（Rust）
- **AND** 错误 message 含 `regen via ./scripts/build-stdlib.sh`

#### Scenario: 未来 minor 拒收

- **WHEN** zpkg 文件 header minor=7
- **AND** reader 当前 `ZPKG_VERSION_MINOR=6`
- **THEN** reader 抛错并提示 `writer is at 0.6`

#### Scenario: major 不匹配拒收

- **WHEN** zpkg header major=1（未来 v1）
- **THEN** reader 抛错 message 含 `expected major 0`

### Requirement: 未识别 section 静默跳过

#### Scenario: 未来 section id 不破坏旧 reader

- **WHEN** 构造 zpkg 含 tag `XXXX` 的未识别 section（其余 META + STRS 正常）
- **THEN** reader 成功加载 + 给出正常 package info（name / version / namespaces）
- **AND** `XXXX` 段被忽略，不抛错

### Requirement: 字节级 Golden Fixture

#### Scenario: 5 个代表性 layout 覆盖

- **WHEN** `src/tests/zpkg-format/` 下有 fixture 集
- **THEN** 至少覆盖以下 5 个 layout：
  - `packed-minimal/`：单 lib 单模块；packed mode 最小形态（META + STRS + NSPC + EXPT + DEPS + SIGS + MODS）
  - `packed-multi-module/`：多 .z42 → 同一 zpkg；MODS 多条目 + 共享 STRS
  - `packed-with-tsig/`：含 cross-package generic / interface 引用（TSIG section）
  - `indexed-minimal/`：incremental cache form（FILE 替代 MODS）
  - `sym-only-sidecar/`：`FlagSymOnly` set；MDBG + BLID（zpkg sym-only sidecar）

#### Scenario: Fixture 双轨校验

- **WHEN** 跑 `dotnet test --filter Z42.Tests.Zpkg.FormatGoldenTests`
- **THEN** 对每个 fixture：
  - 现场重生成 source.zpkg 与 check-in 字节逐字节相等
  - 现场重生成 expected.json 与 check-in JSON 字段相等
- **AND** 任一失败显示 fixture 名 + 失败轨道 + 第一处差异位置

#### Scenario: Fixture regen 流程

- **WHEN** 开发者跑 `src/tests/zpkg-format/generate-fixtures.sh`
- **THEN** 所有 fixture 的 `source.zpkg` + `expected.json` 同步更新
- **AND** `git diff` 显示出 layout 实际变化

### Requirement: Writer 确定性

#### Scenario: 同输入字节级一致

- **WHEN** 给 ZpkgWriter 同一组 IrModule + manifest 跑两遍
- **THEN** 两次输出字节级完全一致

### Requirement: `z42c golden-json` 子命令支持 zpkg

#### Scenario: 按扩展名分发

- **WHEN** 执行 `dotnet z42c.dll golden-json foo.zpkg -o foo.json`
- **THEN** 输出 ZpkgGoldenJsonFormatter 归一化结果
- **WHEN** 执行 `dotnet z42c.dll golden-json foo.zbc -o foo.json`
- **THEN** 输出 ZbcGoldenJsonFormatter 归一化结果（既有行为）

### Requirement: workflow.md zpkg 联动条款

#### Scenario: zbc minor bump 流程加 zpkg 步骤

- **WHEN** 开发者阅读 `.claude/rules/workflow.md` "Bumping `.zbc` minor version" 子节
- **THEN** 步骤 5 显式要求：bump ZpkgWriter.VersionMinor + 同步 Rust 常量 + 加 zpkg.md changelog 行 + 跑 zpkg fixture regen

#### Scenario: 独立 zpkg minor bump 流程

- **WHEN** 仅改 zpkg outer（不动 zbc）
- **THEN** workflow.md 子节有 "Bumping `.zpkg` minor version (independent)" 描述：仅触 ZpkgWriter + zbc_reader.rs ZPKG 常量 + zpkg.md changelog + zpkg fixture regen，无 zbc 联动

## MODIFIED Requirements

### Requirement: ZpkgReader 版本检查（C# + Rust）

**Before**：
- C# `ZpkgReader.cs:279`: `if (major == 0 && minor < 5)`
- Rust `zbc_reader.rs:1191`: `if minor < 5`
- 注释/错误均提"requires 0.5+"（宽容窗口）

**After**：精确匹配 + 引用 writer 常量 + 错误信息含 regen 提示。

### Requirement: ZpkgWriter VersionMinor 注释

**Before**（line 38）：`"... inner zbc 1.4 ..."`（过期，zbc 已 1.5）

**After**：`"... 2026-05-14 freeze-zpkg-v0 catch-up: inner zbc 1.5 (fix-numeric-cast-lowering)..."`

## Pipeline Steps

本 spec 不引入新 IR / VM / Codegen / TypeCheck 行为；仅触：
- 文档（NEW `zpkg.md` + MODIFY `zbc.md` + `workflow.md` + `roadmap.md`）
- ZpkgWriter minor catch-up（0.5 → 0.6）
- ZpkgReader 版本检查精确化（C# + Rust）
- 测试基础设施（5 fixture + harness × 4 类 + invariant × 5）

不需要 lexer / parser / type-checker / codegen / VM interp 改动。
