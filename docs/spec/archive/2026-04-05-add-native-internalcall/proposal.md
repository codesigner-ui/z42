# Proposal: add-native-internalcall

## Why

stdlib 源文件（`src/libraries/`）里的方法已用 `[Native("__xxx")]` 标注，但编译器的
`SkipAttribute()` 直接丢弃属性，导致 stdlib 无法被编译成有效 `.zpkg`（方法体为空），
`artifacts/z42/libs/` 里全是 0 字节占位文件，VM 能发现 libs 目录但什么都不加载。

本变更实现 **InternalCall 语义**：stdlib 方法在编译器侧声明为 `extern`，Codegen 注入
单条 `Builtin` 指令作为方法体；VM 侧用 HashMap 分发到 Rust 实现；VM 启动时自动加载
`z42.core.zpkg`，并按 zpkg 的 `dependencies` 字段加载其他 stdlib 包。

## What Changes

| 层 | 变更点 |
|----|--------|
| Lexer | 新增 `extern` 关键字（`TokenKind.Extern`） |
| Parser | `[Native("__name")]` → `NativeAttribute` 保留到方法；`extern` modifier → `IsExtern=true`，无 body，分号结尾 |
| AST | `FunctionDecl` 增加 `IsExtern: bool` + `NativeIntrinsic: string?` |
| NativeTable | 新建 `z42.IR/NativeTable.cs`：`name → (ParamCount, ReturnType)` 注册表 |
| TypeChecker | extern 方法验证：intrinsic name 存在于 NativeTable，参数数量匹配 |
| Codegen | extern 方法体注入 `Builtin { name, args:[%0..%n] }` + `Ret` |
| VM builtins | `exec_builtin` 改为 HashMap dispatch（启动时构建，O(1) 查） |
| VM startup | 自动加载 `z42.core.zpkg`；读用户 artifact 的 `dependencies` 字段逐个加载 |
| 脚本 | 新增 `scripts/build-stdlib.sh`：编译所有 `src/libraries/*` → `artifacts/z42/libs/*.zpkg` |

## Scope

| 文件/模块 | 变更类型 |
|-----------|---------|
| `src/compiler/z42.Compiler/Lexer/TokenDefs.cs` | 新增 `extern` 关键字 |
| `src/compiler/z42.Compiler/Parser/Ast.cs` | FunctionDecl 新增字段 |
| `src/compiler/z42.Compiler/Parser/TopLevelParser.cs` | ParseAttribute + extern 分支 |
| `src/compiler/z42.IR/NativeTable.cs` | 新建 |
| `src/compiler/z42.Compiler/TypeCheck/TypeChecker.cs` | extern 方法验证 |
| `src/compiler/z42.Compiler/Codegen/IrGen.cs` | extern 方法 codegen |
| `src/compiler/z42.Compiler/Diagnostics/DiagnosticCatalog.cs` | 新增 Z0090/Z0091 |
| `src/runtime/src/interp/builtins.rs` | HashMap dispatch 重构 |
| `src/runtime/src/interp/mod.rs` | 调用 HashMap dispatch |
| `src/runtime/src/main.rs` | stdlib 自动加载 |
| `src/libraries/**/*.z42` | 所有 `[Native]` 方法加 `extern` 关键字 |
| `scripts/build-stdlib.sh` | 新建 |

## Out of Scope

- External FFI（P/Invoke、unmanaged function pointer 调用外部 C 库）
- Builtin integer ID 优化（留给 JIT 阶段）
- TypeChecker 跨包加载外部 zpkg 符号表（Phase 4.5，已知延迟）
- stdlib 方法参数的精确类型检查（当前 NativeTable 做 param count 检查）
