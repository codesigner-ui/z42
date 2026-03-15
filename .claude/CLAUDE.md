# z42 — Claude 工作手册

## 项目简介

z42 是一门融合 C#、Rust、Python 优点的系统编程语言。
- **编译器 / 工具链**：C#（Bootstrap），最终自举为 z42
- **虚拟机**：Rust，支持 Interpreter / JIT / AOT 混合执行
- **详细设计**：见 `specs/`

## 代码库结构

```
z42/
├── CLAUDE.md
├── specs/                    # 语言规范（language-overview.md, ir.md）
├── examples/                 # .z42 示例源文件
└── src/
    ├── compiler/             # C# Bootstrap 编译器 (.NET 10)
    │   ├── Z42.IR/           # IR 数据类型（纯数据，无逻辑）
    │   ├── Z42.Compiler/
    │   │   ├── Lexer/        # Lexer.cs, Token.cs, TokenKind.cs
    │   │   ├── Parser/       # Parser.cs, Ast.cs
    │   │   ├── TypeCheck/    # 类型检查器（待实现）
    │   │   └── Codegen/      # IR 代码生成（待实现）
    │   └── Z42.Driver/       # CLI 入口 z42c
    └── runtime/              # Rust VM
        └── src/
            ├── bytecode.rs   # .z42bc 模块格式
            ├── types.rs      # Value、ExecMode
            ├── interp.rs     # 解释器后端
            ├── jit.rs        # JIT 存根
            ├── aot.rs        # AOT 存根
            └── vm.rs         # 调度层
```

## 构建命令

```bash
# 编译器（C#）
dotnet build src/compiler/z42.slnx

# 运行编译器
dotnet run --project src/compiler/Z42.Driver -- <file.z42> [--dump-tokens]

# VM（Rust）
cargo build --manifest-path src/runtime/Cargo.toml

# 运行 VM
cargo run --manifest-path src/runtime/Cargo.toml -- <file.z42bc> [--mode interp|jit|aot]
```

## 实现阶段

| 阶段 | 内容 | 状态 |
|------|------|------|
| 0 | 规范设计、项目骨架 | ✅ 完成 |
| 1 | 编译器：Lexer + Parser | ✅ 完成 |
| 2 | 编译器：类型检查器 | 🚧 待实现 |
| 3 | 编译器：IR Codegen → .z42bc | 🚧 待实现 |
| 4 | Rust VM：解释器完整实现 | 🚧 待实现 |
| 5 | Rust VM：JIT（Cranelift） | 📋 规划 |
| 6 | Rust VM：AOT（LLVM/inkwell） | 📋 规划 |
| 7 | 自举：用 z42 重写编译器和工具链 | 📋 规划 |

## 关键设计决策

- **IR 是寄存器 SSA 形式**，非栈机，便于 JIT 生成高质量原生代码
- **执行模式注解** `#[exec = interp|jit|aot]` 作用于模块级别，VM 按注解分发
- **无 null**，使用 `T?`（Option）和 `T!`（Result）代替
- **所有权 + Region 内存模型**，无 GC

## 规范文件

修改任何语言行为前，**必须先更新对应的 spec 文件**：
- 新语法 → `specs/language-overview.md`
- 新 IR 指令 → `specs/ir.md`

## 代码风格

### C#
- 使用 C# 12+ 特性（primary constructors、record types、集合字面量）
- AST 节点用 `sealed record`，保持不可变
- 错误用异常（`ParseException`），不用返回码
- 命名空间：`Z42.Compiler.Lexer`、`Z42.Compiler.Parser` 等

### Rust
- 使用 `anyhow::Result` 处理可恢复错误，`thiserror` 定义领域错误类型
- 不使用 `unwrap()`，用 `?` 传播
- 公开类型加 `#[derive(Debug)]`，序列化类型加 `Serialize, Deserialize`

## 注意事项

- `.z42bc` 文件的 magic bytes 固定为 `[0x5A, 0x34, 0x32, 0x00]`（"Z42\0"）
- 在语言自举完成前，**不要**把编译器代码改写成 z42
- JIT/AOT 后端暂为存根，不要填充实现直到解释器通过全部测试

@specs/language-overview.md
@specs/ir.md
