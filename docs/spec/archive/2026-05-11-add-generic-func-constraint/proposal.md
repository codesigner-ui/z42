# Proposal: 泛型委托/函数约束（add-generic-func-constraint）

## Why

z42 泛型约束体系当前缺失"可调用约束"。用户想写以下代码无法表达：

```z42
public class EventBus<T> where T: Action<EventArgs> {
    private List<T> handlers = new List<T>();
    public void Fire(EventArgs e) {
        foreach (var h in handlers) h(e);   // 'h(e)' 编译期 reject — T 无 callable 证据
    }
}

public R Pipe<T, R>(T transform, int seed) where T: Func<int, R> {
    return transform(seed);                  // 同上
}
```

Workaround 是强制 `T = Func<int, R>` 字面（失去泛型抽象）或用 `Action<>`/`Func<>` 字段。前者无法跨多签名复用；后者无法表达"T 必须可调用且具体签名"的形式参数约束。

不做的代价：随泛型 HOF / event-bus / pipeline 模式普及，每个调用方需要手写非泛型 wrapper。

## What Changes

新增**第 8 种约束类别 —— 函数签名约束**。

支持两种 surface 语法，二者等价：

| 形式 | 例 |
|------|---|
| **命名形式** | `where T: Func<int, R>` / `where T: Action<EventArgs>` / `where T: Predicate<int>` |
| **字面量形式** | `where T: (int) -> R` / `where T: (EventArgs) -> void` / `where T: (int) -> bool` |

约束检查语义：T 的类型参数实例必须满足 `T <: ConstraintFuncType`，其中 `<:` 走既有 `Z42FuncType.AssignableTo`（参数逆变 / 返回协变）。

Body 内可直接 `t(args)` 调用，desugar 为 `CallIndirect`（与闭包同 IR 路径）。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Semantics/TypeCheck/Z42Type.cs` | MODIFY | `GenericConstraintBundle` 加 `FuncSignature: Z42FuncType?` 字段 |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.GenericResolve.cs` | MODIFY | `ResolveWhereConstraints()` 识别 `Z42FuncType` 形式的 constraint expr |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Generics.cs` | MODIFY | `ValidateGenericConstraints()` 加 `FuncSignature` 分支，E0408 触发 |
| `src/compiler/z42.Syntax/Parser/TypeParser.cs` | MODIFY | `where` constraint 位置接受 `(T) -> R` 字面量（已支持名称 `Func<>`/`Action<>`/`Predicate<>` 走 generic instantiation 路径，无需改）|
| `src/compiler/z42.Semantics/Codegen/IrGen.cs` | MODIFY | EmitConstraintBundle 发射 FuncSignature |
| `src/compiler/z42.IR/IrModule.cs` | MODIFY | `IrConstraintBundle` 加 `FuncSignature` 字段 |
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs` | MODIFY | SIGS section flag 0x20 + 参数 type-tag 列表 + 返回 type-tag |
| `src/compiler/z42.IR/BinaryFormat/ZbcReader.cs` | MODIFY | 对称读取；zbc minor 0.5 → 0.6 bump |
| `src/compiler/z42.Project/ZpkgWriter.cs` | MODIFY | inner zbc minor bump + outer zpkg minor bump（与既有 split-debug-symbols 同步规则）|
| `src/compiler/z42.Project/ZpkgReader.cs` | MODIFY | 同上 |
| `src/compiler/z42.Core/Diagnostics/DiagnosticCatalog.cs` | MODIFY | 新增 E0408 GenericFuncConstraintViolation + E0409 InvalidFuncConstraint |
| `src/runtime/src/metadata/types.rs` | MODIFY | `TypeParamConstraint` struct 加 `func_signature: Option<FuncSigDescriptor>` |
| `src/runtime/src/metadata/zbc_reader.rs` | MODIFY | 解码 flag 0x20 + per-param type tag |
| `src/runtime/src/metadata/loader.rs` | MODIFY | `verify_constraints()` 验证 func signature 内的 class/interface 类型引用存在 |
| `src/compiler/z42.Tests/GenericFuncConstraintTests.cs` | NEW | C# xUnit：解析 / 约束校验 / variance 单元 |
| `src/tests/generics/func_constraint_basic/source.z42` | NEW | golden test：基础 Func 约束调用 |
| `src/tests/generics/func_constraint_action/source.z42` | NEW | golden test：Action 约束 + void return |
| `src/tests/generics/func_constraint_variance/source.z42` | NEW | golden test：参数逆变 / 返回协变 |
| `src/tests/generics/func_constraint_literal/source.z42` | NEW | golden test：`(T) -> R` 字面量形式 |
| `src/tests/errors/E0408_func_constraint_violation/source.z42` | NEW | error golden：签名不匹配 |
| `docs/design/language/generics.md` | MODIFY | 新增 §"委托/函数约束"小节 + L3-G2.5 表更新 |
| `docs/roadmap.md` | MODIFY | Deferred Backlog Index "委托/函数约束" 条目移除（落地）|
| `docs/error-codes/Z.json` | — | 不动（E#### 是编译器空间，不在 Z.json）|

**只读引用**（不修改）：

- `src/compiler/z42.Semantics/TypeCheck/SymbolCollector.Classes.cs` — 理解既有 interface 约束 collect 流程
- `docs/design/language/generics.md` "约束体系"段 — 既有约束语义
- `docs/design/language/delegates-events.md` — Z42FuncType / delegate 类型关系
- `docs/spec/archive/2026-04-22-add-generics-g3a-vm-metadata/` — 既有约束元数据 zbc 写入流程

## Out of Scope

明确**不**做：

1. **`where T: Callable` 无 signature 结构性约束** —— 依赖 existential type，复杂度过高
2. **multicast 类型作为 func 约束** —— `MulticastAction<T>` 是 `Z42ClassType`，自动走 baseclass/interface 路径（无需特殊处理）
3. **func 约束 + 其他约束组合**（如 `where T: Func<int,int> + ICloneable`）—— 语义复杂，v1 仅纯 func 约束；约束合取留给 v2
4. **ref/out/in 参数修饰符在约束中的处理** —— v1 严格匹配（callee `(ref int)->void` 不可满足约束 `(int)->void`）；后续 spec 讨论是否放宽
5. **反射形式 `T.Invoke(args)`** —— 依赖 L3-R 反射体系；v1 仅 body 内直接 `t(args)` 调用
6. **Optional / variadic 参数在约束 signature 中的语义** —— v1 仅固定参数列表

## Open Questions

无 —— 4 个设计决策（D1-D4）已由 User 在 proposal 前确认（双语法 / 结构性 variance / zbc 0.5→0.6 / CallIndirect 复用）。
