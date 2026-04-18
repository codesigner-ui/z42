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
docs/design/    # 语言规范（language-overview.md, ir.md, ...）
examples/       # .z42 示例源文件
```

## 构建与测试

```bash
# 构建
dotnet build src/compiler/z42.slnx
cargo build --manifest-path src/runtime/Cargo.toml

# 运行编译器（单文件）
dotnet run --project src/compiler/z42.Driver -- <file.z42> [--emit ir|zbc|zasm] [-o <out>]

# 运行编译器（项目模式）
dotnet run --project src/compiler/z42.Driver -- build [<name>.z42.toml] [--release] [--bin <name>]
dotnet run --project src/compiler/z42.Driver -- check [<name>.z42.toml] [--bin <name>]
dotnet run --project src/compiler/z42.Driver -- run   [<name>.z42.toml] [--release] [--bin <name>] [--mode interp|jit|aot]
dotnet run --project src/compiler/z42.Driver -- clean [<name>.z42.toml]

# 其他工具命令
dotnet run --project src/compiler/z42.Driver -- disasm <file.zbc> [-o <file.zasm>]
dotnet run --project src/compiler/z42.Driver -- explain <ERROR_CODE>
dotnet run --project src/compiler/z42.Driver -- errors

# 运行 VM
cargo run --manifest-path src/runtime/Cargo.toml -- <file.z42ir.json> [--mode interp|jit|aot]

# 打包（VM binary + stdlib libs 占位产物 → artifacts/z42/）
./scripts/package.sh            # debug build
./scripts/package.sh release    # release build

# 编译标准库（src/libraries/**/*.z42 → artifacts/z42/libs/*.zpkg）
./scripts/build-stdlib.sh           # debug build
./scripts/build-stdlib.sh release   # release build

# 测试（编译器 golden tests + VM interp/jit 两种模式）
dotnet test src/compiler/z42.Tests/z42.Tests.csproj
./scripts/test-vm.sh
```

> 修改编译器后，先 `--emit ir` 重新生成 `.z42ir.json`，再跑 `./scripts/test-vm.sh`。
> `artifacts/z42/` 已在 `.gitignore` 中，不纳入版本控制。
> 修改标准库源文件后需重新运行 `./scripts/build-stdlib.sh` 更新 zpkg 产物。

## 实现计划

见 `docs/roadmap.md`。当前焦点：**L2 阶段：M6（工程支持 + 测试体系 + 错误码）→ M7（VM 元数据 + 标准库）**。

## 协作工作流（必须遵守）

见 `.claude/rules/workflow.md`。核心要点：

- **每次新对话**：Claude 自动读取当前阶段和 memory，主动说明状态和下一步
- **需规范先行**（lang / ir / vm 类变更）：DRAFT → User 确认 → IMPL → GREEN → COMMIT
- **轻量变更**（fix / refactor / test）：直接 IMPL → GREEN → COMMIT
- **GREEN 标准**（所有迭代必须满足）：
  ```bash
  dotnet build src/compiler/z42.slnx       # 无编译错误
  cargo build --manifest-path src/runtime/Cargo.toml
  dotnet test src/compiler/z42.Tests/z42.Tests.csproj        # 100% 通过
  ./scripts/test-vm.sh                     # 100% 通过
  ```
  **重点：任何测试失败都不得 commit / push。Pre-existing 失败必须在本迭代修复。**
- **提交格式**：`type(scope): 描述`，每个逻辑单元单独提交
- **自动提交**：每次迭代完成后 Claude 自动 commit + push，`.claude/` 和 `openspec/` 必须纳入，无需 User 二次确认

## 文档同步（必须遵守）

**核心规则：任何改变了外部可见行为、机制、规则或约定的迭代，归档前必须有对应文档落地。无文档 = 未完成。**

| 改动类型 | 需要更新的文档 |
|----------|--------------|
| 新语法 / 语句 | `docs/design/language-overview.md` + `docs/design/<feature>.md` |
| 新 IR 指令 | `docs/design/ir.md` |
| 新 VM 行为 | `docs/design/<feature>.md` |
| 新构建步骤 / CLI 参数 | `CLAUDE.md` 构建与测试部分 |
| 特性实现进度变更 | `docs/roadmap.md` L1 进度表 |
| 新工程文件规则 / manifest 字段 | `docs/design/project.md` |
| 新协作规则 / 工作流变更 | `.claude/rules/workflow.md` |
| fix / refactor 涉及行为或机制变更 | 对应 `docs/design/` 文档 |
| 语言设计决策变更（设计目标、phase 归属、设计理由） | `docs/features.md` |
| 规范偏差 | 以实现为准更新规范，不得描述不存在的行为 |

## 代码风格

**C#**：C# 12+ 特性；AST 节点用 `sealed record`；错误用异常（`ParseException`）；命名空间 `Z42.Compiler.*`

**Rust**：`anyhow::Result` + `thiserror`；非测试代码不用 `unwrap()`；公开类型加 `#[derive(Debug)]`

## 代码组织（必须遵守）

详见 `.claude/rules/code-organization.md`。核心要点：

- **目录 README.md**：每个功能目录有 `README.md`；Claude 读代码前先读该目录的 README
- **文件行数**：软限制 300 行，硬限制 500 行（超出必须拆分）
- **函数行数**：软限制 40 行，硬限制 60 行
- **类型/impl 行数**：硬限制 200 行
- **Rust 测试**：单元测试放独立 `<module>_tests.rs` 文件，不内联在实现文件中

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
