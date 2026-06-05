# Spec: zpkg 自描述解析（注入 + NSPC 索引）

## ADDED Requirements

### Requirement: 宿主可注入 zpkg 字节

#### Scenario: 注入后按 namespace 解析
- **WHEN** 宿主在 `z42_host_load_zbc` 前调用 `z42_host_add_zpkg(handle, bytes, len)` 注入一个
  含 `NSPC = [z42.core, Std, Std.Exceptions]` 的 zpkg，随后加载一个 `import Std.Exceptions`
  的 .zbc
- **THEN** runtime 从注入字节读 NSPC，将 `Std.Exceptions` 解析到该 zpkg 的 module，加载成功

#### Scenario: 一个 zpkg 提供多个 namespace
- **WHEN** 注入的单个 zpkg 同时提供 `Std` 与 `Std.Exceptions`
- **THEN** 两个 namespace 都解析到**同一** module，且该 zpkg 字节只解析一次（不重复 merge）

#### Scenario: 注入顺序不影响结果（确定性）
- **WHEN** 以不同顺序注入同一组 zpkg
- **THEN** 最终 namespace → module 索引一致；多包共享同名 namespace 时 first-wins 结果稳定
  （按文件名 Ordinal 排序后裁决）

### Requirement: 解析链统一为读 NSPC

#### Scenario: 桌面扫描与注入互不排斥
- **WHEN** 既配置了 corelib/search_paths，又注入了部分 zpkg
- **THEN** 解析顺序为 `注入索引 → corelib → search_paths 扫描 → silent miss`；注入命中的
  namespace 不再走扫描

#### Scenario: 未提供的 namespace 静默放过
- **WHEN** 用户 .zbc import 一个既未注入、也不在 search_paths 的 namespace
- **THEN** `z42_host_load_zbc` 仍返回成功；仅当 invoke 用到该 namespace 的函数时报
  "undefined function"（保持现有 lazy 语义）

## MODIFIED Requirements

### Requirement: 宿主获取 stdlib 的方式

**Before:** runtime 调用宿主提供的 pull resolver `ZpkgResolver::resolve(ns) -> bytes`
（C ABI `Z42ZpkgResolverFn`）；宿主须自带 `namespace → 文件` 映射，移动/WASM 默认 resolver
读 `index.json` 完成映射。

**After:** 删除 pull resolver。宿主改为 `z42_host_add_zpkg` 主动注册字节（移动/WASM），或由
runtime 扫描 `search_paths`（桌面）。两条路径都由 runtime 读 zpkg 的 `NSPC` 认领 namespace，
**不存在 index.json**。

### Requirement: stdlib 发行布局

**Before:** `libs/` 含 `*.zpkg` + `*.zsym` + `index.json`（手维护 namespace 映射）。

**After:** `libs/` 含 `*.zpkg` + `*.zsym`，无 `index.json`。浏览器 WASM bundle 额外含 build
生成的纯文件名清单（`stdlib/files.json`），不含 namespace 映射。

## Pipeline Steps

受影响阶段（运行期 host 加载链，非编译 pipeline）：
- [x] Host API（`z42_host_add_zpkg` 新增 / `Z42ZpkgResolverFn` 删除）
- [x] `build_host_module` 解析循环
- [x] NSPC 读取复用（`read_zpkg_namespaces`）
- [ ] 编译 pipeline（Lexer/Parser/TypeCheck/Codegen）—— 不涉及
