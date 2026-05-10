# Tasks: Split Debug Symbols to Sidecar (.zsym)

> 状态：🟡 进行中 | 创建：2026-05-10
> 类型：vm + ir（完整流程）
> 前置：✅ unify-frame-chain (`fd5deb2`) 已归档，VmFrame 单一栈已就绪

## 进度概览

- [ ] 阶段 1: zbc 二进制层（C# 写/读 + BLID）
- [ ] 阶段 2: Driver 接线 + symbolicate 子命令
- [ ] 阶段 3: Rust runtime 加载 + 合并
- [ ] 阶段 4: Rust trace 退化 + 函数签名
- [ ] 阶段 5: Golden test 端到端
- [ ] 阶段 6: 文档同步 + 全绿验证

## 阶段 1: zbc 二进制层（C#）

### 1.1 常量与枚举

- [x] 1.1.1 [src/compiler/z42.IR/BinaryFormat/Opcodes.cs](../../../src/compiler/z42.IR/BinaryFormat/Opcodes.cs) — 增 `ZbcFlags.SymOnly = 0x04`
- [x] 1.1.2 同上 — 增 `SectionTags.Blid = "BLID"u8.ToArray()`
- [x] 1.1.3 [src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs](../../../src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs) — bump `VersionMinor` 1 → 2，注释记录原因

### 1.2 BuildId 工具

- [x] 1.2.1 [src/compiler/z42.IR/BinaryFormat/BuildId.cs](../../../src/compiler/z42.IR/BinaryFormat/BuildId.cs) NEW — `Compute(ReadOnlySpan<byte> zbcWithBlidZeroed) → byte[16]` 调 BLAKE3-128
- [x] 1.2.2 引入 `Blake3`（xoofx，2.2.1）NuGet 包到 z42.IR.csproj
- [x] 1.2.3 同步到 `.claude/libraries.md` 推荐列表

### 1.3 ZbcWriter 拆分（含 1.2 wire format 重组）

