# Tasks: unify-zpkg-format

> 状态：🟢 已完成 | 创建：2026-04-04

**变更说明：** 合并 `.zmod` / `.zbin` → `.zpkg`（`mode: indexed|packed`）；添加 `pack` 三层优先级配置；`[package]` → `[project]` 文档对齐；移除 `BuildConfig.Emit`。
**原因：** `.zmod` 和 `.zbin` 是同一工程包的两种形态，统一为 `.zpkg` 简化工具链和 VM 加载器。
**文档影响：** `docs/design/compilation.md`、`docs/design/project.md`

---

## 进度概览

- [ ] 阶段 1: C# 类型层（PackageTypes / ProjectTypes）
- [ ] 阶段 2: C# 构建驱动（BuildCommand / Program / ProjectManifest）
- [ ] 阶段 3: C# 测试更新
- [ ] 阶段 4: Rust 运行时（formats / loader / mod）
- [ ] 阶段 5: 文档同步
- [ ] 阶段 6: 验证

---

## 阶段 1: C# 类型层

- [ ] 1.1 `z42.IR/PackageTypes.cs`
  - 新增 `ZpkgMode { Indexed, Packed }`
  - 新增 `ZpkgKind { Exe, Lib }`（或复用 `ProjectKind`，待确认）
  - 新增 `ZpkgFileEntry` record（source / bytecode / source_hash / exports）
  - 新增 `ZpkgExport` record（symbol / kind）
  - 新增 `ZpkgDep` record（name / version / path）
  - 新增 `ZpkgFile` record（name / version / kind / mode / entry / exports / dependencies / files / modules）
  - 删除 `ZmodManifest`、`ZmodFileEntry`、`ZmodKind`
  - 删除 `ZbinFile`、`ZbinExport`、`ZbinDep`

- [ ] 1.2 `z42.IR/ProjectTypes.cs`
  - `EmitKind` 移除 `Zmod`、`Zbin`（保留 `Ir`、`Zbc`、`Zasm`）
  - `BuildConfig` 移除 `Emit` 字段
  - `ProfileConfig` 新增 `Pack: bool?`
  - `ProjectMeta` 新增 `Pack: bool?`（[project] 级默认）

---

## 阶段 2: C# 构建驱动

- [ ] 2.1 `z42.Build/ProjectManifest.cs`
  - 移除 `defaultEmit` 相关逻辑
  - 新增 `ResolvePack(profileName, exeTargetPack)` — 三层优先级解析
  - 默认值：debug → false，release → true

- [ ] 2.2 `z42.Driver/BuildCommand.cs`
  - 移除 `case "zmod"` / `case "zbin"` 分支
  - 新增统一输出路径：`dist/<name>.zpkg`
  - 根据 `ResolvePack` 结果决定 `ZpkgMode.Indexed` 或 `ZpkgMode.Packed`
  - indexed 模式：写 `files[]`，`modules = []`
  - packed 模式：写 `modules[]`，`files = []`
  - `[[exe]]` 多目标：每个 exe 各输出一个 `.zpkg`

- [ ] 2.3 `z42.Driver/Program.cs`（单文件模式）
  - `--emit` 选项移除 `zmod` / `zbin`（保留 `ir` / `zbc` / `zasm`）
  - help text 更新，移除对 zmod/zbin 的描述

---

## 阶段 3: C# 测试

- [ ] 3.1 `z42.Tests/GoldenTests.cs`
  - `ZbinFile` → `ZpkgFile`，`ZbinExport` → `ZpkgExport`
  - `.zbin` 扩展名 → `.zpkg`
  - 新增 indexed / packed 两种模式的 golden 场景

- [ ] 3.2 `z42.Tests/ProjectManifestTests.cs`
  - 移除 `m.Build.Emit` 相关断言
  - 新增 `ResolvePack` 优先级链的单元测试（5 个场景）：
    - 无配置 → debug=false / release=true
    - project.pack=true → 全局 true，release 不再覆盖
    - exe.pack 覆盖 project.pack
    - profile.pack 覆盖 exe.pack
    - profile.pack 覆盖 project.pack

---

## 阶段 4: Rust 运行时

- [ ] 4.1 `src/runtime/src/metadata/formats.rs`
  - 新增 `ZpkgMode`（`indexed` / `packed`，snake_case serde）
  - 新增 `ZpkgFileEntry` struct
  - 新增 `ZpkgExport` struct
  - 新增 `ZpkgDep` struct
  - 新增 `ZpkgFile` struct
  - 删除 `ZbinFile`、`ZbinExport`、`ZbinDep`、`ZBIN_MAGIC`
  - 删除 `ZmodManifest`、`ZmodFileEntry`

- [ ] 4.2 `src/runtime/src/metadata/loader.rs`
  - 删除 `load_zmod` / `load_zbin` / `check_zbin_version` 函数
  - 新增 `load_zpkg(path)` — 读 JSON → `ZpkgFile`；按 mode 分支处理
    - `indexed` → 按 `files[].bytecode` 读取 `.zbc`，合并
    - `packed`  → 直接从 `modules[]` 合并
  - `load_artifact` 扩展名匹配：`"zmod"` / `"zbin"` → 错误；`"zpkg"` → `load_zpkg`

- [ ] 4.3 `src/runtime/src/metadata/mod.rs`
  - 更新 re-export：`ZbinFile` → `ZpkgFile`

---

## 阶段 5: 文档同步

- [ ] 5.1 `docs/design/compilation.md`
  - 更新"文件扩展名总览"表：删除 `.zmod` / `.zbin` 行，新增 `.zpkg` 行
  - 更新"VM 加载语义"表
  - 更新"输出路径约定"表

- [ ] 5.2 `docs/design/project.md`
  - 全文 `[package]` → `[project]`
  - emit 格式表更新（移除 `zmod` / `zbin`，说明 pack 字段）
  - 新增 `pack` 字段说明（L3 或 L4 层次）
  - `[build]` 字段表移除 `emit` 行

---

## 阶段 6: 验证

- [ ] 6.1 `dotnet build src/compiler/z42.slnx` — 无编译错误
- [ ] 6.2 `cargo build --manifest-path src/runtime/Cargo.toml` — 无编译错误
- [ ] 6.3 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` — 全绿
- [ ] 6.4 `./scripts/test-vm.sh` — 全绿
- [ ] 6.5 spec scenarios 逐条覆盖确认

---

## 备注
