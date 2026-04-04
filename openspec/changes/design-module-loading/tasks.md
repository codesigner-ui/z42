# Tasks: Module Loading & Dependency Resolution

> 状态：🟡 进行中 | 创建：2026-04-04

## 进度概览

- [x] 阶段 1: zbc 格式重设计（full/stripped 双模式）— 1c.1 + 1c.4 留后续阶段
- [x] 阶段 2: zpkg manifest 扩展（格式层）
- [ ] 阶段 3: VM module path（Z42_PATH）
- [ ] 阶段 4: 编译器命名空间解析
- [x] 阶段 5: 文档更新（5.1 + 5.4 完成，5.2 + 5.3 待格式层实现后补）
- [ ] 阶段 6: 测试与验证

---

## 阶段 1: zbc 格式重设计（full / stripped 双模式）

### 1a: Header + flags + section 框架

- [x] 1a.1 `src/compiler/z42.IR/BinaryFormat/Opcodes.cs` — 新 header v0.2：`magic[4]+version[2+2]+flags[2]+reserved[6]`；`ZbcFlags` 枚举（`STRIPPED=0x01, HAS_DEBUG=0x02`）
- [x] 1a.2 section 通用框架：`section_id[4] + length[4] + data[N]`（顺序写入，无 directory）；ZbcWriter/ZbcReader 完整重写
- [x] 1a.3 `NSPC` section 为第一个 section；`ZbcReader.ReadNamespace()` 只读 header+第一 section
- [x] 1a.4 `src/runtime/src/metadata/formats.rs` — 新增 section tag 常量、flags 常量、`read_zbc_namespace()`、`zbc_is_stripped()`

### 1b: Full mode sections

- [x] 1b.1 `STRS` section（string heap，本文件字符串去重）
- [x] 1b.2 `EXPT` section（export table）
- [x] 1b.3 `SIGS` section（function name + param_count + ret_type + exec_mode）
- [x] 1b.4 `IMPT` section（外部调用目标符号名列表）
- [x] 1b.5 `FUNC` section（function bodies：reg_count + block_count + instr_len + exc_count + block_offsets + exc_table + instr_bytes）

### 1c: Stripped mode（zpkg indexed 用）

- [x] 1c.2 `ZbcWriter.Write(module, ZbcFlags.Stripped)` → NSPC + BSTR（body-only strings）+ FUNC；无 SIGS/IMPT/EXPT
- [x] 1c.3 `source_hash` 不写入 zbc（只在 zpkg FILE_TABLE）
- [ ] 1c.1 `BuildCommand.cs` — zpkg indexed build 时产出 stripped zbc 到 `.cache/`，读取 full zbc 的 SIGS → 写入 zpkg SYMBOL_INDEX
- [ ] 1c.4 读取方防护：`flags.STRIPPED=1` 的 zbc 出现在 Z42_PATH 时报错

### 1d: 测试

- [x] 1d.1 `ZbcWriter_MagicBytesCorrect` 更新 + `ZbcWriter_StrippedMode_FlagsSet`（full/stripped 两种模式验证）
- [x] 1d.2 `ZbcReader_ReadNamespace_ReturnsCorrectNamespace`（NSPC 快速扫描）
- [x] 1d.3 `ZbcWriter_StrippedMode_ContentStable`（相同 source → 逐字节相同 stripped zbc）

## 阶段 2: zpkg manifest 扩展

- [x] 2.1 `src/runtime/src/metadata/formats.rs` — `ZpkgFile` 新增 `namespaces: Vec<String>` 字段；`ZpkgDep` 重构
- [x] 2.2 `src/compiler/z42.Project/PackageTypes.cs` — `ZpkgFile` 新增 `Namespaces: List<string>`；`ZpkgDep` 重构
- [x] 2.3 `ZpkgDep` 结构重构：`(Name, Version?, Path?)` → `(File: string, Namespaces: List<string>)`（C# + Rust 两侧同步）
- [x] 2.4 `BuildCommand.cs` — 打包时汇总所有源文件 namespace 声明 → 写入顶层 `namespaces`
- [ ] 2.5 单元测试：验证 namespaces 字段在 indexed / packed 两种模式下均正确生成

