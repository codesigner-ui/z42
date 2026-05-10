# Proposal: catch-by-generic-type — 异常 catch 子句按声明类型过滤

## Why

z42 当前所有 `catch (T e) { ... }` 子句**都按 wildcard 处理**（在 [FunctionEmitterStmts.cs:439](src/compiler/z42.Semantics/Codegen/FunctionEmitterStmts.cs#L439) 注释中明确："ExceptionType is not stored in BoundCatchClause — use wildcard catch"）。这是一个**语义正确性 bug**：

```z42
try {
    throw new IOException("disk full");
}
catch (NullReferenceException e) {  // ← 当前会捕获 IOException！
    Console.WriteLine("nullref: " + e.message);
}
catch (Exception e) {                 // ← 永远到不了
    Console.WriteLine("other: " + e.message);
}
```

用户在写 catch 时类型断言被 z42 默认违反，写了"看起来在过滤"的代码却根本没生效。这破坏 C#/Java/Rust 等语言的核心异常处理范式，也阻塞 D-8b-1 `MulticastException<R>` —— 即便加了 generic exception 类，用户也无法 `catch (MulticastException<int> e)` 单独处理。

更糟的是：现有 stdlib 提供了 `Std.Exception` + 9 个标准子类（`ArgumentException` / `InvalidOperationException` / `IOException` / ...）以及自定义 exception class 机制，**给了用户写类型化 catch 的工具，但 catch 后端永远不过滤**——形态对、语义错。

来源：[docs/deferred.md](docs/deferred.md) D-8b-2，由 D-8b 探索（2026-05-04）发现。

## What Changes

- 编译器把 catch clause 的声明异常类型一路保留到 IR，停止丢弃
- 校验：catch 声明的类型必须是已知 class 且继承 `Std.Exception`（或就是 Exception 自己）；否则 E0420 InvalidCatchType
- VM `find_handler` 在选 handler 时按 `catch_type` 过滤：thrown value 的运行时 class 必须 instance-of catch_type（含子类匹配，复用 [exec_instr.rs:601](src/runtime/src/interp/exec_instr.rs#L601) `is_subclass_or_eq_td`）
- JIT `translate.rs::translate_throw` 同步加 catch_type 过滤
- 多个 catch 子句按**源代码顺序**第一个匹配者胜出（C# / Java 标准）
- 缺类型的 `catch { }` / `catch (e)` 仍是 wildcard（向后兼容现有 exception goldens）

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| [src/compiler/z42.Semantics/Bound/BoundStmt.cs](src/compiler/z42.Semantics/Bound/BoundStmt.cs) | MODIFY | `BoundCatchClause` 增 `string? ExceptionTypeName` 字段（解析后的 FQ 名）|
| [src/compiler/z42.Semantics/TypeCheck/TypeChecker.Stmts.cs](src/compiler/z42.Semantics/TypeCheck/TypeChecker.Stmts.cs) | MODIFY | 解析 `clause.ExceptionType` 字符串到 `Z42Type`，校验是 Exception 子类，写入 BoundCatchClause |
| [src/compiler/z42.Semantics/Codegen/FunctionEmitterStmts.cs](src/compiler/z42.Semantics/Codegen/FunctionEmitterStmts.cs) | MODIFY | 删除 `null` 写入注释 + 改用 `clause.ExceptionTypeName` 作 IrExceptionEntry.CatchType |
| [src/compiler/z42.Core/Diagnostics/Diagnostic.cs](src/compiler/z42.Core/Diagnostics/Diagnostic.cs) | MODIFY | 新增 `E0420 InvalidCatchType` 错误码 |
| [src/compiler/z42.Core/Diagnostics/DiagnosticCatalog.cs](src/compiler/z42.Core/Diagnostics/DiagnosticCatalog.cs) | MODIFY | E0420 catalog 条目 |
| [src/runtime/src/interp/mod.rs](src/runtime/src/interp/mod.rs) | MODIFY | `find_handler` 接受 thrown value，按 catch_type 过滤；旧 callers 适配 |
| [src/runtime/src/jit/translate.rs](src/runtime/src/jit/translate.rs) | MODIFY | JIT throw helper 同步 catch_type 过滤 |
| `src/tests/exceptions/catch_by_type/` | NEW | 单 catch 类型过滤 golden |
| `src/tests/exceptions/catch_by_type_subclass/` | NEW | 子类匹配 golden（catch base 捕获 derived）|
| `src/tests/exceptions/catch_by_type_order/` | NEW | 多 catch 子句源序选择 golden |
| `src/tests/exceptions/catch_wildcard_compat/` | NEW | 无类型 `catch { }` 仍 wildcard golden |
| `src/tests/errors/420_invalid_catch_type/` | NEW | E0420 错误用例 |
| [src/compiler/z42.Tests/CatchByTypeTests.cs](src/compiler/z42.Tests/CatchByTypeTests.cs) | NEW | C# 单元测试覆盖 BoundCatchClause / IrExceptionEntry emission |
| [docs/design/exceptions.md](docs/design/exceptions.md) | MODIFY | 加"catch 类型过滤"章节 |
| [docs/deferred.md](docs/deferred.md) | MODIFY | 移除 D-8b-2 active 条目，登记到"已落地" |

**只读引用**：

- [docs/design/exceptions.md](docs/design/exceptions.md) — Exception 类层次现状
- [src/runtime/src/interp/exec_instr.rs](src/runtime/src/interp/exec_instr.rs) — `is_subclass_or_eq_td` 复用
- [src/compiler/z42.IR/IrModule.cs](src/compiler/z42.IR/IrModule.cs) — `IrExceptionEntry.CatchType` 已有字段（不需要改 IR 元数据格式）
- [src/runtime/src/metadata/bytecode.rs](src/runtime/src/metadata/bytecode.rs) — `ExceptionEntry.catch_type: Option<String>` 已有

## Out of Scope（本变更不做）

- **D-8b-0**（class registry arity-aware）：catch generic 时 `MulticastException<int>` vs `MulticastException` 的 lookup 用 IR 字符串名直接传递；不依赖 class registry 改造。当 D-8b-0 落地后，泛型 catch 的"基于 type-arg 子类匹配"自动 piggyback。
- **D-8b-1** stdlib `MulticastException<R>`：本变更不引入泛型 exception 类，仅修复 catch 过滤机制
- **C# 风格 exception filters** (`catch (Foo e) when (cond)`)：z42 暂不支持，独立 spec 评估
- **多类型 catch** (`catch (A | B e)`)：C# 11 才有，z42 不引入
- **Catch 不带 type 但带 var**（`catch (e)`）：解析到 `ExceptionType=null` 仍 wildcard，与现有行为一致

## Open Questions

无（关键决策已与 User 在 deferred.md 排序中明确：先做 D-8b-2 因为是 bug fix，与 D-8b-0/1 解耦）。
