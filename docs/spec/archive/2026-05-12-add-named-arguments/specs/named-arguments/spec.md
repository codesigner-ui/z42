# Spec: Named Arguments

## ADDED Requirements

### Requirement: Parser Recognizes `<ident> : <expr>` as Named Argument

#### Scenario: Simple named argument is parsed
- **WHEN** source contains `Greet(name: "Alice")` at call site
- **THEN** Parser emits `CallExpr` with one `Argument { Name = "name", Value = StringExpr("Alice") }`

#### Scenario: Named argument with `ref` modifier
- **WHEN** source contains `Update(target: ref x)`
- **THEN** Argument carries `Name = "target"`, `Modifier = Ref`, `Value = IdentExpr("x")`

#### Scenario: Named argument with `out var` inline declaration
- **WHEN** source contains `TryParse("42", value: out var result)`
- **THEN** Argument carries `Name = "value"`, `Modifier = Out`, plus inline `OutVarDecl("result")`
- **AND** `result` is in scope after the call (existing rule)

#### Scenario: `<ident>:` only triggers named-arg form at argument start
- **WHEN** source contains `f(a ? b : c)` (ternary expression)
- **THEN** parser treats the entire ternary as a positional `Argument`, not a named one
- **AND** disambiguation: `IDENT :` only triggers named-arg when `IDENT` is the very first token of the argument expression

### Requirement: Positional Arguments Must Precede Named Arguments

#### Scenario: Valid mixed call
- **WHEN** source contains `f(1, name: "x", flag: true)`
- **THEN** type-check succeeds; binding maps positional `1` to param 0, named to their slots

#### Scenario: Positional after named is rejected
- **WHEN** source contains `f(name: "x", 1)`
- **THEN** type-check emits diagnostic `Z0501 PositionalAfterNamed` on the offending positional argument
- **AND** the positional argument's span is the offending range

### Requirement: Named Arguments Bind by Parameter Name

#### Scenario: Out-of-order named arguments
- **WHEN** function `void Draw(string color, int width, bool filled)` is called as `Draw(filled: true, color: "red", width: 2)`
- **THEN** binding produces positional argument list `["red", 2, true]`
- **AND** IrGen emits CallInstr / VCallInstr with that positional order

#### Scenario: Skipping a middle default parameter
- **WHEN** function `void M(int a, int b = 10, int c = 20)` is called as `M(1, c: 30)`
- **THEN** binding produces `[1, 10, 30]` (middle slot filled with default expression)
- **AND** the default value for `b` is emitted from `BoundDefaults[b's param]`

#### Scenario: Unknown parameter name
- **WHEN** call `Draw(unknownParam: 5)` references no such parameter
- **THEN** diagnostic `Z0502 UnknownArgumentName` is emitted on the name token's span

#### Scenario: Duplicate named argument
- **WHEN** call `Draw(color: "red", color: "blue")` repeats a name
- **THEN** diagnostic `Z0503 DuplicateArgumentName` is emitted on the second name token

#### Scenario: Parameter specified twice (positional + named)
- **WHEN** call `Draw("red", color: "blue")` provides both
- **THEN** diagnostic `Z0504 ParameterDoublySpecified` is emitted on the named argument

#### Scenario: Required parameter missing after binding
- **WHEN** call `Draw(width: 2)` for `void Draw(string color, int width)` omits required `color`
- **THEN** diagnostic `Z0505 MissingRequiredArgument` is emitted on the call span

### Requirement: Overload Resolution Considers Named Arguments

#### Scenario: Disambiguate overloads by name set
- **WHEN** overloads `void Open(string path)` and `void Open(int handle)` exist
- **AND** call is `Open(path: "x")`
- **THEN** the first overload is chosen (name `path` matches its parameter)

#### Scenario: Overload with name+type both matching wins
- **WHEN** overloads `void M(int a, string b)` and `void M(int x, string y)` exist
- **AND** call is `M(a: 1, b: "z")`
- **THEN** the first overload is chosen; ambiguity would be flagged as compile error (existing overload-ambiguous diagnostic reused)

#### Scenario: No overload matches the name set
- **WHEN** overload `void M(int a)` exists but call is `M(b: 1)`
- **THEN** diagnostic `Z0502 UnknownArgumentName` is emitted (overload candidate filtered out, no match remains)

### Requirement: Constructor Invocations Support Named Arguments

#### Scenario: Constructor named call
- **WHEN** source contains `new Random(seed: 42)` for class with ctor `Random(long seed, bool threadSafe = false)`
- **THEN** binding produces positional args `[42, false]` (threadSafe filled with default)
- **AND** IrGen emits `ObjNewInstr` with the positional argument list

#### Scenario: Constructor overload resolution with names
- **WHEN** class has two ctor overloads matching arity
- **AND** the call uses named args that match only one
- **THEN** the matching ctor is chosen

### Requirement: Bound Layer Already Positional (Zero IR Impact)

#### Scenario: BoundCall.Args is positional
- **WHEN** TypeChecker emits a `BoundCall` for a call with named args
- **THEN** `BoundCall.Args` is a `List<BoundExpr>` indexed by parameter position (0-based)
- **AND** no name metadata is propagated to IR / zbc
- **AND** `BoundCall.OriginalNamedIndices` (optional debug info) records which positions came from named-form for diagnostics rendering only

#### Scenario: IrGen unaware of named-arg concept
- **WHEN** Codegen processes BoundCall with reordered args
- **THEN** existing `FillDefaults` + `EmitTypeDefault` logic handles trailing or middle-hole defaults exactly as today (positional perspective)
- **AND** no new IR instruction emitted

## MODIFIED Requirements

### Requirement: `CallExpr.Args` Container

**Before:** `CallExpr(Expr Callee, List<Expr> Args, Span Span)` — each arg is a positional expression
**After:** `CallExpr(Expr Callee, List<Argument> Args, Span Span)` where `Argument(string? Name, Expr Value, ArgModifier Modifier, Span Span)`. Existing `ref/out/in` modifiers and `OutVarDecl` inline form preserved via `Modifier` field; positional-only call sites construct `Argument(Name: null, ...)`. ~30 internal `CallExpr` construction call sites (parser-synth event/delegate code paths) mechanically updated.

## IR Mapping

新增 / 修改的二进制要素：

| 名称 | 位置 | 大小 | 说明 |
|------|------|------|------|
| (none) | — | — | named args 在 TypeCheck 阶段已绑定到 param 位置，BoundCall 100% 位置化；IR / zbc / VM 零改动 |

## Pipeline Steps

受影响的 pipeline 阶段：

- [ ] Lexer — 不涉及（`:` token 已存在用于 ternary）
- [x] Parser / AST — 新增 `Argument` 节点；`CallExpr.Args` 类型升级；`IDENT :` lookahead
- [x] TypeChecker — named-arg 绑定算法 + overload candidate 过滤 + 4 个新错误码
- [x] Bound 层 — `BoundCall.Args` 保持按位置形态（不变）；可选 `OriginalNamedIndices` 元数据
- [ ] IR Codegen — 无变化（BoundCall 已是位置形态）
- [ ] VM interp — 无变化
- [ ] JIT — 无变化
- [ ] zbc wire format — 无变化
