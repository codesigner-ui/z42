# Spec: Generic Function-Type Constraint

## ADDED Requirements

### Requirement: 命名形式 Func/Action/Predicate 约束

#### Scenario: 基础 Func 约束接受签名匹配的 lambda
- **WHEN** 声明 `T Apply<T, R>(T fn, int x) where T: Func<int, R> { return fn(x); }`，调用 `Apply<Func<int,int>, int>(n => n * 2, 5)`
- **THEN** TypeChecker 通过；IR emit CallIndirect；运行结果为 10

#### Scenario: Action 约束 + void return
- **WHEN** 声明 `void Run<T>(T handler, EventArgs e) where T: Action<EventArgs> { handler(e); }`
- **THEN** TypeChecker 接受 `T = Action<EventArgs>`；运行时 handler 被调用一次

#### Scenario: Predicate 约束
- **WHEN** 声明 `bool Test<T>(T pred, int x) where T: Predicate<int> { return pred(x); }`
- **THEN** 接受 `Predicate<int>` 实例化；返回 pred(x)

#### Scenario: 签名不匹配在调用站点 reject
- **WHEN** 用 `T = Func<string, int>` 实例化 `Apply<T, int>` 约束 `T: Func<int, int>`
- **THEN** 编译器在调用站点报 **E0408** GenericFuncConstraintViolation；错误消息含期望签名 + 实际签名

### Requirement: 字面量形式 `(T) -> R` 约束

#### Scenario: 字面量等价于命名形式
- **WHEN** 声明 `R Apply<T, R>(T fn, int x) where T: (int) -> R { return fn(x); }`
- **THEN** 行为等价于 `where T: Func<int, R>`；同一约束 bundle；同一 IR；同一错误码

#### Scenario: void 字面量约束
- **WHEN** 声明 `void Run<T>(T handler) where T: () -> void`
- **THEN** 接受 `Action`（0-arity） 实例化；reject `Func<>` / non-void return

### Requirement: Variance（参数逆变 / 返回协变）

#### Scenario: 参数逆变接受更宽参数类型
- **WHEN** 约束 `T: Func<Cat, int>`，实例化 `T = Func<Animal, int>`（Cat : Animal）
- **THEN** TypeChecker 通过 —— 因为 `(Animal) -> int` 可接受任意 Cat 参数（Cat <: Animal）

#### Scenario: 返回协变接受更窄返回类型
- **WHEN** 约束 `T: Func<int, Animal>`，实例化 `T = Func<int, Cat>`
- **THEN** TypeChecker 通过 —— 因为返回 Cat 满足 Animal 契约

#### Scenario: 参数协变 reject
- **WHEN** 约束 `T: Func<Animal, int>`，实例化 `T = Func<Cat, int>`
- **THEN** E0408 reject —— 调用方可能传 Animal 但 T 只接 Cat

### Requirement: Body 内调用 desugar

#### Scenario: 直接调用 `t(args)`
- **WHEN** body 中对 `T` 类型参数 `t` 写 `t(arg1, arg2)`
- **THEN** TypeChecker 用约束 signature 推断结果类型；IrGen emit `CallIndirect` 指令（与闭包同 opcode）

#### Scenario: 没有 func 约束时禁止 `t(args)`
- **WHEN** body 中对未带 func 约束的 `T` 写 `t(args)`
- **THEN** E0411 InvalidCall on non-callable type（既有错误码）

### Requirement: zbc 元数据持久化

#### Scenario: zbc 写入 + reader 解码
- **WHEN** 编译器对含 func 约束的泛型函数 emit zbc
- **THEN** SIGS section 该 type_param 的 constraint flags 含 bit 0x20；后跟 param count (u8) + per-param TypeTag + return TypeTag

#### Scenario: zbc 版本升级
- **WHEN** zbc 文件含 func 约束元数据
- **THEN** zbc minor version = 0.6；inner zpkg minor 同步 bump；老 VM（< 0.6）读取报 `Z0XXX UnsupportedZbcVersion`

#### Scenario: VM 加载时 verify_constraints
- **WHEN** Rust VM 加载含 func 约束的 zbc
- **THEN** `verify_constraints()` 遍历 func signature 内引用的 class/interface 类型，确认存在；不存在则 fatal error

## MODIFIED Requirements

### Requirement: GenericConstraintBundle 结构

**Before:** 7 字段（BaseClass / Interfaces / RequiresClass / RequiresStruct / RequiresConstructor / RequiresEnum / TypeParamConstraint）。

**After:** 8 字段，新增：

```csharp
public Z42FuncType? FuncSignature { get; init; } = null;
```

约束类型与既有字段**正交**：v1 仅允许单一 `FuncSignature` 存在（不与其他约束并置；并置 → E0409 InvalidFuncConstraint）。

## IR Mapping

| Constraint kind | IR 表示 |
|------|------|
| Func/Action/Predicate / `(T)->R` | `IrConstraintBundle.FuncSignature: IrFuncSig{ params: List<TypeTag>, ret: TypeTag }` |

## Pipeline Steps

- [x] Lexer（无新 token）
- [x] Parser / AST（`(T) -> R` 字面量已存在；命名形式已通过 generic instantiation 解析）
- [ ] TypeChecker（`ResolveWhereConstraints` + `ValidateGenericConstraints` 新分支）
- [ ] IR Codegen（`EmitConstraintBundle` 新字段）
- [ ] VM Loader（`verify_constraints` 新分支）

## Error Codes

| Code | 名称 | 触发 |
|------|------|------|
| **E0408** | GenericFuncConstraintViolation | 调用站点 type_arg 不满足 func signature |
| **E0409** | InvalidFuncConstraint | func 约束与其他约束并置（如 `T: Func<...> + ICloneable`） |

## Version Bump

- zbc 1.3 → 1.4（per-param func sig 编码扩展）
- zpkg 0.4 → 0.5
- Std stdlib zpkg 全部重生
