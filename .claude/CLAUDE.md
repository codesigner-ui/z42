# z42 — Claude 工作手册

## 项目简介

z42 是一门融合 C#、Rust、Python 优点的系统编程语言。
- **编译器 / 工具链**：C#（Bootstrap），最终自举为 z42
- **虚拟机**：Rust，支持 Interpreter / JIT / AOT 混合执行
- **详细设计**：见 `docs/design/`

## 代码库结构

```
z42/
├── CLAUDE.md
├── docs/design/                    # 语言规范
│   ├── language-overview.md
│   ├── ir.md                 # SSA IR 指令集
│   ├── compilation.md        # 编译产物 .zbc/.zmod/.zlib
│   ├── project.md            # 工程文件 z42.toml（[project] / [workspace]）
│   ├── arrays.md             # 数组语法与 IR 映射
│   ├── foreach.md            # foreach 语句与 break/continue
│   ├── compound-assign.md    # 复合赋值运算符
│   ├── string-builtins.md    # 字符串内置方法
│   └── hot-reload.md         # 热更新（游戏脚本场景）
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
| 专有扩展 | `[ExecMode(Mode.Jit)]` 执行模式注解；`[HotReload]` 热更新注解 |

### Phase 2 — 吸收 Rust / Python 优点（完成基础实现后引入）

z42 **始终带 GC**，不引入所有权/借用。Phase 2 目标是借鉴 Rust/Python 的**语法和类型系统**优点：

| 特性 | Phase 2 方向 |
|------|-------------|
| 错误处理 | `Result<T, E>` + `?` 运算符作为 `try/catch` 的替代 |
| 接口 → Trait | 泛型约束 `where T : Trait`，零开销静态分发 |
| 枚举 | 代数数据类型（sum type）：`enum Option<T> { Some(T), None }` |
| 模式匹配 | `match` 穷尽检查，替代 `switch` |
| 空安全 | `Option<T>` 消除 null，或保留 `T?` + 编译期检查 |
| Python 风格 | 单文件脚本执行、内置 `eval`、交互式 REPL |

### 固定不变的决策

- **IR 是寄存器 SSA 形式**，非栈机，便于 JIT 生成高质量原生代码
- **执行模式注解**作用于命名空间级别，VM 按注解分发
- `.z42bc` magic bytes 固定为 `[0x5A, 0x34, 0x32, 0x00]`（"Z42\0"）

## 改动验证流程（必须遵守）

**每次完成一批改动后，必须按以下顺序自动执行，不需要用户提醒：**

```bash
# 1. 编译（确保无错误）
dotnet build src/compiler/z42.slnx

# 2. 跑 golden tests（确保全部通过）
dotnet test tests/z42.Tests/z42.Tests.csproj

# 3. 提交（tests 全通过后才能 commit）
git add <changed files>
git commit -m "..."

# 4. 推送
git push origin main
```

- **禁止** 在测试失败时 commit 或 push
- 每个逻辑完整的改动单元提交一次，不要积压多个无关改动
- commit message 格式：`type(scope): 简要描述`（如 `feat(parser): ...`、`fix(ir): ...`）

## 新语法/特性开发流程（必须遵守）

**任何新语法或语言特性，必须按以下流程推进，不得跳越：**

1. **起草规范**：在 `docs/design/` 下新建或更新独立的规范文档，描述语法、语义、IR 映射
2. **讨论确认**：与用户讨论规范内容，取得明确确认（"可以"/"OK"/"开始实现"等）
3. **实现**：规范确认后才开始编写编译器/VM 代码
4. **验证**：按"改动验证流程"编译 + 测试通过后提交

- **禁止** 在规范未确认时提前写实现代码
- 若发现实现中需要偏离已确认规范，必须重新走步骤 1–2
- **持续关注可维护性**：实现应易于扩展和维护；发现设计问题（耦合过紧、职责不清、扩展困难）时，主动提出并进行重构，不要为了赶进度积累技术债

## 规范文件

修改任何语言行为前，**必须先更新对应的 spec 文件**：
- 新语法 → `docs/design/language-overview.md`
- 新 IR 指令 → `docs/design/ir.md`
- 新语言特性 → `docs/design/<feature>.md`（独立文件）

## 文档同步（必须遵守）

**每次完成一批改动后，检查以下各项，避免规范与实现脱节：**

| 改动类型 | 需要更新的文档 |
|----------|--------------|
| 新语法 / 语句 | `docs/design/language-overview.md` + 对应 `docs/design/<feature>.md` |
| 新 IR 指令 | `docs/design/ir.md` |
| 新 VM 行为 / 内置函数 | 对应 `docs/design/<feature>.md` |
| 新构建步骤 / CLI 参数 | `CLAUDE.md` 的"构建命令"部分 |
| 新 Phase 1 特性 | `CLAUDE.md` 的"语言设计策略"表格 |
| 规范设计变更 | 同步更新所有引用该设计的文档 |

- **禁止**改动已完成、测试通过后跳过文档更新步骤
- 若实现与规范发生偏差，**以实现为准更新规范**，不得让规范描述不存在的行为

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
| 文件监听 | `notify` | 热更新文件系统事件，跨平台，支持 debounce |

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

@docs/design/language-overview.md
@docs/design/ir.md
