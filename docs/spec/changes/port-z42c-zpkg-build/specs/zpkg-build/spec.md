# Spec: zpkg-build — `z42c build` 端到端

## ADDED Requirements

### Requirement: z42c build 产 packed zpkg

#### Scenario: 单工程 exe 构建
- **WHEN** `z42c build <dir>/app.z42.toml`（kind=exe，源含 `Main`）
- **THEN** 写出 `<dir>/dist/app.zpkg`（packed，flags 含 Exe），META.entry = 自动检测的 Main FQ 名

#### Scenario: z42vm 直接执行产物
- **WHEN** `z42vm <dir>/dist/app.zpkg`（无位置 entry 实参）
- **THEN** 程序按烤入 entry 正确执行（div-zero oracle 自检程序干净退出）

#### Scenario: entry 歧义报错
- **WHEN** 两个文件各有 `Main` 且 manifest 未显式 entry
- **THEN** 构建失败并输出歧义诊断（列出候选）

### Requirement: byte-identical 对 C#

#### Scenario: packed-minimal 同源逐字节
- **WHEN** z42c 构建与 `src/tests/zpkg-format/packed-minimal` 同构的工程（namespaced 单类 + Main）
- **THEN** `.zpkg` 输出与 C# 产物**逐字节一致**（META/STRS/NSPC/EXPT/DEPS/SIGS/MODS 七段全对）

#### Scenario: e2e build byte-compare
- **WHEN** xtask e2e 把同一临时工程分别交 z42c 与 C# `build`
- **THEN** 两个 `.zpkg` 逐字节 diff 为空

### Requirement: namespace 限定（BP-0）

#### Scenario: namespaced 名字全限定
- **WHEN** 源为 `namespace Demo; class G { string H() {...} }`
- **THEN** IrModule 类名 `Demo.G`、函数 key `Demo.G.H`、exports `Demo.G.H`（fixture 同形态）

#### Scenario: 无 namespace 行为不变
- **WHEN** 既有全部无 namespace 的 golden/e2e 源重跑
- **THEN** `.zbc` 字节与本变更前完全一致（zbc golden 零变化）

## IR Mapping
无新 IR 指令；新增 `.zpkg` 写出路径（既有格式，z42c 首次产出）。

## Pipeline Steps
- [x] Lexer / Parser / TypeChecker / IR Codegen（已有；BP-0 仅名字限定）
- [ ] SourceDiscovery（project）
- [ ] ZpkgBuilder / ZpkgWriter（project）
- [ ] driver `build`
- [ ] VM interp（不改——zpkg 既有格式）
