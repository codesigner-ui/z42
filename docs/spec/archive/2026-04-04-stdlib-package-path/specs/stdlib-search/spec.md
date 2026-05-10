# Spec: stdlib-search

## ADDED Requirements

### Requirement: VM libs 目录探测

#### Scenario: $Z42_LIBS 环境变量存在且目录有效
- **WHEN** 用户设置 `Z42_LIBS=/some/path/libs` 且该目录存在
- **THEN** VM 使用该路径作为 libs 目录，verbose log 输出 "libs dir: /some/path/libs"

#### Scenario: adjacent libs/ 目录存在
- **WHEN** `artifacts/z42/bin/z42vm` 运行，`artifacts/z42/libs/` 存在
- **THEN** VM 通过 `<binary-dir>/../libs/` 找到该目录并 log

#### Scenario: CWD artifacts/z42/libs/ 存在
- **WHEN** 从项目根 `cargo run -- file.z42ir.json --verbose` 运行
- **THEN** VM 通过 `<cwd>/artifacts/z42/libs/` 找到并 log

#### Scenario: 所有路径均不存在
- **WHEN** 无 `Z42_LIBS`，adjacent 和 cwd 路径均不存在
- **THEN** VM 正常启动（不报错），verbose log 输出 "libs dir: not found"

#### Scenario: libs/ 目录存在，列出模块文件
- **WHEN** libs 目录找到，目录内有 `.zpkg` / `.zbc` 文件
- **THEN** verbose log 列出找到的文件名（仅 log，不加载）

### Requirement: package.sh 打包产物

#### Scenario: 默认 debug 打包
- **WHEN** 执行 `./scripts/package.sh`
- **THEN** `artifacts/z42/bin/z42vm` 存在（debug build）
- **AND** `artifacts/z42/libs/` 下存在 5 个 `.zbc` + 5 个 `.zpkg` 占位文件

#### Scenario: release 打包
- **WHEN** 执行 `./scripts/package.sh release`
- **THEN** `artifacts/z42/bin/z42vm` 为 release build（更小体积）

## MODIFIED Requirements

### stdlib.md 搜索路径规范更新

**Before:**
```
1. $Z42_STDLIB
2. <vm-binary-dir>/stdlib/
3. ~/.z42/stdlib/
```

**After:**
```
1. $Z42_LIBS
2. <vm-binary-dir>/../libs/
3. <cwd>/artifacts/z42/libs/
```

## Pipeline Steps

受影响组件：
- [x] VM startup（`main.rs`）— 搜索路径探测
- [ ] Lexer / Parser / TypeChecker / IR Codegen — 不涉及
