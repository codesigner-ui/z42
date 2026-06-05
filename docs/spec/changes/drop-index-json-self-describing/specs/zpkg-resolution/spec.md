# Spec: zpkg NSPC 自描述解析

## ADDED Requirements

### Requirement: 从 zpkg 读取解析键

#### Scenario: 返回包名 + namespace
- **WHEN** 对 `z42.core.zpkg` 字节调用 `z42_zpkg_read_namespaces`
- **THEN** visitor 被回调返回其**包名** `z42.core`（prelude 解析键）**及**它声明的 namespace
  （`Std` 等）；状态 `OK`

#### Scenario: 一个 zpkg 多 namespace
- **WHEN** 该 zpkg 的 NSPC 声明多个 namespace
- **THEN** 每个 namespace 都被回调一次（连同包名），resolver 据此把它们都映射到同一份字节

#### Scenario: 非法字节
- **WHEN** 传入非 zpkg 字节（坏 magic / 版本不符）
- **THEN** 返回 `ERR_BAD_ZBC`，不回调

#### Scenario: visitor 为 NULL
- **WHEN** `visit` 为 NULL
- **THEN** 返回 `ERR_BAD_CONFIG`

### Requirement: 平台默认 resolver 读 NSPC

#### Scenario: 枚举 + 读 NSPC 建表
- **WHEN** iOS `BundleZpkgResolver` / Android `AssetZpkgResolver` / WASM `bundleStdlib*` 构造
- **THEN** 枚举可见 `*.zpkg`，对每个调 `z42_zpkg_read_namespaces`（或其语言绑定）建
  `namespace → bytes` 表；**不读** `index.json`

#### Scenario: 加载 hook 不变
- **WHEN** 运行时 `load_zbc` 对每个 namespace 调 `resolver.resolve(ns)`
- **THEN** 行为与改造前一致；仅 resolver 内部的映射来源从 index.json 变为 NSPC

## MODIFIED Requirements

### Requirement: stdlib 发行布局

**Before:** flat dist / `libs/` / bundle / asset 含 `*.zpkg` + 手维护 `index.json`。

**After:** 只含 `*.zpkg`（+ `*.zsym`）。浏览器 WASM bundle 额外含 build 生成的纯文件名清单
`files.json`（非 namespace 映射）。namespace 归属一律读 NSPC。

## Pipeline Steps
- [x] Host C ABI（`z42_zpkg_read_namespaces` 新增）
- [x] 平台 facade resolver（iOS/Android/WASM 读 NSPC）
- [x] 加载 hook（`ZpkgResolver` 保留不变）
- [ ] 编译 pipeline —— 不涉及
