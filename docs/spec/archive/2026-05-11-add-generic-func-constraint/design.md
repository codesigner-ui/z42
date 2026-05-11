# Design: Generic Function-Type Constraint

## Architecture

```
Parser (no change for literal '(T)->R'; no change for 'Func<...>' generic)
    ↓
AST: WhereClause + GenericConstraint (existing TypeExpr child)
    ↓
TypeChecker.GenericResolve.ResolveWhereConstraints():
   detect TypeExpr → Z42FuncType (vs Z42InterfaceType vs Z42ClassType)
   route to bundle.FuncSignature
    ↓
TypeChecker.Generics.ValidateGenericConstraints():
   if bundle.FuncSignature != null:
      use Z42FuncType.AssignableTo (variance baked in)
      else E0408
    ↓
IrGen.EmitConstraintBundle():
   write IrConstraintBundle with FuncSignature: Some(IrFuncSig{params, ret})
    ↓
ZbcWriter SIGS section per-tp constraint flags |= 0x20
   emit param_count:u8 + params:TypeTag[] + return:TypeTag
    ↓
Rust VM loader verify_constraints():
   walk TypeParamConstraint.func_signature
   for each referenced class/interface type-tag → look up type_registry
   bail if missing
    ↓
Body call site:
   typecheck `t(args)` against bundle.FuncSignature
   emit CallIndirect (existing opcode for closure invocation)
```

## Decisions

### Decision 1: 双语法形态 vs 单形态

**问题**：用户写 func 约束应该用命名 `where T: Func<int, R>` 还是字面量 `where T: (int) -> R`？

**选项**：
- **A** — 仅命名：依赖 stdlib Func/Action/Predicate 已存在
- **B** — 仅字面量：纯结构性、与 stdlib 解耦
- **C** — 双形态：用户两种都能写，TypeChecker 把命名形式 desugar 为字面量形式

**决定**：选 **C**（双形态等价）。

**理由**：
- 命名形式对用户友好（与 delegates-events.md / closure.md 文档一致，无认知断层）
- 字面量形式对类型推断更直接（不依赖 stdlib 加载）
- 两者在 TypeChecker 都解析为 `Z42FuncType` → 单一 bundle 表示
- 0 实现成本差：parser 已支持两种 type expression

### Decision 2: 匹配语义 —— 结构性 + variance

**问题**：约束 `where T: Func<Cat, Animal>` 应该接受 `T = Func<Animal, Cat>` 吗？

**选项**：
- **A** — 严格 nominal：仅完全相同 signature（含 type arg）通过
- **B** — 结构性 + variance：参数逆变 / 返回协变，按 `Z42FuncType.AssignableTo` 既有规则

**决定**：选 **B**。

**理由**：
- z42 既有 `Z42FuncType.AssignableTo` 已实现 variance（Z42Type.cs:67-71），function value 赋值已生效
- 约束语义应该与"function value 赋值"语义一致（最小惊讶原则）
- 严格 nominal 会拒绝合理的代码（如把 `Func<Animal, Cat>` 传给约束 `Func<Cat, Animal>` 的泛型）

**实现细节**：`ValidateGenericConstraints()` 调 `bundle.FuncSignature.IsAssignableFrom(typeArg)`。

### Decision 3: zbc 编码 —— 新 flag bit

**问题**：func signature 怎么写进 zbc？

**选项**：
- **A** — `GenericConstraintBundle` 新增 7→8 字段，zbc flags 加 bit 0x20，per-tp 编码段后追加 param_count + per-param TypeTag + return TypeTag
- **B** — 把 func signature 伪装成"特殊 interface" 用既有 Interfaces 槽位

**决定**：选 **A**。

**理由**：
- B 引入 in-band 编码（哨兵 interface name）—— 类型混淆，未来扩展受阻
- A 与既有 BaseClass / Interfaces 字段正交，向前演进自然
- 0.5→0.6 minor bump 在 pre-1.0 阶段成本低（stdlib 重生即可）

**zbc 编码（FUNC sig 在 constraint bundle 内）**：

```
flags: u8                         # bits: 0x01=class 0x02=struct 0x04=enum 0x08=ctor 0x10=type-param 0x20=func
if flags & 0x01: baseclass_name: u32 strIdx
if flags & 0x02: interface_count: u8 + interfaces: u32[]
if flags & 0x20:
  param_count: u8
  params: TypeTag[]
  return: TypeTag
```

复用既有 TypeTag 编码（zbc 1.0+ 已包含 generic instantiation tag），无需新增 TypeTag。

### Decision 4: Body 调用 —— 复用 CallIndirect

