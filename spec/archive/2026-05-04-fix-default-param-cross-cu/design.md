# Design: cross-CU default param fill

## Architecture

```
TypeCheck phase 已有：
  SemanticModel.Classes[name].Methods[methodKey].Z42FuncType
    ├ Params: List<Z42Type>
    └ RequiredCount  (= MinArgCount)
  imported via MergeImported 的 Z42ClassType 也在 SemanticModel.Classes

IrGen 初始化新增：
  _funcSignatures: Dictionary<string, Z42FuncType>
    key 与 _funcParams 同款（QualifyClassName.method 或 .arityKey）
    value 是 Z42FuncType（含 Params + RequiredCount）
  populate from SemanticModel.Classes (含 imported) + Funcs

FunctionEmitterCalls.FillDefaults 改造：
  if _funcParams.TryGetValue(name) → 走 BoundDefault 路径（local CU）
  else if _funcSignatures.TryGetValue(name) → 走 type-default 路径（imported）
  else return argRegs unchanged（兼容未注册 case）
```

## Decisions

### Decision 1: type-default vs 完整 default value 传递
**问题：** imported method 的用户 default 怎么处理？
**选项：**
- A. **Type-default 兜底**：`bool=false`, `int=0`, ref=null 等。简单，不需扩 TSIG
- B. **完整传递**：TSIG 扩 `ExportedParamDef.DefaultValue` 字段；codegen 反序列化

**决定：A（v1 type-default）**。原因：
- B 涉及 TSIG schema 变更（向后兼容 + 反序列化 BoundExpr）很重
- 用户写 `int x = 42` 跨 CU 拿到 0 是悄无声息退化 —— 但这是 stdlib 级问题，stdlib 实际默认值绝大多数就是 type-default（false / 0 / null）
- 文档注释用户限制；触发实际 bug 后开 follow-up（B）

### Decision 2: _funcSignatures 注册位置
**决定：** IrGen 初始化阶段（在 _funcParams 注册之后），遍历：
- `_semanticModel.Classes`（含 imported via MergeImported）
- `_semanticModel.Funcs`（local + imported funcs）

key 与 _funcParams 同款：`{QualifyClassName(class)}.{methodKey}` for methods，`{QualifyName(funcName)}` for top-level funcs。`methodKey = arityKey if overloaded else bare name`。

**为什么不复用 _funcParams**：FuncParams 用 AST `Param` 引用，imported 方法没有 AST。新 dict 用 Z42FuncType（语义类型）避免 AST 依赖。

### Decision 3: EmitTypeDefault 类型映射
```csharp
static IrInstr EmitTypeDefault(Z42Type t, TypedReg dst) => t switch {
    Z42PrimType { Name: "bool" }  => new ConstBoolInstr(dst, false),
    Z42PrimType { Name: "int" or "i8" or "i16" or "i32" or "byte" or "sbyte" or "short" or "ushort" or "uint" or "u8" or "u16" or "u32" }
                                    => new ConstI32Instr(dst, 0),
    Z42PrimType { Name: "long" or "ulong" or "i64" or "u64" }
                                    => new ConstI64Instr(dst, 0),
    Z42PrimType { Name: "float" or "f32" }  => new ConstF32Instr(dst, 0f),
    Z42PrimType { Name: "double" or "f64" } => new ConstF64Instr(dst, 0d),
    Z42PrimType { Name: "char" }   => ConstCharInstr(dst, '\0'),
    _                              => new ConstNullInstr(dst),  // 引用类型 / Option / Class / Interface / Func / Array / generic param
};
```

**generic param**（Z42GenericParamType）：emit ConstNullInstr —— 对实例化 generic 调用，type-arg 实际解为具体类型；但在 generic body 内部调用时 caller's perspective 是抽象 T；ConstNull 是相对安全的占位。

### Decision 4: workaround 还原作为本 spec 一部分
**决定：** 同 spec 还原 D2d-2 的 1-arg/2-arg overload workaround，回到设计预期的 single-signature `Invoke(T, bool=false)` 形式 + 其它两个类同款。
- 验证修复确实通用（不止某一个特例）
- 简化 stdlib API
- 移除 deferred-features.md D-9 条目

## Implementation Notes

- `TypedReg` 已有 `IrType` 字段；emit 前 `Alloc(ToIrType(z42Type))` 然后 emit const
- `ToIrType` 在 FunctionEmitter 中已有；从 Z42Type 转 IrType
- 所有 Const 系列 IrInstr 已存在（bytecode.rs / IR module）
- 测试：CrossCuDefaultParamTests 单元测试不易（需要 stdlib），主要走 golden 端到端

## Testing Strategy

- Golden test `cross_cu_default_param/source.z42`：定义 stdlib-level 助手类（或复用现有 multicast）+ 调跨 CU 1-arg 形式 + 验证函数体读默认参数拿到 type-default
- 既有 multicast_*  golden 在 stdlib 还原 single-sig 后仍 GREEN（核心回归）
- D2a/b/c/d 7 个 multicast 相关 golden 全 GREEN
- IncrementalBuildIntegrationTests 不变（44/44 不动）
