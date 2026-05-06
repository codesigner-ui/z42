# Design: add-default-expression

## Architecture

```
Source                Parser              TypeChecker             IrGen
─────────             ─────────           ───────────             ─────
default(int)        → DefaultExpr       → BoundDefault          → ConstI64(reg,0)
default(string)       Target=int          Type=Z42PrimType(int)   IrType.I64
                      ↓                   ↓                       ↓
default(MyClass)    → DefaultExpr       → BoundDefault          → ConstNull(reg)
                      Target=string       Type=Z42PrimType(str)   IrType.Ref
                      ↓                   ↓                       ↓
                    → DefaultExpr       → BoundDefault          → ConstNull(reg)
                      Target=MyClass      Type=Z42ClassType       IrType.Ref

E0421 cases (TypeChecker rejects, no IR emitted):
  default(NoSuchType)         → 解析返回 Z42ErrorType  → E0421 type not found
  default(R) in Foo<R>        → 解析返回 Z42GenericParamType → E0421 generic deferred

VM: 无变化（既有 Const* 已是 zero-init 真相源 — `default_value_for(type_tag)` 函数）
```

## Decisions

### Decision 1: 是否新增 `DefaultOf(typeId)` IR 指令

**问题**：`default(T)` 是否值得新 IR opcode？

**选项**：
- A. **新指令** `DefaultOf(dst, type_tag_str)` — VM 调 `default_value_for(type_tag)` 得 Value
- B. **现有 Const* 复用** — IrGen 按 T 分发 ConstI64(0) / ConstF64(0.0) / ConstNull / ...

**决定**：选 **B**。理由：

1. z42 已有 6 种 Const* 指令覆盖所有 zero 值（int/long/f64/bool/char/null），加新 opcode 是冗余
2. T 在 codegen 时已知（TypeChecker 已 resolve），无需运行时再算
3. 新 opcode 会让 zbc 格式 bump（兼容性代价），收益接近 0
4. JIT / interp / disasm / verifier 全要补 case，工作量翻倍

唯一新 opcode 有价值的场景是 **generic type-param `default(T)`** —— 那是 Phase 2 范围，独立 spec 处理；引入时再看是否新 op 还是用 monomorphize 时编译期展开。

### Decision 2: `int` / `i32` 等 32-bit 别名 emit ConstI32 还是 ConstI64

**问题**：z42 numeric 别名（`int = i32`, `byte = u8` 等）在 IR 持有的 register 类型是什么？

**决定**：emit `ConstI64(0)` 配 `IrType.I64`。理由：

z42 现有 numeric literal codegen 已经把所有整型常量统一 promote 到 i64 reg（VM `Value::I64` 是唯一整型 value 变体）。`default(int)` 与 `1`（也是 i64-load）应当同源。

`ConstI32` opcode 实际只在某些 narrow-store 场景用，普通 expression 一律走 ConstI64。

未来若引入真正的 i32 reg type / value variant，本规则相应调整 — 那是独立的 IR-numeric-precision spec，不在本变更范围。

### Decision 3: `default(string)` 返回 null 还是 ""

**问题**：z42 string 是引用类型，`default(string)` 应是 null（reference default）还是 empty string ""（user-friendly default）？

**选项**：
- A. **null** — 与 C# `default(string)` 一致；与 z42 reference 类型 zero-init 一致
- B. **""** — 与 string 语义"无文字"对齐；避免 NullRef 风险

**决定**：选 **A**（null）。理由：

1. C# / Java / Kotlin 通用语义：`default(<reference-type>)` = null
2. z42 既有的 `default_value_for("string")` → `Value::Null`（VM 端真相源）
3. user 想要 "" 可显式 `default(string) ?? ""`，明确意图
4. 选 B 会让 string 在 z42 type system 中"非典型"，复杂化未来 nullable 处理

文档 `language-overview.md` 在 `default(T)` 段写明这一规则，避免用户混淆。

