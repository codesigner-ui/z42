# Proposal: Split Debug Symbols to Sidecar (.zsym)

## Why

发布形态的 `.zpkg`（packed mode）当前内嵌 DBUG section（line/column 表 + 局部变量名），按典型工程估计占模块字节量 20–40%。生产环境一般不需要这些信息，但 crash 时栈帧只有 `Module.Func` 又难定位源文件——目前没有"既小又能事后符号化"的中间路径。

借鉴 .NET PDB / dSYM / split-debug 的 sidecar 模式：release 构建剥离 DBUG，配套产 `<name>.zsym`；runtime 自动探测加载，离线时也可用 `z42c symbolicate` 补全 trace。

**附带修复**：[docs/design/zbc.md L260](../../../docs/design/zbc.md#L260) 把 META 描述为"调试信息"，但代码实际把 META 用于模块名/版本/entry，DBUG 才是调试信息。规范文档与代码漂移，借本变更归位。

## What Changes

### 核心新增

- **`.zsym` sidecar 格式**：复用 zbc 容器（magic `ZBC\0`、版本 1.2），新 flag `ZbcFlags.SymOnly = 0x04`
  - 仅 2 个 section：`DBUG`（按现 wire format 不变）+ `BLID`（NEW，16 字节）
  - 不含 FUNC / TYPE / SIGS，靠 build_id 与主 zbc 配对
- **`BLID` section**（NEW，16 字节 BLAKE3-128 哈希）：同步写入主 `.zbc` 和 `.zsym`；runtime 加载时校验
- **runtime sidecar 自动加载**（eager/同步）：加载 `<name>.zbc` 后探测同目录 `<name>.zsym`，build_id 匹配则把 line table 合入 FuncBody
- **`z42c symbolicate <crash.txt> --syms <path>`**：离线符号化工具
- **toml `[profile.*].strip` 接线**：字段已存在（debug 默认 false / release 默认 true，见 [ProjectManifest.cs:246](../../../src/compiler/z42.Project/ProjectManifest.cs#L246)、[L328-329](../../../src/compiler/z42.Project/ProjectManifest.cs#L328-L329)），但当前只解析未消费。本期接到 driver → ZbcWriter 拆分逻辑

### 修改

- `ZbcWriter`：当 `strip=true` 时不写 DBUG（即使 LineTable 非空），改写到 sidecar；`strip=false` 时维持现行（DBUG 内嵌、不产 sidecar）
- `ZbcReader` 识别 SymOnly 文件 + BLID section
- `format_stack_trace`：line==0 且无 sidecar 时退化为 `at Module.Func(sig)+0x<ip> [build:<id>]`（含函数签名，仍可离线符号化）
- driver 读取 effective `strip` 值（profile/CLI 优先）传给 writer；新增 CLI override `--strip-symbols=true|false`
- 函数名带签名 / TYPE / SIGS 全部保留在 strip 后的 zbc（per User 决策：未加载符号也能看到函数全称）

### 修复（顺手做，避免规范漂移）

- `docs/design/zbc.md` L260 section 列表与 META section 描述：META 实际承载 module name/version/entry，DBUG 才是调试信息。文档与代码统一

### 不修改

- `.zpkg` 容器格式本身（zsym 在 zpkg 外平级，1 zpkg : 1 zsym）
- stripped 模式 `.cache/*.zbc`（开发态 cache，与 release strip 正交）
- JIT/AOT 代码路径（trace 来自 IR ip，符号化对 JIT 透明）
- 不为 minor version 旧 zbc 提供 fallback（pre-1.0 不留兼容）

## Scope（允许改动的文件）

| 文件 | 变更 | 说明 |
|------|------|------|
| `src/compiler/z42.IR/BinaryFormat/Opcodes.cs` | MODIFY | `ZbcFlags.SymOnly`、`SectionTags.Blid` |
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs` | MODIFY | 拆分 sidecar；`WriteWithSidecar`；版本 → 1.2 |
| `src/compiler/z42.IR/BinaryFormat/ZbcReader.cs` | MODIFY | 识别 SymOnly + BLID；`ReadSidecar`/`ApplyDebugInfo` |
| `src/compiler/z42.IR/BinaryFormat/BuildId.cs` | NEW | BLAKE3-128 计算（整流置零） |
| `src/compiler/z42.Driver/Program.cs` | MODIFY | 读 effective `strip`；`--strip-symbols=true|false` CLI override |
| `src/compiler/z42.Driver/SymbolicateCommand.cs` | NEW | `z42c symbolicate` 子命令 |
| `src/compiler/z42.Pipeline/CompilationPipeline.cs` | MODIFY | 把 effective `strip` 传给 ZbcWriter |
| `src/compiler/z42.Project/ZpkgWriter.Sections.cs` | MODIFY | packed zpkg 每 member 增 DBUG body；新增 BuildMdbgSection（sidecar 用） |
| `src/compiler/z42.Project/ZpkgReader.Sections.cs` | MODIFY | 读 DBUG body 并应用到 functions；读 MDBG section |
| `src/compiler/z42.Project/ZpkgWriter.cs` | MODIFY | bump zpkg 0.2 → 0.3；`FlagSymOnly`；`WritePackedWithSidecar` |
| `src/compiler/z42.Project/ZpkgReader.cs` | MODIFY | `ReadSidecar` / `ApplyDebugInfo`（zpkg 级）；版本检查 |
| `src/compiler/z42.Project/ZpkgBuilder.cs` | MODIFY | 接受 `stripSymbols`；落盘 `<name>.zsym` |
| `src/compiler/z42.Tests/ZpkgSidecarSymbolsTests.cs` | NEW | zpkg 级 round-trip + build_id pairing |
| `src/runtime/src/metadata/zbc_reader.rs` | MODIFY | 解析 SymOnly + BLID |
| `src/runtime/src/metadata/loader.rs` | MODIFY | sidecar 探测（同步） + build_id 校验 + 合并 line_table |
| `src/runtime/src/metadata/build_id.rs` | NEW | BLAKE3-128 计算（整流置零） |
| `src/runtime/src/exception/mod.rs` | MODIFY | line==0 退化为 `Mod.Func(sig)+0x<ip> [build:<id>]` |
| `src/runtime/src/interp/mod.rs` | MODIFY | frame.func_name 携带函数签名 |
| `src/runtime/src/jit/mod.rs` | MODIFY | 同上 |
| `src/compiler/z42.Tests/SidecarSymbolsTests.cs` | NEW | 写/读/build_id 配对/不匹配 reject |
| `src/compiler/z42.Tests/SymbolicateCommandTests.cs` | NEW | symbolicate 工具单测 |
| `src/compiler/z42.Tests/ProjectStripWiringTests.cs` | NEW | toml strip → effective writer flag |
| `src/runtime/src/metadata/sidecar_tests.rs` | NEW | runtime 加载 + 合并单测 |
| `src/tests/exception/sidecar_symbols/` | NEW | golden test：剥离 + 加载 + trace 一致 |
| `docs/design/zbc.md` | MODIFY | 修正 META/DBUG；增 SymOnly + BLID 章节；版本 1.2 |
| `docs/design/exceptions.md` | MODIFY | 新增 sidecar 符号化 + 退化 trace 格式章节 |
| `docs/design/project.md` | MODIFY | `[profile.*].strip` 字段语义 + `<name>.zsym` 产物说明 |
| `src/compiler/z42.IR/BinaryFormat/README.md` | MODIFY | 增 sidecar 描述 |
| `src/runtime/src/metadata/README.md` | MODIFY | sidecar 加载流程 |

**只读引用**（理解必须读，不修改）：
- `src/compiler/z42.IR/IrModule.cs` — 理解 LineTable / LocalVarTable 形状
- `src/compiler/z42.Project/ProjectManifest.cs` — `ProfileSection.Strip` 字段已存在（解析侧不需改）
- `docs/spec/archive/2026-05-10-unify-frame-chain/` — 已归档，VmFrame 单一栈布局已就绪
- `docs/roadmap.md` — 阶段归属

**估计**：20 MODIFY + 8 NEW = 28 文件。

## Out of Scope

- 函数名剥离（User 决策：保留）
- stripped 模式 `.cache/*.zbc` 改造（dev-workflow cache 与 release strip 正交）
- multi-module / multi-zsym bundle（User 决策：1 zpkg : 1 zsym 单文件）
- JIT-emitted machine code 的 debug map（与 IR ip 无关）
- runtime sidecar 跨目录搜索 / 环境变量配置（本期只探测同目录）
- DBUG wire format 重新设计 / 紧凑化
- 把符号化能力暴露为公开 stdlib API（独立 spec）
- BLAKE3 之外的哈希算法对比（已选定）

> **Scope 扩张（实施期决定，2026-05-10）**：原 Out of Scope 的"packer 集成"已并入本期。User 决策：sidecar 命名 `<name>.zsym`，与 packed `<name>.zpkg` 平级。zpkg 容器扩展 SymOnly flag + MDBG section + BLID section（zpkg 0.2 → 0.3）；indexed 模式同样用 `<name>.zsym` 收集所有 `.cache/*.zbc` 的 DBUG。

## Open Questions

无。设计决策：
- Build_id：BLAKE3-128（User 确认）
- Sidecar 路径：`<name>.zsym`（User 确认）
- 加载策略：eager / 同步加载（User 确认）
- BLID 计算：整流置零哈希（User 确认）
- BLID section tag：4 ASCII = `BLID`（User 确认更易读）
- 拆分逻辑统一（不区分 release/debug 代码路径）；行为由 `[profile.*].strip` 控制，默认 debug=false / release=true（User 确认）
- 退化 trace 格式带函数签名（`Mod.Func(t1,t2)+0x<ip> [build:<id>]`）（User 确认）
- 与 unify-frame-chain：已归档（`fd5deb2`，VmFrame 单一栈已就绪），本变更可直接单阶段实施
