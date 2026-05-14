# Spec: `.zbc` v1 wire format freeze

## ADDED Requirements

### Requirement: 文档与代码版本号一致

#### Scenario: 当前版本号

- **WHEN** 用户读 `docs/design/runtime/zbc.md`
- **THEN** 文档当前版本号显示 `major=1, minor=5`
- **AND** changelog 表覆盖 1.0 → 1.5 全部 minor 的引入内容（含日期 + 触发 spec）

#### Scenario: writer / reader 单一 truth

- **WHEN** 任何代码点引用当前 zbc minor
- **THEN** 该值来自 `ZbcWriter.VersionMinor` 单一 const（C#）+ `ZBC_VERSION_MINOR` 单一 const（Rust）
- **AND** 两个常量在 invariant test 中校验相等

### Requirement: Strict-pin minor 匹配

#### Scenario: 同 minor 加载成功

- **WHEN** writer 写出 minor=5 的 zbc
- **AND** reader 是同版本（reader's `ZBC_VERSION_MINOR=5`）
- **THEN** reader 成功加载，无错误

#### Scenario: 不同 minor 拒收（旧 zbc）

- **WHEN** zbc 文件 header minor=4
- **AND** reader 当前 ZBC_VERSION_MINOR=5
- **THEN** reader 抛 `InvalidDataException`（C#）或 `bail!`（Rust）
- **AND** 错误 message 包含字面字符串 `regen via ./scripts/regen-golden-tests.sh`

#### Scenario: 不同 minor 拒收（未来 zbc）

- **WHEN** zbc 文件 header minor=6
- **AND** reader 当前 ZBC_VERSION_MINOR=5
- **THEN** reader 抛错并提示 `writer is at 1.5`（reader 版本号）

#### Scenario: major 不匹配拒收

- **WHEN** zbc 文件 header major=2 minor=任意
- **THEN** reader 抛错 message 含 `expected major 1`

#### Scenario: pre-1.0 zbc 拒收

- **WHEN** zbc 文件 header major=0
- **THEN** reader 抛错 message 含 `pre-1.0 zbc not supported`

### Requirement: 未识别 section 静默跳过

#### Scenario: 未来 section id 不破坏旧 reader

- **WHEN** 用 hex 编辑器或 ZbcWriter 测试 API 在 zbc 中插入一个 tag=`ZZZZ` 的 section（reader 不识别）
- **AND** 该 zbc 其余 section（STRS / FUNC / TYPE）正常
- **THEN** reader 成功加载并解出 module
- **AND** `ZZZZ` section 被忽略（不抛错、不读其内容）

#### Scenario: 必需 section 缺失仍按既有路径报错

- **WHEN** zbc 缺 STRS section
- **THEN** reader 抛错（既有行为，本 spec 不改）

### Requirement: 字节级 Golden Fixture

#### Scenario: 代表性 layout 覆盖

- **WHEN** `src/tests/zbc-format/` 下有 fixture 集
- **THEN** 至少覆盖以下 6 个 layout：
  - `empty`：最小 zbc（仅 NSPC + STRS + FUNC，零类零函数体）
  - `strp-func-minimal`：单类单方法，无 debug 信息
  - `with-dbug-blid`：含 debug + build_id（HasDebug flag set）
  - `with-tidx`：含 test index（[Test] 注解触发）
  - `with-frcs`：含 frame info section
  - `cross-import-token`：含 IMPORT_BASE 编码的跨包引用

#### Scenario: Fixture 双轨校验

- **WHEN** 跑 `dotnet test --filter FormatGoldenTests`
- **THEN** 对每个 fixture：
  - 现场重生成 source.zbc 与 check-in 字节逐字节相等
  - 现场重生成 expected.json 与 check-in JSON 字段相等
- **AND** 任一 fixture 失败 → 显示 fixture 名 + 失败轨道（bytes / json）+ 第一处差异位置

#### Scenario: Fixture regen 流程

- **WHEN** 开发者修改了 wire format 并跑 `src/tests/zbc-format/generate-fixtures.sh`
- **THEN** 所有 fixture 的 `source.zbc` + `expected.json` 同步更新
- **AND** `git diff` 显示出 layout 实际变化（commit 时一并 review）

