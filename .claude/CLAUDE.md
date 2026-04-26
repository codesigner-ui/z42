# z42 — Claude 工作手册

## 项目简介

z42 是一门融合 C#、Rust、Python 优点的系统编程语言。
- 编译器：C#（Bootstrap），最终自举为 z42
- 虚拟机：Rust，支持 Interpreter / JIT / AOT 混合执行
- 详细设计见 `docs/design/`；库推荐见 `.claude/libraries.md`

## 代码库结构

```
src/compiler/   # C# Bootstrap 编译器（z42.IR / z42.Compiler / z42.Driver）
src/runtime/    # Rust VM（interp / jit / aot）
src/libraries/  # 标准库 .z42 源码（编译后产出 .zpkg）
src/toolchain/  # 配套工具链（host / debugger / packager / workload，均为占位）
docs/design/    # 语言规范（language-overview.md, ir.md, ...）
examples/       # .z42 示例源文件
```

## 构建与测试

所有构建、编译、测试、打包命令见 [docs/dev.md](../docs/dev.md)。

## 实现计划

见 `docs/roadmap.md`。当前焦点：**L2 阶段：M6（工程支持 + 测试体系 + 错误码）→ M7（VM 元数据 + 标准库）**。

## 协作工作流（必须遵守）

完整流程见 `.claude/rules/workflow.md`。核心要点：

- **每次新对话**：Claude 自动读取 `.claude/projects/<project>/memory/MEMORY.md` 和当前阶段，主动说明状态和下一步
- **需规范先行**（lang / ir / vm 类变更）：DRAFT → User 确认 → IMPL → GREEN → COMMIT
- **轻量变更**（fix / refactor / test）：直接 IMPL → GREEN → COMMIT
- **全绿（GREEN）标准**：定义见 [workflow.md 阶段 8](rules/workflow.md)；任何测试失败（含 pre-existing）都不得 commit / push
- **提交格式**：`type(scope): 描述`，每个逻辑单元单独提交
- **自动提交**：每次迭代完成后 Claude 自动 commit + push，`.claude/` 和 `spec/` 必须纳入，无需 User 二次确认

## 文档同步（必须遵守）

**核心规则：任何改变了外部可见行为、机制、规则或约定的迭代，归档前必须有对应文档落地。无文档 = 未完成。**

具体的"改动类型 → 需更新文档"映射表见 [workflow.md 阶段 9](rules/workflow.md)。

> **实现原理文档规则（2026-04-25）**：涉及编译器或 VM 的**内部机制 / 架构策略**的变更（不只是对外行为），必须把"实现原理"（数据结构、算法、加载策略、决策权衡）同步到 `docs/design/compiler-architecture.md` 或 `docs/design/vm-architecture.md`，使新接手者不必阅读大量源码即可理解"为什么这样设计"。

## 代码风格

**C#**：C# 12+ 特性；AST 节点用 `sealed record`；错误用异常（`ParseException`）；命名空间 `Z42.Compiler.*`

**Rust**：`anyhow::Result` + `thiserror`；非测试代码不用 `unwrap()`；公开类型加 `#[derive(Debug)]`

## 代码组织（必须遵守）

完整规则见 `.claude/rules/code-organization.md`（目录 README、文件/函数/类型行数限制、Rust 测试拆分等）。

## 规范冲突检测（必须遵守）

**Claude 在实施任何变更时，若发现规范文档之间存在冲突、不一致或冗余，必须立即：**

1. **停下来指出冲突**，描述：哪两个文档、哪条规则、冲突点是什么
2. **提出建议的解决方案**（以哪个为准，或如何合并）
3. **等待 User 裁决**后再继续实施
4. **裁决结果同步到所有相关文档**，确保唯一真相来源

> 规范冲突优先级高于当前任务推进，适用于所有规范文档（`CLAUDE.md`、`workflow.md`、`code-organization.md`、`docs/design/` 等）。

## 注意事项

- 自举完成前，不把编译器代码改写成 z42
- M4（解释器）全绿前，不填充 JIT/AOT 实现
- L2/L3 特性（Result、Trait、ADT、泛型、Lambda、async 等）不在 L1 阶段引入到规范或代码中

@docs/design/language-overview.md
@docs/design/ir.md
