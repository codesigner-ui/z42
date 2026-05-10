# Proposal: Extend `[Native]` for Tier 1 dispatch (C6)

## Why

C2/C3 让 z42 VM **运行时**支持 Tier 1 C ABI 与 Tier 2 Rust 注册路径，但 z42 用户代码**仍写不出对它们的调用**——`[Native("__name")]` 只能走 L1 stdlib builtin dispatch（emit `BuiltinInstr`）。

C6 扩展 `[Native]` attribute 接受**新的命名参数形式** `[Native(lib="...", type="...", entry="...")]` 让 z42 用户代码能直接 emit `CallNativeInstr` IR 调用 C2 注册的 native 函数。两种形式互斥共存，与现有 L1 stdlib InternalCall 机制零冲突。

举例：

```z42
namespace Demo;

public static class NumZ42 {
    [Native(lib="numz42", type="Counter", entry="__alloc__")]
    public static extern long CounterAlloc();

    [Native(lib="numz42", type="Counter", entry="inc")]
    public static extern long CounterInc(long ptr);

    [Native(lib="numz42", type="Counter", entry="get")]
    public static extern long CounterGet(long ptr);
}

void Main() {
    long ptr = NumZ42.CounterAlloc();
    NumZ42.CounterInc(ptr);
    NumZ42.CounterInc(ptr);
    NumZ42.CounterInc(ptr);
    long n = NumZ42.CounterGet(ptr);  // 3
}
```

> 端到端运行测试需要 VM 启动时预注册 numz42-c —— C6 不引入这条 test harness（独立 spec），**C6 只验证 IR 形状正确**：含 `CallNativeInstr` 而非 `BuiltinInstr`。

## What Changes

- **AST**：`FunctionDecl` 加可选字段 `Tier1NativeBinding? Tier1Binding`；新增 `Tier1NativeBinding(string Lib, string TypeName, string Entry)` record
- **Parser**：`TryReadNativeIntrinsic` 扩展两种形式：
  - 旧 `[Native("__name")]` → string，set `NativeIntrinsic` (L1)
  - 新 `[Native(lib="...", type="...", entry="...")]` → set `Tier1Binding` (L2 C2)
  - 互斥：同一 attribute 不能两种形式混用；不同方法可以各自选
- **TypeChecker**：`extern` 方法允许 `NativeIntrinsic` xor `Tier1Binding`，二选一必须有一个
- **IR Codegen**：`EmitNativeStub` 根据哪个字段非空决定 emit `BuiltinInstr` 还是 `CallNativeInstr`
- **错误码**：复用现有 `ExternRequiresNative` / `NativeRequiresExtern`；新增 `Z0907 NativeAttributeMalformed` 给参数解析失败

## Scope

| 文件路径 | 变更 | 说明 |
|---------|-----|------|
| `src/compiler/z42.Syntax/Parser/Ast.cs` | MODIFY | 加 `Tier1NativeBinding` record + `FunctionDecl.Tier1Binding` 字段 |
| `src/compiler/z42.Syntax/Parser/TopLevelParser.Helpers.cs` | MODIFY | `TryReadNativeIntrinsic` 兼容新旧形式（重命名为 `TryReadNativeAttribute`，返回 union 形式）|
| `src/compiler/z42.Syntax/Parser/TopLevelParser.*.cs` | MODIFY | 调用方对接新返回值（约 2-3 处）|
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.cs` | MODIFY | `hasNative` 检查改为 "L1 binding xor Tier1Binding 存在" |
| `src/compiler/z42.Semantics/Codegen/IrGen.cs` | MODIFY | `EmitNativeStub` 接受可选 `Tier1NativeBinding`；优先 emit `CallNativeInstr` |
| `src/compiler/z42.Tests/NativeAttributeTier1Tests.cs` | NEW | 5–6 unit tests 覆盖 parser / typecheck / codegen 路径 |
| `src/compiler/z42.Core/Diagnostics/Diagnostic.cs` + `DiagnosticCatalog.cs` | MODIFY | E0907 NativeAttributeMalformed 注册 |
| `docs/design/error-codes.md` | MODIFY | Z0907 从占位 → 已启用（C6） |
| `docs/design/interop.md` | MODIFY | §10 加 C6 行 ✅ |
| `docs/roadmap.md` | MODIFY | C6 → ✅ |

**只读引用**：
- `src/runtime/src/native/exports.rs` — Tier 1 ABI 函数（不改，IR 已端到端 ready）
- `src/runtime/src/interp/exec_instr.rs::CallNative` — VM dispatch（不改）
- `tests/data/numz42-c/numz42.c` — 已注册的 Counter 类型作参考

## Out of Scope

- **端到端运行测试** （需要 test harness 在 zbc 启动前预注册 numz42-c）—— 留作单独 spec
- **`extern class T { ... }` 语法** —— 后续 spec
- **`import T from "lib"` + manifest reader** —— 后续 spec
- **`CallNativeVtable` runtime + IR codegen** —— 后续 spec

## Open Questions

- [ ] **Q1**：参数 `type=` 在某些场景可空（自由函数风格 native），需要支持吗？
  - 倾向：**C6 强制三个参数都给**。Tier 1 registry by `(module, type)` index；自由函数留给后续 spec（可能需要 VmContext 加 free-function table）
- [ ] **Q2**：是否允许属性参数顺序无关 / 部分省略？
  - 倾向：必须按 `lib, type, entry` 严格命名（不要求顺序）；遗漏报 E0907