## 阶段 3: VM module path（Z42_PATH）

- [ ] 3.1 `src/runtime/src/main.rs` — 新增 `Z42_PATH` 环境变量探测，按优先级构建 module path 列表：`Z42_PATH` → `<cwd>/` → `<cwd>/modules/`
- [ ] 3.2 `--verbose` 输出 module path 列表及找到的 `.zbc` 文件（仅 log）
- [ ] 3.3 `src/runtime/src/loader.rs` — `resolve_namespace(ns)` 先查 module path（读 zbc namespace header），再查 libs path（读 zpkg namespaces 字段）
- [ ] 3.4 同层冲突检测：两个文件提供相同 namespace → `AmbiguousNamespaceError`
- [ ] 3.5 单元测试：`ModuleResolutionTests` — 验证优先级及冲突报错

## 阶段 4: 编译器命名空间解析

- [ ] 4.1 `BuildCommand.cs` — 编译启动时扫描 libs/ 所有 zpkg，读 `namespaces` 字段，建立 `Map<namespace, zpkg_file>`
- [ ] 4.2 `BuildCommand.cs` — 扫描 Z42_PATH 所有 zbc，读 namespace header，建立 `Map<namespace, zbc_file>`；zbc 优先覆盖 zpkg 条目
- [ ] 4.3 TypeChecker 集成：`using X` 查 Map → 加载对应文件符号表；未找到 → `UnresolvedNamespaceError`
- [ ] 4.4 编译完成后写入 `dependencies`：只包含实际用到的 namespace 对应文件
- [ ] 4.5 Golden test：新增带跨包 `using` 的编译场景，验证 `dependencies` 字段内容

## 阶段 5: 文档更新

- [x] 5.1 `docs/design/project.md` L5 — 重写 `[dependencies]` 为包名+扫描设计；更新完整字段速查表
- [x] 5.2 `docs/design/ir.md` — 重写 Binary Format 章节：zbc full/stripped 双模式 + zpkg indexed/packed 格式
- [ ] 5.3 `docs/features.md` Section 17 — 补充双路径机制（Z42_PATH + Z42_LIBS）及 zbc/zpkg 职责说明
- [x] 5.4 `scripts/package.sh` — 移除 `.zbc` 占位文件生成，只保留 `.zpkg` 占位

## 阶段 6: 测试与验证

- [ ] 6.1 `dotnet build src/compiler/z42.slnx` — 无编译错误
- [ ] 6.2 `cargo build --manifest-path src/runtime/Cargo.toml` — 无编译错误
- [ ] 6.3 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` — 全绿
- [ ] 6.4 `./scripts/test-vm.sh` — 全绿
- [ ] 6.5 spec scenarios 逐条覆盖确认：
  - [ ] Z42_PATH / cwd / modules/ 搜索路径
  - [ ] zbc > zpkg 优先级
  - [ ] 同层冲突报错
  - [ ] zpkg namespaces 字段生成（indexed + packed）
  - [ ] dependencies 为解析结果（含 zbc 和 zpkg 两种）
  - [ ] zbc namespace header 读写
  - [ ] package.sh 不再产出 .zbc 占位

## 备注

- 此变更属于 L2 基础设施，不涉及语言语义变更，无需 DRAFT → 确认流程
- `z42.toml [dependencies]` 保留，语义从"声明式路径/版本"改为"包名 + 范围约束"；旧的 `{ path, version }` 格式需更新
- `ZpkgDep` 结构变更是 breaking change，但 dependencies 目前始终为 `[]`，无兼容性风险
- 脚本模式（无 `[dependencies]`）与正式项目模式（有 `[dependencies]`）共用同一套扫描逻辑，只是扫描范围不同
