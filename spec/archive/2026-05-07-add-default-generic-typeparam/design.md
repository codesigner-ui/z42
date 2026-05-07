# Design: add-default-generic-typeparam (D-8b-3 Phase 2)

## Architecture

```
Source: class Foo<R> { R m() { return default(R); } }
  │
  ▼  Parser
DefaultExpr(NamedType("R"))
  │
  ▼  TypeChecker.Exprs.cs (case DefaultExpr)
ResolveTypeExpr("R") → Z42GenericParamType("R")
  │
  ├─ in-class scope?  (R ∈ enclosing class's TypeParams)
  │     yes → BoundDefault(Z42GenericParamType, paramIndex=idx_of("R"))
  │     no  → BoundDefault(Z42GenericParamType, paramIndex=0)  ← graceful path
  │
  ▼  IrGen / FunctionEmitterExprs (case BoundDefault)
DefaultOfInstr(dst, paramIndex)
  │
  ▼  ZbcWriter.Instructions
[opcode 0xA3] [type_tag Ref] [dst u16] [paramIndex u8]
  │
  ▼  Rust ZbcReader → bytecode.rs Instruction enum
Instruction::DefaultOf { dst, param_index }
  │
  ├─ Interp: exec_instr.rs case DefaultOf
  │      ┌─ frame.regs[0] = Value::Object(rc)?
  │      │     no  → default_value_for("unknown") = Null  ← graceful degradation
  │      └─ rc.borrow().type_desc.type_args.get(idx as usize)
  │            None → default_value_for("unknown") = Null
  │            Some(tag) → default_value_for(tag) = resolved zero
  │
  └─ JIT: translate.rs case DefaultOf → call jit_default_of(frame, ctx, dst, param_index)
         helpers_object.rs jit_default_of mirrors interp logic
```

## Decisions

### Decision 1: Operand encoding — index byte vs name string

**问题**：`DefaultOf` 需要表达"当前 class 的第几个 type-param"。两种方案：

- A. 编码 byte index（0/1/2/...），codegen 时根据 `_activeClassTypeParams` 列表确定位置
- B. 编码 string name（"R"/"K"/"V"），运行时在 `type_desc.type_params` 里 linear search

**选项 A**（index byte）：
- 优点：编码紧凑（1 byte），运行时 O(1) 直接索引；renaming type-param 不破坏 zbc 兼容（虽然 pre-1.0 不在意兼容）；与 IR / VM 风格"用 slot index 而非 name"一致
- 缺点：disasm 输出 `default.of $1` 不如 `default.of @R` 易读；codegen 时必须知道 param 在 class TypeParams 中的位置（需要把"当前 class 的 type_params 列表"穿到 emit 上下文）

**选项 B**（string name）：
- 优点：disasm 直观；codegen 不需要 class type_params 上下文；consistent with `FieldGet` 用 field_name string
- 缺点：编码大（4 byte string pool index vs 1 byte）；运行时多一步 linear search（<5 elements，可忽略）

**决定**：选 **A**（index byte）。

理由：
1. User confirmation 明确选 A
2. 与 z42 IR 的"register slot 用 index、type 用 string"惯例并不冲突 —— type-param 在 class 上下文里更接近 register slot 的语义（位置决定身份）
3. 编码紧凑 + 运行时 O(1) 直接索引；性能不是核心考量但"对的事顺便做对"
4. disasm 端可以用 `default.of  %dst, $1  ; param=R` 注释格式（writer 端从 SemanticModel 拿 type_param 名字），保留可读性

### Decision 2: Receiver — implicit `this` (reg 0) vs explicit operand

**问题**：`DefaultOf` 需要从某个 generic instance 拿 type_args。两种方案：

- A. 隐式约定 `this` 在 `frame.regs[0]`，opcode 不编码 receiver
- B. 显式编码 receiver register `obj`

**选项 A**（隐式 this）：
- 优点：opcode 体积更小（少 2 bytes）；与 z42 instance method 调用约定一致（this = reg 0）
- 缺点：static method / free function 调用时 reg 0 不是 generic instance；需要 graceful-degradation 处理

**选项 B**（显式 receiver）：
- 优点：可用于"对任意已知 generic instance 取 default(T)"；与 FieldGet 一致
- 缺点：常态使用永远是 this，多编 2 bytes 浪费

