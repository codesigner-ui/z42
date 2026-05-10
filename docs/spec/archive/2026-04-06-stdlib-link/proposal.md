# Proposal: stdlib-link

## Why
编译脚本时，对 `Console.WriteLine`、`Assert.Equal` 等的调用由 `BuiltinTable.cs` 和 `NativeTable.cs` 中的硬编码表处理，输出 IR 直接产生 `BuiltinInstr`，而不经过 stdlib。标准库（`z42.io`、`z42.core`、`z42.math` 等）已经完整，但编译器完全绕过它——输出 zpkg 中没有 stdlib 依赖声明，VM 也无法在运行时看到这些符号来自哪个包。

## What Changes
- 编译用户脚本时，加载 stdlib zpkg，提取符号索引（StdlibCallIndex）
- IrGen 将伪类调用（`Console.WriteLine`、`Assert.Equal`、`Math.Abs` 等）和 stdlib 实例方法（`str.Substring`、`list.Add` 等）解析为 `CallInstr("z42.io.Console.WriteLine", ...)` 而非 `BuiltinInstr("__println", ...)`
- 输出 zpkg 自动记录 stdlib 依赖（不依赖用户手写 `using`）
- 删除 `BuiltinTable.cs` 和 `NativeTable.cs`

## Scope（允许改动的文件/模块）
| 文件/模块 | 变更类型 | 说明 |
|-----------|---------|------|
| `z42.IR/NativeTable.cs` | 删除 | 不再需要 |
| `z42.Compiler/TypeCheck/BuiltinTable.cs` | 删除 | 不再需要 |
| `z42.Compiler/TypeCheck/TypeChecker.cs` | 修改 | ValidateNativeMethod 不再查 NativeTable |
| `z42.Compiler/TypeCheck/TypeChecker.Exprs.cs` | 修改 | 移除 BuiltinTable 引用，伪类调用回退 Unknown |
| `z42.Compiler/Codegen/IrGen.cs` | 修改 | 接受 StdlibCallIndex，追踪用到的 stdlib 命名空间 |
| `z42.Compiler/Codegen/IrGenExprs.cs` | 修改 | 伪类静态/实例调用走 StdlibCallIndex → CallInstr |
| `z42.Driver/BuildCommand.cs` | 修改 | 加载 stdlib zpkg，构建 StdlibCallIndex，传入 IrGen/TypeChecker，收集依赖 |
| `z42.Project/StdlibCallIndex.cs` | 新增 | 符号索引类型，从 ZpkgFile 提取 |

## Out of Scope
- TypeChecker 参数类型验证（保持 Unknown 回退，Phase 1 可接受）
- VM interp 侧改动（VM 直接执行 stdlib 中的 BuiltinInstr stub，无需修改）
- JIT / AOT
- `z42.text`、`z42.collections` 完整实例方法分发（歧义方法保留 VCallInstr）
- zpkg 二进制格式变更

## Open Questions
- [x] 歧义实例方法（如 `Contains` 同时存在于 String 和 List）：使用方法名作为键时，静态类方法优先，其余走 VCallInstr 运行时分发
