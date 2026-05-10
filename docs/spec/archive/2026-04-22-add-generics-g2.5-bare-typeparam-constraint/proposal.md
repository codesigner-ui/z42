# Proposal: L3-G2.5 裸类型参数约束（`where U: T`）

## Why

L3-G2.5 表最后一个缺口：让类型参数 U 约束为另一个类型参数 T 的子类型。常见用例：

```z42
class Container<T, U> where U: T {
    T Get(U child) { return child; }  // U 上转为 T，向下兼容
}

void AddAll<T, U>(List<T> target, List<U> source) where U: T {
    foreach (var s in source) target.Add(s);  // U 值放入 List<T> 合法
}
```

C# 支持此语法（`where U: T`），Rust 无（Rust 无继承子类型），Scala 用 `<:`。z42 沿用 C# 风格，与现有 `where T: BaseClass` / `where T: I` 同构。

## What Changes

- **语法**：无新 token；Parser 已能解析 `where U: T`（T 是 identifier，作 `NamedType` 通过 TypeParser）
- **AST**：无结构改动；沿用现有 `GenericConstraint.Constraints: List<TypeExpr>`
- **TypeCheck**：
  - `GenericConstraintBundle` 新增 `TypeParamConstraint: string?`（被约束为哪个 type param 名）
  - `ResolveWhereConstraints` 解析 constraint 时判断：若 NamedType 名字 ∈ `declaredTypeParams` → 记入 `TypeParamConstraint`
  - 体内成员查找：`u.Method()` 当 U 的约束是另一个 type param T，沿 T 的 bundle 递归查找（一次跳转足够，防无限递归）
  - 调用点校验：所有 typeArg 推断完后，对每个 `TypeParamConstraint=T` 的 U，校验 `typeArg[U]` 是 `typeArg[T]` 的子类型或相等
- **IR + zbc**：
  - `IrConstraintBundle` 新增 `TypeParamConstraint: string?`
  - zbc SIGS/TYPE 每个 bundle 新增 flag bit3 `HasTypeParamConstraint` + 条件 `type_param_name_idx: u32`
  - **版本 bump 0.5 → 0.6**（格式不向后兼容，全量重生成）
- **Rust VM**：
  - `ConstraintBundle` 镜像新增字段
  - `zbc_reader` / `binary.rs` 解码
  - `verify_constraints` 对 `type_param_constraint` 不做校验（名字引用的是同一 decl 的 type param，本地即知；即使跨 zpkg 也在同一签名内）

## Scope

| 文件/模块 | 变更 |
|-----------|------|
| `z42.Semantics/TypeCheck/Z42Type.cs` | `GenericConstraintBundle.TypeParamConstraint: string?` |
| `z42.Semantics/TypeCheck/TypeChecker.cs` | ResolveWhereConstraints 识别 type-param 约束；ValidateGenericConstraints 加子类型校验 |
| `z42.Semantics/TypeCheck/TypeChecker.Exprs.cs` / `.Calls.cs` | 受 type-param 约束的成员查找递归到目标 type-param 的 bundle |
| `z42.IR/IrModule.cs` | `IrConstraintBundle.TypeParamConstraint: string?` |
| `z42.IR/BinaryFormat/ZbcWriter.cs` | VersionMinor 5 → 6；flag bit3 + optional name_idx |
| `z42.IR/BinaryFormat/ZbcReader.cs` | 同步解码 |
| `z42.IR/BinaryFormat/ZasmWriter.cs` | `.constraint U: T` 格式（T 为类型参数名时按已声明顺序显示） |
| `z42.Project/ZpkgReader.cs` | SIGS 扫描同步跳过新字段 |
| `z42.Semantics/Codegen/IrGen.cs` | `BuildConstraintList` 拷贝新字段 |
| `src/runtime/src/metadata/bytecode.rs` | `ConstraintBundle.type_param_constraint: Option<String>` |
| `src/runtime/src/metadata/zbc_reader.rs` / `binary.rs` | 解码新字段 |
| `z42.Tests/TypeCheckerTests.cs` | 4-5 新用例 |
| `z42.Tests/ZbcRoundTripTests.cs` | 1 新 round-trip |
| `src/runtime/src/metadata/constraint_tests.rs` | 1 新 loader 测试 |
| `src/runtime/tests/golden/run/72_generic_bare_typeparam/` | Golden：Container<T, U> 实例化 |
| `src/runtime/tests/golden/errors/33_bare_typeparam_not_subtype/` | Error：typeArg[U] 不是 typeArg[T] 子类型 |
| `docs/design/generics.md` | L3-G2.5 裸参数约束小节 |
| `docs/roadmap.md` | 范式表裸参数 → ✅ |

## Out of Scope

- **多层 type-param 约束链 `U: T, T: V`**：一层跳转足够，递归检查解约束在一跳后退出（不开无限递归）
- **跨 decl 的 type-param 约束**：本次只支持同一 decl（函数或类的 type params 列表内）
- **运行时 Call/ObjNew 子类型校验**：与 L3-G3a 同策略，延后到 L3-R 统一批次

## Open Questions

- [ ] 格式版本升 0.5 → 0.6 策略：直接 bump + 一次性重生成？**决策**：同 L3-G3a，一次性重编（stdlib + golden）
- [ ] bundle 字段放哪？**决策**：单独 `TypeParamConstraint: string?` 字段，与 `BaseClass: Z42ClassType?` 并列，零语义歧义
- [ ] 若 U 约束 T 同时 U 也有 interface 约束（`where U: T + IDisplay`）：允许组合，成员查找先走接口后走 T 的 bundle
