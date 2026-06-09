# Design: `typeof(T)` → `Std.Type`

## Architecture

```
parser   ParseTypeof:  typeof(T)  ──►  TypeofExpr(Target: TypeExpr)
            ▼
typecheck TypeChecker.Exprs:  TypeofExpr
            │  target  = ResolveType(to.Target)          // 被反射的类型
            │  stdType = ResolveType(NamedType "Type")    // 结果类型 = Std.Type 类（prelude 解析）
            ▼  BoundTypeof(Target, Type=stdType)
codegen   FunctionEmitter.VisitTypeof:
            │  name = 限定名(Target)                       // 用户类: QualifyClassName → "Demo.Point"
            │                                              // 基础类型: pt.Name; 数组: "Std.Array"
            │  ConstStr nameReg, Intern(name)
            ▼  BuiltinInstr(dst, "__typeof", [nameReg])
runtime  __typeof builtin  ──►  reflection::make_type_from_name(name)
            │  限定名命中主模块 type_registry → 带句柄 Type（成员可枚举）
            ▼  基础类型/不可解析 → synthetic Type（i32→int 规范化）
         Std.Type
```

## Decisions

### Decision 1: 完整编译期解析（option A），不走 parser desugar（修订原 DRAFT）

**问题：** 标准做法是加 `TypeofExpr` AST + TypeChecker 绑定 + Codegen 处理。原 DRAFT 因 `port-z42c-codegen`（自举 B 流）活跃镜像 IrGen，退而求其次选 parser desugar（option B），代价是丢失编译期类型 token → 主模块用户类只能 name-only。

**变化：** `port-z42c-codegen` 已于 2026-06-09 归档（CG-1A–2，210 cases），IrGen 活跃区约束解除。User 裁决「按方案 3 推进」「a（现在做 option A）」。

**决定：** 选 option A。新增 `TypeofExpr` → `BoundTypeof` → FunctionEmitter codegen。FunctionEmitter 在编译期把目标类型解析成**限定名**（用户类 `QualifyClassName` → `Demo.Point`），运行时 `make_type_from_name` 据此命中主模块 `type_registry` → **带真句柄**，成员可枚举。彻底消除 option B 的主模块用户类型退化限制。footprint 大（全套 BoundExpr visitor 都要加 `VisitTypeof`），但换来干净的类型语义 + 完整功能。

### Decision 2: 结果类型解析为 `Std.Type` 类——用短名 `Type` 而非 FQN `Std.Type`

**问题：** `BoundTypeof.Type` 需是带属性派发能力的 Std.Type **类**（`Z42ClassType`），否则 `typeof(int).Name` 退化为字段读取返回 null。

**坑：** `ResolveType(NamedType "Std.Type")`（FQN）穿过 `SymbolTable.ResolveType` 的 `TypeRegistry`（仅基础类型）→ `Classes`（键未命中 FQN）→ **fallback 到 `Z42PrimType("Std.Type")`**。结果 `typeof(int)` 被当作基础类型，`.Name` 绑定到字段读取 → null。

**决定：** 改用**短名** `ResolveType(NamedType "Type")`。短名经 stdlib prelude auto-import 解析为真正的 `Std.Type` 类（与用户手写 `Type t = ...` 同路径）。`typeof(int).Name == "int"` ✓。

### Decision 3: FunctionEmitter emit 限定名（用户类带句柄的关键）

`VisitTypeof` 按目标类型种类构造名字串：

| 目标类型 | emit 的名字 | make_type_from_name 结果 |
|---------|-----------|------------------------|
| `Z42PrimType pt` | `pt.Name`（int/string/bool…）| synthetic Type（规范化别名）|
| `Z42ArrayType` | `"Std.Array"` | Array 的 Type |
| `Z42InstantiatedType it` | `QualifyClassName(it.Definition.Name)` | 限定名命中 registry → 句柄 |
| `Z42ClassType ct` | `QualifyClassName(ct.Name)` → `Demo.Point` | **主模块 registry 命中 → 真句柄** ✓ |
| `Z42GenericParamType gp` | `gp.Name` | synthetic |

`QualifyClassName`（`IEmitterContext`）把短类名补成限定名 → `make_type_from_name` 的主模块 `type_registry.get(qualified)` 命中 → 真 `TypeDesc` 句柄 → `GetFields()` 可枚举。

### Decision 4: `__typeof` builtin 复用 `make_type_from_name`

反射 MVP 已有 `reflection::make_type_from_name(name) -> Value`：先查主模块 `type_registry` → 再 `try_lookup_type`（zpkg lazy loader）→ 都不中则 synthetic Type（规范化别名 i32→int）。`__typeof` 直接转发 `args[0]`（string）。零新逻辑。

## Implementation Notes

- **TypeofExpr / BoundTypeof 是叶子节点**（无 BoundExpr 子节点）：所有 visitor 的 `VisitTypeof` 默认行为——Walker/FlowAnalyzer/ClosureEscapeAnalyzer 返回 `default`，Rewriter 返回 `t`（identity），Dumper 打印 `target=`。新增 BoundExpr variant 触发全套 visitor 编译期穷尽性检查（缺一不编译）。
- **golden 链式属性约束**：`typeof(int).Name` 直接链式可用（`tp` 是局部变量 `var tp = typeof(int)` 后 `tp.Name`）。`var x = obj.GetType()` 的 var 推断不带属性派发——consistency check 用显式 `Type viaGetType = p.GetType()`（见 Out of Scope 限制）。
- **无 Type.z42 改动**：option A 直接 emit `BuiltinInstr("__typeof")`，不经 `Std.Type.__Of` extern。typeof 复用反射 MVP 已落地的 Type 类（Name/FullName/GetFields）——故 C2 **不占 stdlib 锁**。

## Testing Strategy
- golden（权威，C# GoldenTests 跑）：`typeof.z42` —— 基础类型 Name、用户类 `GetFields().Length`、与 `GetType()` 一致（显式 `Type` 注解）。
- C# 全量：1543 编译器测试（含 BoundVisitor 穷尽性、Rewriter identity）。
- GREEN：`xtask test`（vm / cross-zpkg / lib）。