### Decision 4: Parser 与 switch `default:` label 的歧义消解

**问题**：`default` 是已存在的 keyword，用作 switch case label。引入 `default(T)` 表达式后语法可能歧义？

**分析**：
- switch label 上下文：parser 在 `case <expr> :` / `default :` 形态下工作；StmtParser 内 `case TokenKind.Default` 直接消费 `default :`，不进入 expr 解析
- expr 上下文：parser 走 ExprParser；nud 表注册 `TokenKind.Default`，期望紧随 `(`

**决定**：上下文消解 — 在 switch label 解析路径，default 在 ExprParser 之外被独占消费；其它任何 expr 起始位置遇到 `default` 都进入 ExprParser nud → expect `(`。

无歧义因为：switch case 体一定是 statement，statement 内的 `default(T)` 总是出现在表达式位置（赋值 RHS / 函数实参 / return 值 / `Console.WriteLine(...)` 实参等），不可能与 `default :` 标签混淆。

### Decision 5: 在 generic class / function 内的 `default(T)` 错误处理

**问题**：用户在 `class Foo<T> { ... default(T) ... }` 写 `default(T)`，TypeChecker 应该如何处理？

**决定**：TypeChecker resolve type expr `T` 得 `Z42GenericParamType`，立即 emit `E0421 InvalidDefaultType` 并把 expression 标为 `Z42ErrorType`，**不阻塞**后续 binding（让用户看完整批 diagnostic）。

错误信息明确指向 deferred Phase 2：

```
E0421: default(<T>) on generic type parameter is not yet supported
       (deferred to spec add-default-generic-typeparam — see docs/deferred.md D-8b-3 Phase 2)
```

这样 D-8b-1 实施期遇到 `default(R)` 时立即得到清晰指引，而不是 cryptic "internal compiler error" / 错误 codegen。

### Decision 6: Diagnostic 编码为 E0421 而非新 W (warning)

E0420 是上一变更刚定义的 catch-related 错误码，0421 紧邻保持"异常 / 类型相关"段位连续。default(T) 的失败语义是 **硬错误**（codegen 无法决定 emit 什么），warning 没意义。

## Implementation Notes

### TypeChecker 解析 default(T)

```csharp
// In TypeChecker.Exprs.cs case DefaultExpr de:
var t = ResolveTypeExpr(de.Target);
if (t is Z42ErrorType)
{
    // ResolveTypeExpr 已 emit type-not-found diagnostic，不重复
    return new BoundDefault(t, de.Span);
}
if (t is Z42GenericParamType gp)
{
    _diags.Error(DiagnosticCodes.InvalidDefaultType,
        $"default(<{gp.Name}>) on generic type parameter is not yet supported " +
        "(deferred to spec add-default-generic-typeparam)",
        de.Span);
    return new BoundDefault(Z42Type.Error, de.Span);
}
return new BoundDefault(t, de.Span);
```

`ResolveTypeExpr` 已是 TypeChecker 通用 type expression 解析器，复用即可。

### IrGen emit 分发

```csharp
// In FunctionEmitterExprs.cs case BoundDefault bd:
var t = bd.Type;

if (t is Z42PrimType pt)
{
    switch (pt.Name)
    {
        case "double" or "float":
        {
            var dst = Alloc(IrType.F64);
            Emit(new ConstF64Instr(dst, 0.0));
            return dst;
        }
        case "bool":
        {
            var dst = Alloc(IrType.Bool);
            Emit(new ConstBoolInstr(dst, false));
            return dst;
        }
        case "char":
        {
            var dst = Alloc(IrType.Char);
            Emit(new ConstCharInstr(dst, '\0'));
            return dst;
        }
        case "string":
        {
            var dst = Alloc(IrType.Ref);
            Emit(new ConstNullInstr(dst));
            return dst;
        }
        default:
            // numeric (int, long, byte, etc) → i64 register
            if (IsNumericPrim(pt.Name))
            {
                var dst = Alloc(IrType.I64);
                Emit(new ConstI64Instr(dst, 0));
                return dst;
            }
            break;
    }
}

// All non-prim (class, interface, array, nullable, struct) → null
{
    var dst = Alloc(IrType.Ref);
    Emit(new ConstNullInstr(dst));
    return dst;
}
```