- [x] 1.3.0 [src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs](../../../src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs) `BuildFuncSection` — 移除 LineTable 写入（u16 lineCount + LineEntry[]），FUNC body 仅余 reg/blocks/instr/exc
- [x] 1.3.0a 同上 `BuildDbugSection` — 重写：每函数 `u16 lineCount + LineEntry[] + u16 varCount + VarEntry[]`
- [x] 1.3.0b 同上 — DBUG 触发条件统一：模块任何函数 LineTable/LocalVarTable 非空即写
- [x] 1.3.0c 同上 — Stripped 模式（.cache/*.zbc）也写 DBUG（含 LineTable，保持 dev workflow trace）
- [x] 1.3.1 同上 — 新 API `WriteWithSidecar(IrModule, bool stripSymbols, ZbcFlags, exports, allocator) → (byte[] main, byte[]? sidecar)`
- [x] 1.3.2 同上 — 主流程：strip=true 时 main 不写 DBUG；构建 BLID section（占位 16 个 0）追加为最后 section；`AssembleFile` 后计算 BLAKE3-128 并 `Seek(end-16)` 回填
- [x] 1.3.3 同上 — sidecar 流程：构造 NSPC + STRS（debug 子集）+ DBUG + BLID，header flags 设 `SymOnly`；BLID 字节与 main 一致（直接复用，无需重算）
- [x] 1.3.4 同上 — strip=false 时维持新格式（DBUG 内嵌、no BLID、no sidecar）
- [x] 1.3.5 同上 — 旧 `Write(...)` 保留为 thin wrapper（`WriteWithSidecar(..., stripSymbols: false).main`），调用方平滑过渡

### 1.3.zpkg ZpkgWriter / ZpkgReader（packed 模式 feature parity）

- [x] 1.3.zpkg.1 [src/compiler/z42.Project/ZpkgWriter.Sections.cs](../../../src/compiler/z42.Project/ZpkgWriter.Sections.cs) — 每 member 增 DBUG body 字段（u32 size + bytes，size=0 表示空）
- [x] 1.3.zpkg.2 [src/compiler/z42.Project/ZpkgReader.Sections.cs](../../../src/compiler/z42.Project/ZpkgReader.Sections.cs) — 读 DBUG body 并应用到 functions（mirror writer）
- [x] 1.3.zpkg.3 [src/compiler/z42.Project/ZpkgReader.cs](../../../src/compiler/z42.Project/ZpkgReader.cs) `ReadSourceHashes` — skip 新增的 DBUG body（增量编译命中需要）

## 阶段 1.5b: zpkg-level sidecar wire format（packer 集成；2026-05-10 scope 扩张）

- [x] 1.5b.1 [src/compiler/z42.Project/ZpkgWriter.cs](../../../src/compiler/z42.Project/ZpkgWriter.cs) — bump zpkg 0.2 → 0.3；新增 `FlagSymOnly = 0x04`、`ZpkgTags.Mdbg`、`ZpkgTags.Blid`
- [x] 1.5b.2 同上 — 新 API `WritePackedWithSidecar(zpkg, stripSymbols) → (main, sidecar?)`
- [x] 1.5b.3 [src/compiler/z42.Project/ZpkgWriter.Sections.cs](../../../src/compiler/z42.Project/ZpkgWriter.Sections.cs) — `BuildMdbgSection`；`BuildModsSection` 接受 `bool stripSymbols`
- [x] 1.5b.4 [src/compiler/z42.Project/ZpkgReader.cs](../../../src/compiler/z42.Project/ZpkgReader.cs) — `ReadSidecar` / `ApplyDebugInfo`（zpkg 级）+ `ReadBuildId` + SymOnly 拒绝
- [x] 1.5b.5 同上 — `ReadMdbgSection`；版本 < 0.3 reject
- [x] 1.5b.5b [src/runtime/src/metadata/zbc_reader.rs](../../../src/runtime/src/metadata/zbc_reader.rs) — read_zpkg_modules 版本 < 0.3 reject + SymOnly flag reject
- [x] 1.5b.6 [src/compiler/z42.Project/ZpkgBuilder.cs](../../../src/compiler/z42.Project/ZpkgBuilder.cs) — 新 `WriteZpkgWithSidecar` 返回 (zpkgPath, sidecarPath?)；driver pipeline 落盘 `<outDir>/<name>.zsym`
- [x] 1.5b.7 [src/compiler/z42.Tests/ZpkgSidecarSymbolsTests.cs](../../../src/compiler/z42.Tests/ZpkgSidecarSymbolsTests.cs) NEW — 9 个 case 全绿（round-trip + BLID pairing + 不匹配 reject + SymOnly 误用拒绝）

### 1.4 ZbcReader 识别（含 1.2 重组）

- [x] 1.4.0 [src/compiler/z42.IR/BinaryFormat/ZbcReader.cs](../../../src/compiler/z42.IR/BinaryFormat/ZbcReader.cs) `ReadFuncSection` — 移除 LineTable 读取
- [x] 1.4.0a 同上 `ReadDbugSection` — 解析新格式（LineTable + LocalVarTable per function）→ 注入 IrFunction
- [x] 1.4.1 同上 — 解析 BLID section（16B → IrModule.BuildId 字段）
- [x] 1.4.2 同上 — 识别 SymOnly flag；新 API `ReadSidecar(byte[]) → SidecarData`
- [x] 1.4.3 同上 — 新 API `ApplyDebugInfo(IrModule mainModule, SidecarData sidecar)` — 校验 BuildId + 合并 LineTable / LocalVarTable 进 module.Functions[]
- [x] 1.4.4 同上 — version mismatch（< 1.2 zbc）→ 抛错（不留兼容路径）

### 1.5 z42.IR 单测

- [x] 1.5.1 [src/compiler/z42.Tests/SidecarSymbolsTests.cs](../../../src/compiler/z42.Tests/SidecarSymbolsTests.cs) NEW（10 个 case 全绿）
- [x] 1.5.2 case：`WriteWithSidecar(strip=true)` LineTable 非空 → main 不含 DBUG / sidecar 仅 DBUG+BLID
- [x] 1.5.3 case：`WriteWithSidecar(strip=true)` 全部 LineTable 空 → 仍产 sidecar，BLID 一致
- [x] 1.5.4 case：`WriteWithSidecar(strip=false)` → DBUG 内嵌、no BLID、sidecar=null
- [x] 1.5.5 case：BLID 稳定性 — 同输入两次哈希相同
- [x] 1.5.6 case：BLID 敏感性 — 改一字节哈希变
- [x] 1.5.7 case：`ApplyDebugInfo` 匹配 BLID 后 LineTable 注入到 module
- [x] 1.5.8 case：`ApplyDebugInfo` BLID 不匹配 → 抛 `InvalidOperationException`
- [x] 1.5.9 case：把 sidecar 当主模块 read → 拒绝
- [x] 1.5.10 case：`ReadSidecar` 拒绝普通 zbc
- [x] 1.5.11 case：`BuildId.ShortHex` 格式正确

## 阶段 2: Driver 接线 + symbolicate

### 2.1 Effective strip 解析

- [x] 2.1.1 [src/compiler/z42.Driver/BuildCommand.cs](../../../src/compiler/z42.Driver/BuildCommand.cs) — 解析 `--strip-symbols=true|false` CLI flag (Option<bool?>)
- [x] 2.1.2 [src/compiler/z42.Pipeline/PackageCompiler.cs](../../../src/compiler/z42.Pipeline/PackageCompiler.cs) `Run` — effective strip 优先级：CLI > `manifest.SelectProfile.Strip` > 内置默认（debug=false / release=true）
- [x] 2.1.3 同上 `BuildTarget` — 接受 `bool stripSymbols`，传入 `ZpkgBuilder.WriteZpkgWithSidecar`
- [x] 2.1.4 同上 — sidecar 字节非 null 时落盘 `<outDir>/<name>.zsym`（end-to-end 验证 stdlib 6 包均产 .zsym）
- [x] 2.1.5 [src/compiler/z42.Pipeline/WorkspaceBuildOrchestrator.cs](../../../src/compiler/z42.Pipeline/WorkspaceBuildOrchestrator.cs) — `BuildOptions.StripSymbols` 字段，workspace effective strip = CLI ?? release default

### 2.2 Driver 单测（暂用 e2e 验证；定向单测留作 follow-up）

- [x] 2.2.1 e2e 验证：`./scripts/build-stdlib.sh` 触发 `--release`，6 个 stdlib 包均产 `.zsym`

### 2.3 z42c symbolicate 子命令

- [ ] 2.3.1 [src/compiler/z42.Driver/SymbolicateCommand.cs](../../../src/compiler/z42.Driver/SymbolicateCommand.cs) NEW — `Run(string crashFile, string symsPath, string? outFile)`
- [ ] 2.3.2 同上 — 加载 sidecar via `ZbcReader.ReadSidecar`
- [ ] 2.3.3 同上 — 行正则：`^\s*at (.+?\(.*?\))\+0x([0-9a-f]+)( \[build:([0-9a-f]{8})\])?$`
- [ ] 2.3.4 同上 — 对每行：验证 build_id（不匹配 → 错误退出）；按 ip 查 LineTable；重写为 `at FQN(sig) (file:line:col)`
- [ ] 2.3.5 同上 — 无 [build:...] 段：best-effort 解析 + stderr warn
- [ ] 2.3.6 [Program.cs](../../../src/compiler/z42.Driver/Program.cs) 添加 subcommand 路由 `symbolicate`

### 2.4 Symbolicate 单测

- [ ] 2.4.1 [src/compiler/z42.Tests/SymbolicateCommandTests.cs](../../../src/compiler/z42.Tests/SymbolicateCommandTests.cs) NEW
- [ ] 2.4.2 case：完整 trace（含签名 + build_id）正确还原
- [ ] 2.4.3 case：build_id 不匹配 → 退出码非 0 + stderr 格式
- [ ] 2.4.4 case：旧 trace（无 [build:...]）→ best-effort + warning
- [ ] 2.4.5 case：trace 中混合 ip-form 与非 ip-form 行 → 仅匹配行被改写

## 阶段 3: Rust runtime 加载

### 3.1 BuildId

- [x] 3.1.1 [src/runtime/src/metadata/build_id.rs](../../../../src/runtime/src/metadata/build_id.rs) NEW — `compute` + `short_hex` + 5 单测
- [x] 3.1.2 引入 `blake3` crate 到 runtime Cargo.toml
- [x] 3.1.3 [src/runtime/src/metadata/mod.rs](../../../../src/runtime/src/metadata/mod.rs) — `pub mod build_id;`

### 3.2 zbc_reader 解析（含 1.2 重组）

- [x] 3.2.0 [src/runtime/src/metadata/zbc_reader.rs](../../../src/runtime/src/metadata/zbc_reader.rs) — FUNC 解析移除 LineTable 字段读取（lineCount + LineEntry[]）
- [x] 3.2.0a 同上 — DBUG 解析新格式：每函数 LineTable + LocalVarTable，注入 Function.line_table / local_vars
- [x] 3.2.0b 同上 — read_mods_section 也解析 per-member DBUG body（packed zpkg feature parity）
- [x] 3.2.1 同上 — `read_build_id(&[u8]) -> Option<[u8;16]>` 公开 API
- [x] 3.2.2 同上 — SymOnly flag bit reject；version < 1.2 zbc → `bail!`
- [x] 3.2.3 同上 — 新 API `parse_zbc_sidecar` / `parse_zpkg_sidecar`（覆盖两种 sidecar 形态）

### 3.3 Loader 集成

- [x] 3.3.1 [src/runtime/src/metadata/loader.rs](../../../../src/runtime/src/metadata/loader.rs) — SymOnly 文件作为主 zbc 加载时 bail（zbc_reader read_zbc 内部已实现）
- [x] 3.3.2 同上 — `load_zbc` / `load_zpkg` 探测 `path.with_extension("zsym")`
- [x] 3.3.3 同上 — `apply_zbc_sidecar` / `apply_zpkg_sidecar`：BLID 匹配则合并 line_table / local_vars
- [x] 3.3.4 同上 — 不匹配 / 损坏 / SymOnly flag 错 → `tracing::warn!` + 忽略，加载流程不失败
- [x] 3.3.5 `Module` 直接 mutate（不需 RefCell）— sidecar merge 在 read_zbc / read_mods_section 返回的可变 Module 上发生
- [ ] 3.3.6 [docs/design/runtime/vm-architecture.md] — 记录"sidecar 加载策略"决策原理（阶段 6 文档同步时落地）

### 3.4 Rust 加载单测

- [x] 3.4.1 [src/runtime/src/metadata/sidecar_tests.rs](../../../../src/runtime/src/metadata/sidecar_tests.rs) NEW — 9 case 全绿（覆盖 zbc/zpkg sidecar 拒绝路径：bad magic、old minor、SymOnly flag 缺失、missing BLID、read_build_id 边界）
- [x] 3.4.2-3.4.7 round-trip 验证暂用 stdlib e2e + golden tests 隐式覆盖（6 个 stdlib 包加载 sidecar 后 312 VM tests 全绿）；专项 round-trip 测试可在阶段 5 golden 中加针对性 case

## 阶段 4: Rust trace 退化 + 函数签名

### 4.0 Wire format Phase 4：SIGS 携带 per-param 类型名（zbc 1.2 → 1.3, zpkg 0.3 → 0.4）

- [x] 4.0.1 [src/compiler/z42.IR/IrModule.cs](../../../../src/compiler/z42.IR/IrModule.cs) — `IrFunction.ParamTypes: List<string>?` 字段
- [x] 4.0.2 [src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs](../../../../src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs) `BuildSigsSection` — 写 u32 strIdx × paramCount；intern "?" 占位
- [x] 4.0.3 同上 — bump version 1.2 → 1.3；`WriteConstraintBundle` 改 public（zpkg 共享）
- [x] 4.0.4 [src/compiler/z42.IR/BinaryFormat/ZbcReader.cs](../../../../src/compiler/z42.IR/BinaryFormat/ZbcReader.cs) `ReadSigsSection` + 主入口 — 读 ParamTypes 并注入 IrFunction
- [x] 4.0.5 同上 — version < 1.3 reject
- [x] 4.0.6 [src/compiler/z42.Project/ZpkgWriter.Sections.cs](../../../../src/compiler/z42.Project/ZpkgWriter.Sections.cs) — zpkg `BuildSigsSection` 同步写 ParamTypes + **修复 pre-existing bug**（写 constraint bundle 与 reader 对齐）
- [x] 4.0.7 [src/compiler/z42.Project/ZpkgReader.Sections.cs](../../../../src/compiler/z42.Project/ZpkgReader.Sections.cs) — zpkg-internal SIGS reader 跳过 ParamTypes 字段
- [x] 4.0.8 [src/compiler/z42.Project/ZpkgWriter.cs](../../../../src/compiler/z42.Project/ZpkgWriter.cs) — bump zpkg 0.3 → 0.4
- [x] 4.0.9 [src/compiler/z42.Project/ZpkgReader.cs](../../../../src/compiler/z42.Project/ZpkgReader.cs) — version < 0.4 reject
- [x] 4.0.10 [src/runtime/src/metadata/zbc_reader.rs](../../../../src/runtime/src/metadata/zbc_reader.rs) `read_sigs` — 读 ParamTypes 进 FuncSig；版本 1.2 → 1.3、zpkg 0.3 → 0.4 reject + sidecar 同步
- [x] 4.0.11 [src/runtime/src/metadata/bytecode.rs](../../../../src/runtime/src/metadata/bytecode.rs) — `Function.param_types: Vec<String>` 字段
- [x] 4.0.12 [src/compiler/z42.Semantics/Codegen/FunctionEmitter.cs](../../../../src/compiler/z42.Semantics/Codegen/FunctionEmitter.cs) `EmitMethod` / `EmitFunction` — 填充 ParamTypes（含 `this` 类名前缀）

### 4.1 Frame.func_name 携带签名

- [ ] 4.1.1 在 metadata 层提供 `signature_string(func_idx) -> String`（合成 `(t1,t2,...)`）
  - 简化类型映射：i32/i64/f32/f64/bool/str/void → 同字面；类 → 裸类名；数组 → `T[]`；空参 → 空字符串
- [ ] 4.1.2 [src/runtime/src/interp/mod.rs](../../../src/runtime/src/interp/mod.rs) `exec_function` push 站点 — 把 `<FQN>` 改为 `<FQN>(<sig>)` 传给 `VmFrame::new`
- [ ] 4.1.3 [src/runtime/src/jit/mod.rs](../../../src/runtime/src/jit/mod.rs) `JitModule::run_fn` 同上
- [ ] 4.1.4 [src/runtime/src/jit/helpers/call.rs](../../../src/runtime/src/jit/helpers/call.rs) `jit_call` 同上
- [ ] 4.1.5 [src/runtime/src/jit/helpers/closure.rs](../../../src/runtime/src/jit/helpers/closure.rs) `jit_call_indirect` 同上
- [ ] 4.1.6 [src/runtime/src/jit/helpers/vcall.rs](../../../src/runtime/src/jit/helpers/vcall.rs) `jit_vcall`（4 个 invoke 路径）同上
- [ ] 4.1.7 [src/runtime/src/jit/helpers/object.rs](../../../src/runtime/src/jit/helpers/object.rs) `jit_obj_new` ctor 同上
- [ ] 4.1.8 [src/runtime/src/jit/helpers/value.rs](../../../src/runtime/src/jit/helpers/value.rs) `jit_to_str` 同上
- [ ] 4.1.9 既有 stack-trace e2e golden 重生（`./scripts/regen-golden-tests.sh`），核对 trace 现在带签名

### 4.2 退化 trace 格式

- [ ] 4.2.1 [src/runtime/src/exception/mod.rs](../../../src/runtime/src/exception/mod.rs) `format_stack_trace` — line==0 分支输出 `at <func_name>+0x<ip:hex>[ [build:<8hex>]]`
- [ ] 4.2.2 同上 — `FrameSnapshot` 增 `module_blid: Option<[u8; 16]>` + `ip: u32` 字段（throw 时填充）
- [ ] 4.2.3 [src/runtime/src/exception/mod.rs](../../../src/runtime/src/exception/mod.rs) — 异常抛出路径在 snapshot 时填 ip + module_blid
- [ ] 4.2.4 [src/runtime/src/exception/tests.rs](../../../src/runtime/src/exception/tests.rs) — 加 case：strip 模式 + 无 sidecar → trace 退化格式正确
- [ ] 4.2.5 同上 — 加 case：模块无 BLID → 省略 `[build:...]`

## 阶段 5: Golden test 端到端

### 5.1 sidecar_symbols golden

- [ ] 5.1.1 [src/tests/exception/sidecar_symbols/source.z42](../../../src/tests/exception/sidecar_symbols/source.z42) NEW — 抛异常的小程序（含至少 2 层调用栈）
- [ ] 5.1.2 [src/tests/exception/sidecar_symbols/expected_output.txt](../../../src/tests/exception/sidecar_symbols/expected_output.txt) NEW — strip + 加载 sidecar 时的 trace（FQN(sig) + file:line:col）
- [ ] 5.1.3 [src/tests/exception/sidecar_symbols/runner.toml](../../../src/tests/exception/sidecar_symbols/runner.toml) NEW — 配置 strip=true
- [ ] 5.1.4 second variant：`sidecar_symbols_no_zsym/` — 同 source 但 runner 删除 sidecar，expected 是退化格式
- [ ] 5.1.5 `./scripts/test-vm.sh` interp + jit 双 mode 通过

## 阶段 6: 文档同步 + 全绿验证

### 6.1 设计文档

- [ ] 6.1.1 [docs/design/runtime/zbc.md](../../../docs/design/runtime/zbc.md) — 修正 META section 描述（仅模块名/版本/entry，移除"调试信息"误述）
- [ ] 6.1.2 同上 — 增 DBUG section 完整描述（line table + local var names）
- [ ] 6.1.3 同上 — 增 BLID section + ZbcFlags.SymOnly + sidecar 形态章节
- [ ] 6.1.4 同上 — 版本历史段标记 1.1 → 1.2，列变更（BLID + SymOnly + DBUG 可外迁）
- [ ] 6.1.5 [docs/design/language/exceptions.md](../../../docs/design/language/exceptions.md) — 新增"Sidecar 符号化"章节（产物形态 + 加载流程 + 退化格式）
- [ ] 6.1.6 同上 — 增 Deferred 段：lazy/mmap、跨目录搜索、packer 集成、stdlib API、bundle、压缩 wire format
- [ ] 6.1.7 [docs/design/compiler/project.md](../../../docs/design/compiler/project.md) — `[profile.*].strip` 字段语义（之前仅占位、本期生效）
- [ ] 6.1.8 同上 — release pipeline 产物表加 `<name>.zsym`

### 6.2 README 同步

- [ ] 6.2.1 [src/compiler/z42.IR/BinaryFormat/README.md](../../../src/compiler/z42.IR/BinaryFormat/README.md) — 增 sidecar 拆分能力 + `WriteWithSidecar`/`ReadSidecar`/`ApplyDebugInfo` 入口
- [ ] 6.2.2 [src/runtime/src/metadata/README.md](../../../src/runtime/src/metadata/README.md) — 增 sidecar 加载流程 + build_id.rs 角色

### 6.3 Roadmap

- [ ] 6.3.1 [docs/roadmap.md](../../../docs/roadmap.md) — 在 M6 / M7 相关进度表标记 split-debug-symbols 完成项

### 6.4 全绿验证

- [ ] 6.4.1 `dotnet build src/compiler/z42.slnx` — 无编译错误
- [ ] 6.4.2 `cargo build --manifest-path src/runtime/Cargo.toml` — 无编译错误
- [ ] 6.4.3 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` — 100% 通过
- [ ] 6.4.4 `./scripts/test-vm.sh` — interp + jit 双 mode 100% 通过
- [ ] 6.4.5 spec scenarios 逐条覆盖核对（生成 GREEN 报告映射 spec → 测试位置）

## 备注

- **不为旧版本提供兼容**：1.1 zbc 直接 reject，残留 golden 用 `regen-golden-tests.sh` 重生
- **packer 集成留后续 spec**：本期 sidecar 仅在 indexed 模式 `<outDir>/<name>.zbc` + `<name>.zsym` 旁，packed `<name>.zpkg` 不动
- **`[profile.*].strip` 字段**已存在但未消费（[ProjectManifest.cs:246](../../../src/compiler/z42.Project/ProjectManifest.cs#L246)），本期接到 driver