**决定**：选 **A**（implicit this）。

理由：
1. User confirmation 明确选 A
2. 主用例（method body 内 `default(T)` 引用本类 type-param）100% 走 reg 0
3. graceful-degradation 路径覆盖 static / free / non-object reg 0 → 不 panic，返回 Null
4. 未来若需要"显式 receiver"形态，可加 sibling opcode `DefaultOfFrom` 而非污染主路径

### Decision 3: Gate lifting — allow any vs only class-level

**问题**：TypeChecker 解除 E0421 gate 时，是否区分 class-level type-param（已支持）与 method-level / free type-param（仅 graceful-degradation）？

- A. 解除全部，让 TypeChecker 一律允许 `Z42GenericParamType`
- B. 严格判定 in-class scope，否则保留 E0421 + 升级提示信息

**选项 A**（全部解除）：
- 优点：用户代码 `default(T)` 在任何 generic 上下文都编译通过；行为可预测（runtime 给 Null）
- 缺点：method-level / free 路径运行时返回 Null 但用户不知道 —— silent 错误风险

**选项 B**（仅 class-level，其他保 E0421）：
- 优点：明确区分 supported / unsupported；用户被强制感知边界
- 缺点：codepath 双轨；E0421 message 需要更细化（"class-level 已支持，方法级 / free 待 follow-up spec"）

**决定**：选 **A**（全部解除）。

理由：
1. User confirmation 明确选 A
2. graceful-degradation 是 z42 整体一致的"未知 fallback to Null"风格（参见 VM `default_value_for("unknown") = Null`、ImportedSymbolLoader 未知类型 fallback to Z42PrimType 等）
3. method-level / free 场景在 stdlib 当前用例里不存在；用户极少触发
4. 若实际遇到痛点（用户报"我以为返回 0 但拿到 null"），可在新 spec 里加 method-level type_args 计参 —— 那是真正需要 calling-convention 改造的工作，不应在本 spec 强行用 E0421 阻塞编译

### Decision 4: zbc version bump

**问题**：新 opcode 是否需要 zbc version bump？

**决定**：必需。0.8 → 0.9。

理由：
1. User confirmation 明确选 A（bump）
2. 旧 VM 看到 0xA3 字节会 unknown opcode panic；显式 bump 让 loader 在版本不匹配时 fail-fast 而非乱解码
3. pre-1.0 minor bump 是常态；workflow 规则明确"不为旧版本提供兼容"

### Decision 5: Inherited generic continuity

**问题**：`class Bar<R> : Foo<R>` 调用父类 `Foo<R>::m()` 内的 `default(R)` 时，运行时 `this.type_desc` 是 Bar 的 desc，能否拿到正确的 `type_args[idx_of_R]`？

**调研发现**：z42 现有 generic 继承（D-8b-0 落地后）的 `type_desc.type_args` 传递路径未在本 spec 范围内验证。

**决定**：作为本 spec 的"验证场景"之一加入 golden 测试。如果发现 inherited type_args 不完整，**停下汇报 User**（per workflow.md 越界防护），不在本 spec 内强行修，独立 spec 处理。

理由：
1. inherited generic continuity 是 D-8b-0（class arity overloading）+ generics-base 的复合工作产物；其完整性应独立验证
2. 本 spec 主体（Phase 2 IR opcode + runtime 查表）不依赖父类继承场景就能 land MulticastException<R> 主用例

## Implementation Notes

### TypeChecker side

`TypeChecker.Exprs.cs` 的 `case DefaultExpr de:` 分支当前在 `Z42GenericParamType` 触发 E0421。改为：

```csharp
if (t is Z42GenericParamType gp)
{
    int paramIdx = ResolveGenericParamIndex(gp.Name);  // class TypeParams 内的位置；找不到返回 0（graceful）
    return new BoundDefault(t, de.Span) { GenericParamIndex = paramIdx };
}
```

`ResolveGenericParamIndex(name)`：
- 在 `_currentClassDecl?.TypeParams` 列表里 IndexOf(name)；命中 → 返回 idx
- 未命中（method-level / free / static） → 返回 0（runtime 退化）

