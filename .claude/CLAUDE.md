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

# 运行编译器
dotnet run --project src/compiler/z42.Driver -- <file.z42> [--emit ir|zbc|zmod|zlib]

# 运行 VM
cargo run --manifest-path src/runtime/Cargo.toml -- <file.z42ir.json> [--mode interp|jit|aot]

# 测试（编译器 golden tests + VM interp/jit 两种模式）
dotnet test src/compiler/z42.Tests/z42.Tests.csproj
./scripts/test-vm.sh
```

> 修改编译器后，先 `--emit ir` 重新生成 `.z42ir.json`，再跑 `./scripts/test-vm.sh`。

## 实现阶段

| 阶段 | 内容 | 状态 |
|------|------|------|
| 1 | 编译器：Lexer + Parser | ✅ |
| 2 | 编译器：类型检查器 | 🚧 |
| 3 | 编译器：IR Codegen → .z42bc | 🚧 |
| 4 | Rust VM：解释器 | ✅ |
| 5 | Rust VM：JIT（Cranelift） | ✅ |
| 6 | Rust VM：AOT（LLVM/inkwell） | 📋 |
| 7 | 自举 | 📋 |

## 语言设计策略

- **Phase 1（当前）**：语法完全对齐 C# 9–12，尽快跑通完整 pipeline
- **Phase 2**：编译器基础设施巩固（AST 配置化、错误体系、z42bc 二进制格式、VM 框架）
- **Phase 3**：引入 Rust/Python 优点（Result、match、Trait、ADT）——Phase 1/2 完成前不讨论

**固定决策**：IR 是寄存器 SSA 形式；执行模式注解作用于命名空间级；`.z42bc` magic = `Z42\0`；z42 始终带 GC，不引入所有权/借用。

## 改动验证流程（必须遵守）

每次完成一批改动后，按顺序执行，不需要用户提醒：

1. `dotnet build` + `cargo build` —— 确保无编译错误
2. `dotnet test` + `./scripts/test-vm.sh` —— 确保全部测试通过
3. `git add <changed files> && git commit -m "type(scope): 描述"`
4. `git push origin main`

- **禁止**在测试失败时 commit 或 push
- 每个逻辑完整的改动单元单独提交，不积压

## 新语法/特性开发流程（必须遵守）

1. **起草规范**：在 `docs/design/` 新建或更新规范文档
2. **确认**：取得用户明确确认后才开始实现
3. **实现**：严格对齐已确认规范；发现偏差必须重走步骤 1–2
4. **验证**：按改动验证流程通过后提交

**禁止**在规范未确认时提前写实现代码。

## 文档同步（必须遵守）

| 改动类型 | 需要更新的文档 |
|----------|--------------|
| 新语法 / 语句 | `docs/design/language-overview.md` + `docs/design/<feature>.md` |
| 新 IR 指令 | `docs/design/ir.md` |
| 新 VM 行为 | `docs/design/<feature>.md` |
| 新构建步骤 / CLI 参数 | `CLAUDE.md` 构建与测试部分 |
| 规范偏差 | 以实现为准更新规范，不得描述不存在的行为 |

## 代码风格

**C#**：C# 12+ 特性；AST 节点用 `sealed record`；错误用异常（`ParseException`）；命名空间 `Z42.Compiler.*`

**Rust**：`anyhow::Result` + `thiserror`；不用 `unwrap()`；公开类型加 `#[derive(Debug)]`

## 注意事项

- 自举完成前，不把编译器代码改写成 z42
- 解释器全部测试通过前，不填充 JIT/AOT 实现
- Phase 3 特性不在 Phase 1/2 阶段引入到规范或代码中

@docs/design/language-overview.md
@docs/design/ir.md
