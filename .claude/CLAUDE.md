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
├── specs/                    # 语言规范
│   ├── language-overview.md
│   ├── ir.md                 # SSA IR 指令集
│   ├── compilation.md        # 编译产物 .zbc/.zmod/.zlib
│   └── project.md            # 工程文件 z42.toml（[project] / [workspace]）
├── examples/                 # .z42 示例源文件 + z42.toml
└── src/
    ├── compiler/             # C# Bootstrap 编译器 (.NET 10)
    │   ├── z42.IR/           # IR + 工程数据类型（纯数据，无逻辑）
    │   │   ├── IrModule.cs       # SSA IR 数据模型
    │   │   ├── PackageTypes.cs   # ZbcFile / ZmodManifest / ZlibFile
    │   │   └── ProjectTypes.cs   # Z42Proj / Z42Sln（z42.toml 模型）
    │   ├── z42.Compiler/
    │   │   ├── Lexer/        # Lexer.cs, Token.cs, TokenKind.cs
    │   │   ├── Parser/       # Parser.cs, Ast.cs
    │   │   ├── TypeCheck/    # 类型检查器（待实现）
    │   │   └── Codegen/      # IrGen.cs — AST → IrModule
    │   └── z42.Driver/       # CLI 入口 z42c（--emit ir|zbc|zmod|zlib）
    └── runtime/              # Rust VM
        └── src/
            ├── bytecode.rs   # Module / Instruction / Terminator
            ├── package.rs    # ZbcFile / ZmodManifest / ZlibFile
            ├── project.rs    # Z42Proj / Z42Sln（z42.toml 模型）
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
dotnet run --project src/compiler/z42.Driver -- <file.z42> [--dump-tokens]

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

## 语言设计策略（两阶段，不要跳越）

### Phase 1 — C# 语法（当前）

语法**完全对齐 C# 9–12**，目标是尽快跑通完整 pipeline。

| 特性 | Phase 1 写法 |
|------|-------------|
| 命名空间 | `namespace Foo;` |
| 导入 | `using System;` |
| 类型 | `int`, `long`, `double`, `string`, `bool` |
| 推断 | `var x = 42;` |
| 类/结构体 | `class`, `struct`, `record`, `record struct` |
| 接口 | `interface` |
| 模式匹配 | `switch` 表达式（C# 8+ 风格） |
| 错误处理 | `try/catch/throw` |
| 异步 | `async Task<T>` + `await` |
| 专有扩展 | `[ExecMode(Mode.Jit)]` 执行模式注解 |

### Phase 2 — Rust 改进（完成基础实现后引入）

| 特性 | Phase 2 方向 |
|------|-------------|
| 内存 | 所有权 + 借用，`mut` 显式可变，无 GC |
| 错误处理 | `Result<T, E>` + `?` 运算符替换异常 |
| 接口 → Trait | 零开销静态分发 |
| 枚举 | 真正的代数数据类型（sum type） |
| 模式匹配 | `match` 穷尽检查替换 `switch` |
| 空安全 | `Option<T>` 编译期检查，消除 null |

### 固定不变的决策

- **IR 是寄存器 SSA 形式**，非栈机，便于 JIT 生成高质量原生代码
- **执行模式注解**作用于命名空间级别，VM 按注解分发
- `.z42bc` magic bytes 固定为 `[0x5A, 0x34, 0x32, 0x00]`（"Z42\0"）

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

## 充分利用开源库，不重复造轮子

**核心原则：优先寻找成熟开源库，只在无合适库或需要深度定制时才自行实现。**

### 编译器（C#）推荐库

| 用途 | 推荐库 | 说明 |
|------|--------|------|
| 命令行解析 | `System.CommandLine` | 官方 CLI 框架 |
| 测试 | `xUnit` + `FluentAssertions` | 单元 / 集成测试 |
| 基准测试 | `BenchmarkDotNet` | 编译器性能回归 |
| 源码生成辅助 | `Roslyn`（只读引用）| 参考 C# AST 结构 |
| 二进制序列化 | `MessagePack-CSharp` | `.z42bc` 读写（比手写快）|
| 日志 | `Microsoft.Extensions.Logging` | 统一日志接口 |

### VM（Rust）推荐库

| 用途 | 推荐库 | 说明 |
|------|--------|------|
| JIT 代码生成 | `cranelift-jit` | Bytecode Alliance，Wasmtime 同款 |
| AOT / LLVM | `inkwell` | LLVM safe bindings for Rust |
| 二进制格式 | `bincode` ✅（已用）| 序列化 `.z42bc` |
| 解析辅助（调试格式）| `nom` 或 `winnow` | 文本 IR（`.z42ir`）解析 |
| GC（未来沙盒模式）| `gc-arena` | arena 式 GC，可选引入 |
| 并发运行时 | `tokio` | async VM task 调度 |
| 性能剖析 | `pprof-rs` | 火焰图 |

### 参考实现与学习资源

在实现某个子系统前，**先调研同类开源项目**：

| 子系统 | 参考项目 |
|--------|---------|
| 解释器结构 | CPython（ceval.c）、wren、lua 5.4 |
| SSA / IR 设计 | LLVM IR、Cranelift CLIF、QBE |
| 类型推导 | OCaml 编译器、Hindley-Milner 论文实现 |
| JIT 流水线 | LuaJIT、V8 Maglev、JavascriptCore |
| 所有权检查（Phase 2）| rustc borrow checker、Midori 语言 |
| 模式匹配编译 | `rustc_mir_build`、MLton |

### 决策流程

```
需要某个功能？
  └─ 搜索 crates.io / NuGet / GitHub
       ├─ 找到活跃、有测试、许可证兼容的库 → 直接引入
       ├─ 找到接近的库但需要定制 → fork 或包装后用
       └─ 没有合适库，或库会引入不可接受的架构耦合 → 自行实现，
          并在 PR / commit 中说明为何不用现有库
```

## 注意事项

- 在语言自举完成前，**不要**把编译器代码改写成 z42
- JIT/AOT 后端暂为存根，不要填充实现直到解释器通过全部测试
- Phase 2 的改动（所有权、Result、match 等）**不要**在 Phase 1 实现阶段提前引入到规范中

@specs/language-overview.md
@specs/ir.md