### BoundDefault record 扩展

加 nullable `GenericParamIndex: int?` 字段（`null` 表示 fully-resolved 路径，emit Const*；非 null 表示 generic-param 路径，emit DefaultOfInstr）。

### IrGen side

`FunctionEmitterExprs.cs` 的 `case BoundDefault bd:` 分支 dispatch：

```csharp
case BoundDefault bd:
    if (bd.GenericParamIndex is int idx)
        Emit(new DefaultOfInstr(dst, (byte)idx));
    else
        // Phase 1 路径：按 bd.Type emit Const*
        EmitPhase1Default(bd, dst);
    break;
```

### VM interp side

`exec_instr.rs` 加：

```rust
Instruction::DefaultOf { dst, param_index } => {
    let val = match frame.get(0)? {                          // reg 0 = this
        Value::Object(rc) => {
            let td = &rc.borrow().type_desc;
            td.type_args.get(*param_index as usize)
                .map(|tag| default_value_for(tag))
                .unwrap_or(Value::Null)
        }
        _ => Value::Null,                                    // graceful: non-object reg 0
    };
    frame.set(*dst, val);
}
```

### VM JIT side

`helpers_object.rs` 加：

```rust
pub unsafe extern "C" fn jit_default_of(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32, param_index: u32,
) -> u8 {
    // mirror interp logic
}
```

`translate.rs` 加：

```rust
Instruction::DefaultOf { dst, param_index } => {
    let d = ri!(*dst);
    let pi = builder.ins().iconst(types::I32, *param_index as i64);
    let inst = builder.ins().call(hr_default_of, &[frame_val, ctx_val, d, pi]);
    let ret = builder.inst_results(inst)[0]; check!(ret);
}
```

## Testing Strategy

**Golden tests**（端到端 IR + VM）：

- `src/tests/operators/default_generic_param/` — `class Foo<R> { R m() { return default(R); } }`，分别用 `Foo<int>` / `Foo<string>` / `Foo<MyClass>` 实例验证返回 `0` / `null` / `null`
- `src/tests/operators/default_generic_param_field_init/` — 泛型字段 `T x = default(T);` 自动 init 验证
- `src/tests/operators/default_generic_param_pair/` — `Pair<K, V>` 多 type-param，第二位置 `default(V)` 验证
- `src/tests/operators/default_generic_param_inherited/` — `class Bar<R> : Foo<R>` 子类调父类 method 取 `default(R)` 验证（如失败：标记 follow-up，不阻塞主流程）
- `src/tests/operators/default_generic_param_graceful/` —（如可构造）method-level / free 场景跑 default(T) 拿 Null

**xUnit 单测**（C#，新 cases 加进 `DefaultExpressionTests.cs`）：

- TypeCheck：`default(R)` in-class → 通过 + GenericParamIndex 正确
- TypeCheck：`default(R)` method-level → 通过（不再 E0421）+ GenericParamIndex 0
- IrGen：generic-param 路径 emit `DefaultOfInstr`；fully-resolved 路径仍 emit Const*
- ZbcWriter / ZbcReader round-trip：`DefaultOfInstr` 编 / 解码

**Cargo 单测**（Rust）：

- `interp::default_of` — 构造 fake frame + `Object` with `type_args=["int"]`，dispatch DefaultOf(0, 0) → Value::I64(0)
- `interp::default_of_oob` — `param_index` 超出 type_args 长度 → Value::Null
- `interp::default_of_non_object` — reg 0 是 primitive → Value::Null

**Regression**：

- `dotnet test` 全绿（含原 21+ DefaultExpressionTests 不破）
- `./scripts/test-vm.sh interp/jit` 全绿（147 + 143 不破，新增 4-5 golden 通过）
- `cargo test` 全绿
- `regen-golden-tests.sh` 因 zbc 版本 bump 必须 re-emit 全部 .zbc

## Open Questions（实施期会停下确认）

- **Decision 5 inherited continuity**：如果 golden `default_generic_param_inherited` 失败，停下汇报，不在本 spec 内修
- **`param_index` 上限**：byte 上限 255，常规 generic class 1-3 个 type-param，足够；如代码库有 5+ type-param 的 class（极罕见）需要复检