**问题**：body 内 `t(args)` 编译成什么 IR？

**选项**：
- **A** — 新增 `CallTypeParam` 指令携带 type_param index + signature
- **B** — 复用既有 `CallIndirect`（用于闭包间接调用）

**决定**：选 **B**。

**理由**：
- 闭包间接调用已有完整 IR / VM / JIT 链路
- T 是值（变量，传入为 `Value::Closure` / `Value::FuncRef`），CallIndirect 通过 dispatch type-tag 自动选 invocation 路径
- 0 新指令 = 0 VM 端工作 = 0 JIT 端工作

**实现细节**：`TypeChecker.Exprs.cs BindCallExpr` 在 receiver 为 Z42GenericParamType 且 bundle 有 FuncSignature 时，按 signature 推断结果类型 + emit `BoundCallIndirect`。

### Decision 5: v1 仅允许"独立" func 约束（无组合）

**问题**：`where T: Func<int,int> + IFormattable` 是否支持？

**选项**：
- **A** — v1 仅允许 `Func`/`Action`/`Predicate` / `(T)->R` 单独存在
- **B** — 允许 func + interface / baseclass 组合

**决定**：选 **A**（v1 严格）。

**理由**：
- 组合语义复杂：T 既是 callable 又必须实现接口 —— 实践罕见
- 用 E0409 显式拒绝，比静默接受易理解
- v2 如需开放只需放松校验（向前兼容）

**实现细节**：`ValidateGenericConstraints()` 入口检测 `bundle.FuncSignature != null && (bundle.Interfaces.Count > 0 || bundle.BaseClass != null || ...)`，触发 E0409。

## Implementation Notes

### 关键 API 改动

```csharp
// Z42Type.cs
public sealed record GenericConstraintBundle(
    Z42ClassType? BaseClass = null,
    IReadOnlyList<Z42InterfaceType> Interfaces = null!,
    bool RequiresClass = false,
    bool RequiresStruct = false,
    string? TypeParamConstraint = null,
    bool RequiresConstructor = false,
    bool RequiresEnum = false,
    Z42FuncType? FuncSignature = null     // ← NEW
);
```

```rust
// src/runtime/src/metadata/types.rs
pub struct TypeParamConstraint {
    pub base_class: Option<String>,
    pub interfaces: Vec<String>,
    pub flags: u8,
    pub bare_tp_name: Option<String>,
    pub func_signature: Option<FuncSigDescriptor>,  // ← NEW
}

pub struct FuncSigDescriptor {
    pub params: Vec<TypeTag>,
    pub ret: TypeTag,
}
```

### 错误码示例

```
error[E0408]: 'Apply<T,int>' 的类型参数 'T' 必须满足 func 约束 '(int) -> int'
   --> example.z42:12:5
    |
 12 |     Apply<Func<string,int>, int>(s => int.Parse(s), 5);
    |     ^^^^^ T = Func<string,int> 不满足约束：参数类型 (string) 不能匹配 (int)
    |           （参数逆变：实际 (string) 不接受调用方传入的 int）

error[E0409]: func 约束不能与其他约束组合（v1 限制）
   --> example.z42:5:32
    |
  5 | void X<T>() where T: Func<int,int> + ICloneable { ... }
    |                                ^^^^^^^^^^^^^^ 移除 ICloneable，或不用 func 约束
```

## Testing Strategy

### C# 单元测试（`GenericFuncConstraintTests.cs`）

- 解析：`where T: Func<int,int>` 与 `where T: (int) -> int` 等价产生同一 bundle
- 校验：variance 正例 / 反例（参数逆变 ✓、参数协变 ✗、返回协变 ✓、返回逆变 ✗）
- 错误：E0408 触发条件 + E0409 组合约束 reject

### Golden tests

- `func_constraint_basic/` —— 单 Func 约束 + 调用 + 数值断言
- `func_constraint_action/` —— Action 约束 + 副作用断言
- `func_constraint_variance/` —— Animal/Cat 链 + 4 个 variance 组合 + 编译错 + 通过
- `func_constraint_literal/` —— `(T) -> R` 字面量等价
- `errors/E0408_func_constraint_violation/` —— 编译失败 golden

### 跨 mode

interp + JIT 都跑（既有 CallIndirect 已在两模式下支持）。

### zbc 兼容性

- 写一个 0.6 zbc 用 0.5 reader 读：触发 `UnsupportedZbcVersion`
- 写一个无 func 约束的 0.6 zbc 用 0.6 reader 读：正常解码（flag 0x20 未置 → 无 payload）
