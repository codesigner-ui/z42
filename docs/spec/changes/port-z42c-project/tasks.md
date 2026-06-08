# Tasks: port-z42c-project — z42c.project 真实移植

> 状态：🟡 进行中 | 创建：2026-06-08 | 子系统锁：z42c（顺序续作，承接 port-z42c-syntax）
> **变更说明：** 把 C# `z42.Project`（清单模型 / 加载器 / zpkg 读写）用 z42 重写，替换 ProjectSkeleton。
> **类型：** port（实现既有行为）。架构见 [self-hosting.md](../../../design/compiler/self-hosting.md)。
> **C# 参考映射**：见会话内 z42.Project 全量 map（ProjectManifest 769 / ManifestLoader 398 / ZpkgReader·Writer ~1750 byte-identical / 等，共 ~5500 LOC）。
> **stdlib 杠杆**：用 `Std.Toml`（解析）/ `Std.IO`（文件·二进制）/ `Std.Collections` —— 不 hand-roll（区别于 C# Tomlyn/FileSystemGlobbing）。

## increment 1（清单模型 + 单清单加载器 — ✅ 已完成）
- [x] `ProjectModel.z42`：`ProjectManifest`（[project] name/version/kind/entry/pack + [sources] include·exclude + [dependencies]）+ `DepEntry`（受限写法 typed array + count）
- [x] `ManifestLoader.z42`：`ParseText(text)` / `Load(path)`（`Std.Toml.TomlValue` 导航；deps 支持字符串版本 + 表 `{version,path}` 两形态）
- [x] z42c.project 加 `z42.toml` 依赖；新 tests/manifest/ 单元（3 单测：project 段 / sources+deps / dep 表形态）
- [x] 验证：`xtask test compiler-z42` → **7 units 93 cases** 全绿（7/7 zpkg；z42c.project.zpkg 11152 bytes）

## 后续增量（机械段，依赖 stdlib）
- [ ] **2**：workspace 发现 + 成员展开（`z42.workspace.toml`）+ 继承（C1 shared fields）+ ResolvedManifest
- [ ] **3**：源文件 glob 发现（`Std.IO` glob）+ include 链解析（IncludeResolver DFS）+ 路径模板（`${workspace_dir}` 等）

## 后续增量（byte-identical 硬段，强依赖 z42c.ir 的 ZbcFile/IrModule —— 须先移植 ir/emit）
- [ ] **4**：ZpkgReader（META/STRS/NSPC/EXPT/DEPS/SIGS/MODS/FILE/TSIG/IMPL 各段；zpkg v0.11 格式，逐字节）
- [ ] **5**：ZpkgWriter + ZpkgBuilder（packed/indexed 模式；string pool dedup；split-debug-symbols sidecar + BLAKE3-128 BLID）→ round-trip golden
- [ ] **6**：integration（ResolvedManifest → 源 → lex/parse/typecheck/codegen → ZbcFile → ZpkgBuilder → ZpkgWriter → dist/*.zpkg）

> **注**：increment 4-6（zpkg 读写）须等 z42c.ir（IrModule/ZbcFile/ZbcWriter）移植到位——zpkg 承载的是 IR 编译产物。故 **project 的硬段排在 ir/semantics 之后**。机械段（1-3，清单/源发现）可独立先行。

## 备注
- ProjectSkeleton.z42 暂留（semantics/pipeline 仍引用，各自移植时移除）。
- 整体自举进度 + 受限写法见 memory `project_z42c_selfhosting.md`。
