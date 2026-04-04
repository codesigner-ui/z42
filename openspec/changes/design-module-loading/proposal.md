---
name: design-module-loading
description: 模块加载与依赖解析系统设计
type: project
---

# Proposal: Module Loading & Dependency Resolution

## Why

当前 `using` 命名空间与物理文件（zpkg/zbc）没有明确的映射规则，VM 只有 `Z42_LIBS` 一条搜索路径，且 `z42.toml` 的 `[dependencies]` 采用声明式设计（类 Cargo），复杂且需要手动维护。

需要一套简单、用户可控、支持 Python-like 轻量加载的模块系统：
- 正式打包依赖（zpkg）走 libs/ 路径
- 轻量脚本模块（zbc）走独立的 module path
- 编译器自动发现依赖，无需手写 `[dependencies]`

## What Changes

- VM 新增 `Z42_PATH` / `modules/` 搜索路径，专用于 zbc 模块加载
- zpkg manifest 新增顶层 `namespaces: string[]` 字段，供编译器快速扫描
- zpkg `dependencies` 字段改为编译期**解析结果**（记录实际文件名），而非声明式
- zbc 格式新增轻量 namespace header，允许不解析完整字节码就读取命名空间
- 编译器通过扫描 libs/ 和 Z42_PATH 自动解析 `using`，不再需要 `z42.toml [dependencies]`
- 解析优先级：Z42_PATH（zbc）> Z42_LIBS（zpkg）；同层冲突 = 编译错误
- project.md L5 更新为新的自动发现设计

## Scope

| 文件/模块 | 变更类型 | 说明 |
|-----------|---------|------|
| `src/runtime/src/main.rs` | modify | 新增 Z42_PATH 搜索路径探测 |
| `src/runtime/src/loader.rs` | modify | 扫描 zbc namespace header；依据 dep manifest 加载依赖 |
| `src/runtime/src/formats.rs` | modify | ZpkgFile 新增 namespaces；ZpkgDep 改为解析结果格式 |
| `src/compiler/z42.Driver/BuildCommand.cs` | modify | 命名空间解析 + 依赖记录写入输出 zpkg |
| `src/compiler/z42.IR/Formats/ZbcFormat.cs` | modify | zbc 新增 namespace header section |
| `docs/design/project.md` | modify | L5 改为自动发现设计，移除声明式 `[dependencies]` |
| `docs/design/ir.md` | modify | 补充 zbc namespace header 格式 |
| `docs/features.md` | modify | Section 17 补充双路径机制说明 |

## Out of Scope

- Lockfile / 版本解析器
- 包注册中心（registry）
- packed zpkg 内 fat→thin zbc 优化（另立变更）
- z42.toml `[dependencies]` 声明式语法（由自动发现替代）
- workspace 依赖管理（L6）

## Open Questions

- [x] z42.toml 是否完全去掉 `[dependencies]` section，还是保留为可选的文档注释？
  → **保留**。语义改为"包名 + 范围约束"，编译器按包名在 libs/ 查 zpkg，从其 namespaces 字段解析命名空间。无 `[dependencies]` 时为自动发现的脚本模式。