`IsNumericPrim` 是 z42 现有的一组 helper（在 `Z42Type` 或 `IrTypeHelpers`）；查名字是否在 numeric whitelist 里。

### Parser nud 注册

```csharp
// In ExprParser.cs init nud table:
NudTable[(int)TokenKind.Default] = (cursor, feat) =>
{
    var startSpan = cursor.Current.Span;
    cursor = cursor.Advance(); // consume 'default'
    Expect(ref cursor, TokenKind.LParen);
    var typeExpr = TypeParser.TypeExpr(cursor).Unwrap(ref cursor);
    Expect(ref cursor, TokenKind.RParen);
    return ParseResult<Expr>.Ok(
        new DefaultExpr(typeExpr, startSpan.Merge(cursor.Previous.Span)),
        cursor);
};
```

binding power 不重要（nud 只在 expression 起始位置触发；后续 led 比较运算照常）。

### 错误码 E0421 catalog 文案

```csharp
[DiagnosticCodes.InvalidDefaultType] = new(
    "Invalid type in default(T) expression",
    "The type argument to `default(T)` must be a fully-resolved type known at " +
    "compile time. Generic type parameters are not yet supported (Phase 2; see " +
    "docs/deferred.md D-8b-3). Unknown type names produce a regular type-not-found " +
    "diagnostic in addition to E0421.",
    "default(NoSuchType)  // E0421: type 'NoSuchType' not found\n" +
    "class Foo<R> { R make() { return default(R); } }  // E0421: generic type-parameter deferred\n" +
    "default(int)  // ok, evaluates to 0\n" +
    "default(string)  // ok, evaluates to null"),
```

## Testing Strategy

- **Golden run tests** (`src/tests/operators/`):
  - `default_primitives/` — `default(int) + 1`, `default(double) + 0.5`, `default(bool)` printed, `default(char)` length, `default(byte)` arithmetic
  - `default_string/` — `default(string) == null`, `default(string) ?? "<null>"` printed
  - `default_class/` — 自定义 class，`default(MyClass)` is null, null-conditional `?.` 访问
  - `default_array/` — `default(int[])` is null；与 `new int[N]` 区分

- **Golden error tests** (`src/tests/errors/`):
  - `421_invalid_default_type/` — generic type-param + 未知类型两种触发

- **C# 单测** (`src/compiler/z42.Tests/DefaultExpressionTests.cs`):
  - Parser: `default(int)` → DefaultExpr.Target 是 NamedType("int")
  - Parser: `default(MyNs.MyClass)` → dotted-path TypeExpr
  - Parser: `default()` → 报 syntax error
  - Parser: `default int` (no parens) → 报 syntax error
  - TypeChecker: `default(int)` → BoundDefault.Type = Z42PrimType("int")
  - TypeChecker: `default(NoSuchType)` → E0421 + Z42ErrorType
  - TypeChecker: 在 generic class 上下文 `default(T)` → E0421 with "generic type parameter"
  - TypeChecker: `default(MyClass)` → BoundDefault.Type = Z42ClassType
  - IrGen: 各 prim 类型 emit 正确 Const*；reference emit ConstNull

- **回归验证**：现有 `switch` 语句 + `default :` 标签所有 goldens 必须 PASS（消解 Decision 4 — switch 测试在 `src/tests/control_flow/{08_switch,56_switch_statement}` 等）

- **VM 不变**：既有 Const* 测试 + interp / jit golden 全 PASS（无新 opcode → VM 0 改动）
