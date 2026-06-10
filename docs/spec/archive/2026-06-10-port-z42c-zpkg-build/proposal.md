# Proposal: port-z42c-zpkg-build — `z42c build` 端到端产 byte-identical `.zpkg`

> 状态：DRAFT（待 User 审批）｜子系统锁：z42c（source-spans 归档后接力）

## Why

z42c 已能产 byte-identical `.zbc`（含 DBUG），但还不能产**包**：`z42c build <toml>` 不存在，自举编译器无法产出 VM 可直接运行的 `.zpkg`（CLI parity 表的 0.3.9 大件）。不做：自举永远停在单文件 `--emit-zbc`，无法走向"z42c 编译 stdlib/自身"的对账与 1.0 切换。

## What Changes

- **BP-0 IrGen 命名空间限定**：namespaced 源的类/函数/ctor 名全限定（`Demo.Minimal.Greeter` 等，fixture STRS 实证）；无 namespace 源行为不变（既有 e2e/golden 全保）
- **BP-1 z42c.project 文件系统段**：源发现（`[sources].include` glob → 排序文件列表，镜像 IncludeResolver/GlobExpander 核心）
- **BP-2 包模型 + 组装**：PackageTypes（ZbcFileZ/ZpkgFileZ/Export/Dep）+ ZpkgBuilder（exports 限定 + namespaces 去重 + **entry 自动检测**：`.Main`>`Main`>`.main`>`main`，歧义报错）+ SourceHash（stdlib `Sha256` + 大写 hex）
- **BP-3 ZpkgWriter packed 模式**：META/STRS(全模块统一池)/NSPC/EXPT/DEPS/SIGS/MODS 七段（镜像 C# ZpkgWriter；MODS 复用 z42c ZbcWriter 的 FUNC/TYPE/DBUG/REGT 段构建器——需把它们重构为共享池+remap 参数化；每模块独立 TokenAllocator）
- **BP-4 driver `build <toml>`**：manifest 载入（已有）→ 源发现 → 逐文件编译 → BuildPacked → 写 `<name>.zpkg`（单工程/packed/无增量）
- **BP-5 验证**：① **byte-identical 对 `src/tests/zpkg-format/packed-minimal` fixture**（namespaced 单类，777B 级）；② e2e：z42c build 出 exe zpkg → z42vm **直接跑 zpkg**（entry 已烤入，不需要位置实参）+ xtask byte-compare 扩展（z42c build vs C# build 同工程 diff）

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/z42c/z42c.semantics/src/IrGen.z42` | MODIFY | BP-0 命名空间限定（类/函数 key/ObjNew 引用） |
| `src/z42c/z42c.semantics/src/ExprEmitter.z42` | MODIFY | （如需）调用/new 的 FQ 引用穿线 |
| `src/z42c/z42c.semantics/src/FunctionEmitter.z42` | MODIFY | （如需）FQ 函数名穿线 |
| `src/z42c/z42c.project/src/SourceDiscovery.z42` | NEW | BP-1 glob 源发现（排序确定性） |
| `src/z42c/z42c.project/src/PackageTypes.z42` | NEW | BP-2 ZbcFileZ/ZpkgFileZ/ZpkgExportZ/ZpkgDepZ |
| `src/z42c/z42c.project/src/ZpkgBuilder.z42` | NEW | BP-2 组装 + exports 限定 + entry 检测 |
| `src/z42c/z42c.project/src/ZpkgWriter.z42` | NEW | BP-3 packed 七段 + 装配 |
| `src/z42c/z42c.project/z42c.project.z42.toml` | MODIFY | + `z42c.ir` 依赖（镜像 C# project→ir 边）+ z42.crypto |
| `src/z42c/z42c.ir/src/BinaryFormat/ZbcWriter.z42` | MODIFY | 段构建器共享化（public + 外部池/remap/allocator 参数） |
| `src/z42c/z42c.driver/src/Main.z42` | MODIFY | BP-4 `build <toml>` 路由 |
| `src/z42c/z42c.driver/z42c.driver.z42.toml` | MODIFY | + z42c.project 依赖 |
| `src/z42c/z42c.project/tests/zpkg/*`（toml+tests）| NEW | BP-5 fixture byte-identical 断言 |
| `scripts/xtask_compiler_z42.z42` | MODIFY | e2e：build 产 zpkg 直跑 + build byte-compare |
| `src/z42c/z42c.project/README.md` / `z42c.ir/README.md` / `z42c.driver/README.md` | MODIFY | 同步 |
| `docs/design/compiler/self-hosting.md` | MODIFY | build/对账状态 |

**只读引用**：C# `ZpkgWriter*.cs`/`ZpkgBuilder.cs`/`PackageTypes.cs`/`PackageCompiler.BuildTarget.cs`（AutoDetectEntry/Sha256Hex/SourceFile 相对化）/`IncludeResolver.cs`/`GlobExpander.cs`；`src/tests/zpkg-format/packed-minimal/`（oracle）。

## Out of Scope

- indexed 模式 / `.zsym` sidecar / stripSymbols（MVP 全走 packed 内联 DBUG）
- TSIG/IMPL 段（ExportedModules——跨包泛型/接口签名导出；packed-minimal fixture 无此段）
- 增量编译 / workspace 多成员 / 依赖解析（DEPS 先写空表；stdlib 经 Z42_LIBS 运行期解析）
- cache `.zbc` 散文件落盘（ZpkgBuilder 的 cacheDir 行为）

## Open Questions

- [ ] Q1：BP-0 全限定会改变 namespaced 源的 `--dump-ir` 文本（函数名带 ns）——接受为正确行为？（C# 同形态）
- [ ] Q2：fixture byte-identical 需要 dotnet 不在场也可跑（golden hex 内嵌测试） vs 直接读 fixture 文件对比——拟内嵌 hex（同 zbc golden 模式）
