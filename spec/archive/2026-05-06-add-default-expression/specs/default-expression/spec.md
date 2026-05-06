# Spec: Default Expression

## ADDED Requirements

### Requirement: `default(T)` produces the zero / null value of T

#### Scenario: Numeric primitives default to 0
- **WHEN** `default(int)` / `default(long)` / `default(double)` / `default(byte)` etc. evaluate
- **THEN** result is `0` (or `0.0` for floating types)；type of expression is the primitive itself

#### Scenario: bool defaults to false
- **WHEN** `default(bool)` evaluates
- **THEN** result is `false`

#### Scenario: char defaults to '\0'
- **WHEN** `default(char)` evaluates
- **THEN** result is `'\0'`（z42 char tag → `Value::Char('\0')` at runtime）

#### Scenario: string defaults to null
- **WHEN** `default(string)` evaluates
- **THEN** result is `null`；用户可用 `?? ""` 选 empty 语义

#### Scenario: class / interface / array / nullable types default to null
- **WHEN** `default(MyClass)` / `default(IComparable<int>)` / `default(int[])` / `default(int?)` 等
- **THEN** result is `null`；与 z42 既有 ObjNew zero-fill 行为一致

#### Scenario: default(T) usable in expression context
- **WHEN** `var x = default(int) + 1;` 或 `Console.WriteLine(default(string) ?? "<null>");`
- **THEN** 表达式按 T 推断后参与外层运算 / 类型检查（int + 1 = 1；string ?? "<null>" = "<null>"）

### Requirement: Compile-time type validation (E0421)

#### Scenario: Unknown type rejected
- **WHEN** `default(NoSuchType)` 解析时，TypeExpr 解析失败 / 名称在符号表 / imports 中找不到
- **THEN** 报 `E0421 InvalidDefaultType: type 'NoSuchType' not found`

#### Scenario: Generic type-parameter rejected (Phase 2 deferred)
- **WHEN** `default(R)` 出现在泛型类 `Foo<R>` / 泛型方法 `void m<R>(...)` 上下文中，R 是 type-param
- **THEN** 报 `E0421 InvalidDefaultType: default(<T>) on generic type parameter is not yet supported (D-8b-3 Phase 2 deferred)`；不阻塞同 CU 其它编译

#### Scenario: Type expression syntactically valid but unresolvable
- **WHEN** `default(MyClass<NoSuchType>)` — outer 已知 inner 未知
- **THEN** 报 `E0421` 指向不可解析的内部类型

### Requirement: Parser disambiguates `default` keyword vs expression vs label

#### Scenario: `default :` label inside switch
- **WHEN** `switch (x) { default: ... }`
- **THEN** parser 走 switch label 路径（既有行为），不进 expr `default(T)`

#### Scenario: `default(T)` as statement-level expression
- **WHEN** `var x = default(int);`
- **THEN** parser 走 expr 路径，产出 DefaultExpr

#### Scenario: `default` 不带括号 or 缺 type
- **WHEN** 位于非 switch label 上下文写 `default` 或 `default()`
- **THEN** parser 报 syntax error（缺 `(` 或缺 TypeExpr）

## MODIFIED Requirements

### Requirement: AST adds DefaultExpr node

**Before**: AST 无 default expression 节点；用户写 `default(T)` parser 进入 ident 解析路径，要么把 default 误识别为 ident（实际 lexer 把它识别为 Keyword Default），要么报错 — 整体不工作。

**After**: `Ast.cs` 新增

```csharp
public sealed record DefaultExpr(TypeExpr Target, Span Span) : Expr(Span);
```

ExprParser nud 表注册 `TokenKind.Default`：
- 期望 `(` → 解析 TypeExpr → 期望 `)` → 产出 `DefaultExpr(typeExpr, span)`
- 不存在 `(` → fail（让 switch label 路径在 stmt 上下文吃 default）

### Requirement: BoundExpr adds BoundDefault node

**Before**: 无对应 Bound 节点。

**After**: `BoundExpr.cs` 新增

```csharp
public sealed record BoundDefault(Z42Type Type, Span Span) : BoundExpr(Span)
{
    public override Z42Type Type => base.Type;  // ← record 模式下 by primary param
}
```

TypeChecker.Exprs.cs `case DefaultExpr de`：
1. 解析 `de.Target` → `Z42Type`
2. 校验：T 不是 `Z42GenericParamType`（否则 E0421 generic case）；T 不是 `Z42ErrorType`（否则 E0421 unknown type）
3. 返回 `BoundDefault(t, de.Span)` 类型为 T

### Requirement: IrGen emits primitive Const for `default(T)`

**Before**: `BoundDefault` 不存在；codegen 无对应分支。

**After**: `FunctionEmitterExprs.cs` `case BoundDefault bd`：

```
Z42PrimType("int" | "long" | numeric aliases) → ConstI64(reg, 0)
Z42PrimType("double" | "float")               → ConstF64(reg, 0.0)
Z42PrimType("bool")                           → ConstBool(reg, false)
Z42PrimType("char")                           → ConstChar(reg, '\0')
其他（string / class / interface / array / nullable）→ ConstNull(reg)
```

reg 由 Alloc 按 IrType 分配（int/long → IrType.I64；double → IrType.F64；其它 → IrType.Ref）。

`int` / `i32` / `byte` 等 32-bit 别名一律 emit `ConstI64(0)` — 与 z42 现有 numeric promote 行为一致（i32 在 IR 用 i64 register 持有）。

## Pipeline Steps

- [ ] Lexer — 无变化（`default` 早是 Keyword Default）
- [ ] Parser / AST — 加 `DefaultExpr` + ExprParser nud 注册
- [ ] TypeChecker — `case DefaultExpr` + BoundDefault 节点 + E0421
- [ ] IR Codegen — `case BoundDefault` 按 type 分发现有 Const*
- [ ] VM interp / JIT — 无变化（既有 Const* 已足够）