### Requirement: Writer 确定性

#### Scenario: 同输入字节级一致

- **WHEN** 给 ZbcWriter 同一个 IR module 跑两遍
- **THEN** 两次输出字节级完全一致

### Requirement: Disasm round-trip

#### Scenario: 字节级 round-trip

- **WHEN** 给 3 个代表性 zbc 跑 `z42c disasm <zbc>` → `z42c assemble <zasm>`
- **THEN** 输出 zbc 与原 zbc 字节级相等
- **AND** 测试覆盖：simple-class / with-debug / cross-import 三种 layout

### Requirement: workflow.md `.zbc` minor bump 流程

#### Scenario: 文档化的 bump 程序

- **WHEN** 开发者准备 bump minor
- **THEN** `.claude/rules/workflow.md` 包含 "Bumping `.zbc` minor version" 子节
- **AND** 子节列出必须同步的四处：`ZbcWriter.VersionMinor` / `zbc_reader.rs` 常量 / `zbc.md` changelog / `generate-fixtures.sh` regen

## MODIFIED Requirements

### Requirement: ZbcReader 版本检查（C# + Rust）

**Before**（C# `ZbcReader.cs:35-37`）：

```csharp
if (major == 1 && minor < 5)
    throw new InvalidDataException(
        $"zbc {major}.{minor} not supported; requires 1.5+." +
        $" regen via ./scripts/regen-golden-tests.sh");
```

**After**（精确匹配 + reference writer const）：

```csharp
if (major != ZbcWriter.VersionMajor)
    throw new InvalidDataException(
        $"zbc major {major} not supported (expected {ZbcWriter.VersionMajor})");
if (minor != ZbcWriter.VersionMinor)
    throw new InvalidDataException(
        $"zbc minor {minor} not supported (writer is at {ZbcWriter.VersionMinor}); " +
        $"regen via ./scripts/regen-golden-tests.sh");
```

Rust 端对应：`zbc_reader.rs` 加常量 + 精确匹配 + 同等错误信息。

### Requirement: zbc.md 版本兼容章节

**Before**（zbc.md L426-433）：

```
- `version_major` 变化 → 破坏性变更，VM 必须拒绝加载
- `version_minor` 变化 → 新增操作码，旧 VM 遇到未知 opcode 报 `UnsupportedOpcode` 错误
- 当前版本：major=1, minor=0
- pre-1.0 (0.x) 不支持
```

**After**：

```
- **strict-pin 政策**：reader 仅接受 `major == WRITER_MAJOR && minor == WRITER_MINOR`；
  pre-1.0 z42 不为旧 zbc minor 提供向前/向后兼容。每次 minor bump 必须 regen
  所有 zbc artifacts（`./scripts/regen-golden-tests.sh`）。
- **当前版本**：`major=1, minor=5`（2026-05-13 fix-numeric-cast-lowering）
- **触发 minor bump**：新 opcode / 新 section / 已定义 section 内部字段语义变化
- **触发 major bump**：改 magic / 改 16B header layout / 改 section directory 12B 条目格式 /
  Token 空间重划。v2 出现前所有 wire format 变化都走 minor bump。
- **未识别 section**：reader 通过 dict-lookup 自动跳过（不在已知 tag 集合内的 section 不影响其他 section 加载）。这是 v1 内"加 section 不破坏旧 reader"的唯一兼容点；但前提是新 section 不携带必须信息（必须信息出现时 minor bump 本身就让旧 reader bail）。
- **changelog**：见 zbc.md 文末 minor 变更表。
```

## Pipeline Steps

本 spec 不引入新 IR / VM / Codegen / TypeCheck 行为；仅触：
- 文档（`docs/design/runtime/zbc.md` + `.claude/rules/workflow.md` + `docs/roadmap.md`）
- ZbcReader 版本检查精确化（C# + Rust）
- 测试基础设施（fixture + harness + invariant）

不需要 lexer / parser / type-checker / codegen / VM interp 改动。
