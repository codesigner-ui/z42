# Design: ObjNew ctor name dispatch

## Architecture

```
源码 new Foo(1, "x")
  │
  ↓ Parser
NewExpr(Type=Foo, Args=[1, "x"])
  │
  ↓ TypeChecker (NewExpr branch)
  │  1. ResolveType → Foo class type
  │  2. ResolveCtor(Foo, args) → 选 ctor name (含 $N 如有)
  │     - 查 ClassType.Methods 字典中以 "Foo" 开头的项（ctor 命名约定 Class.SimpleName 或 SimpleName$N）
  │     - 按 args.Count + 类型匹配选具体 overload
  │     - 单 ctor 类返回 "Foo.Foo"；重载返回 "Foo.Foo$N"
BoundNew(QualName="Foo", CtorName="Foo.Foo$2", Args=[...], Type, Span)
  │
  ↓ Codegen (EmitBoundNew)
ObjNewInstr(Dst, ClassName="Foo", CtorName="Foo.Foo$2", Args)
  │
  ↓ ZbcWriter
[OP_OBJ_NEW] [tag] [dst] [class_idx] [ctor_idx] [args]
  │
  ↓ Loader (Rust)
Instruction::ObjNew { dst, class_name="Foo", ctor_name="Foo.Foo$2", args }
  │
  ↓ VM interp
1. allocate object (TypeDesc by class_name)
2. fn = lookup(ctor_name) // 直查，无推断
3. call fn(obj, ...args)
```

## Decisions

### Decision 1: BoundNew 增字段而非新建

**问题**：`BoundNew(QualName, Args, Type, Span)` 当前不带 ctor 信息。

**决定**：增加 `string CtorName` 字段，**所有路径都填**（即使单 ctor 也填
完整名 `"Foo.Foo"`）。不区分"有/无重载"。

**理由**：
- 简化下游 codegen 逻辑（永远从 BoundNew 拿名，无 fallback）
- 单 ctor 时填的是"VM 当前推断会得到的同样字符串"，0.4 zbc 兼容路径保留
  此填充作为缺省

### Decision 2: ctor overload resolution 算法

**问题**：从 ClassType.Methods 字典选具体 ctor。当前 method 字典 keys 含
`$N` suffix，如 `Foo.Foo$1` / `Foo.Foo$2`；单 ctor 仅 `Foo.Foo`。

**决定**：

```
resolve_ctor(className, args):
  candidates = methods.keys().filter(k =>
    k starts with className + "." + simpleName  // "Foo.Foo" or "Foo.Foo$N"
  )
  if candidates.size == 1: return candidates[0]
  // overload: 按 args.Count 匹配 method.Params.Count
  for c in candidates:
    if methods[c].Params.Count == args.Count:
      return c  // 简化版：只看 arity，不做 type-based selection
  // 无匹配 → 选 args.Count 最接近的 / 报错
```

**理由**：
- z42 现有方法 overload selection 应已支持 arity-based 选择（看
  `TypeChecker.Calls.cs` 已有逻辑）；ctor 复用同样路径
- 类型 narrowing / implicit conversion 优先级是后续优化，本 Wave 不上

### Decision 3: ctor 函数名约定

**问题**：stdlib / 用户类的 ctor 函数命名规则。

**已有规则**（保持不变）：
- 单 ctor：`{ClassName}.{SimpleName}`，如 `Std.Exception.Exception`
- 多 ctor：`{ClassName}.{SimpleName}${N}`，如 `Std.Exception.Exception$1` / `$2`

`{N}` 是按声明顺序的 1-based index（与现有方法重载约定一致）。

**理由**：保持与方法重载现有规则对齐；不为 ctor 引入特殊命名。

### Decision 4: 不为 0.4 zbc 提供兼容（2026-04-26 调整）

**问题**：0.4 zbc（无 ctor_name 字段）如何加载？

**决定**：**不兼容**。0.5 解码器遇 0.4 zbc 直接报版本错误。

**理由**：按 [.claude/rules/workflow.md "不为旧版本提供兼容"](.claude/rules/workflow.md#不为旧版本提供兼容-2026-04-26-强化)，
z42 处于快速迭代阶段，任何兼容投入是过早优化。所有现有 zbc 通过
`./scripts/regen-golden-tests.sh` 一次性重生为 0.5 格式即可；持久化的旧
zbc 无 use case。

### Decision 5: VM exec 路径不再推断

**决定**：`exec_instr.rs ObjNew` 完全删除 `${class}.${simple}` 字符串推断。
ctor 名仅从 `Instruction::ObjNew.ctor_name` 字段读取。

**理由**：根因修复原则。VM 不应做 dispatch 决策，只查表。

### Decision 6: zbc 版本 bump 0.4 → 0.5

**决定**：bump minor version。

**理由**：
- 字段添加是格式扩展（minor），未删除已有字段
- 旧版可读（兼容路径）；新版不可被 0.4 解码器读
- 与历史 bump 节奏一致

### Decision 7: JIT 路径同步

**决定**：JIT 端的 ObjNew 路径（`jit/translate.rs` / `jit/helpers_object.rs`
若有）同样用 ctor_name 字段；不复用 simple 推断。

**理由**：interp / JIT 一致；现状 JIT 是否有 ObjNew 路径需在阶段 1 调研
确认（通常 ObjNew 走 builtin call，可能需要修 helper）。

## Implementation Notes

### 编译器侧 — TypeChecker

`TypeChecker.Exprs.cs` `NewExpr` 处理（line 129-160）：

```csharp
case NewExpr newExpr:
{
    var args     = newExpr.Args.Select(a => BindExpr(a, env)).ToList();
    var newType  = ResolveType(newExpr.Type);
    var qualName = newExpr.Type switch { ... };

    // 新增：ctor overload resolution
    var ctorName = ResolveCtorName(qualName, args.Count, newExpr.Span);

    // ... 现有 abstract / generic 验证 ...

    return new BoundNew(qualName, args, ctorName, newType, newExpr.Span);
}

private string ResolveCtorName(string className, int argCount, Span span)
{
    if (!_symbols.Classes.TryGetValue(className, out var cls)
        && !_imported.Classes.TryGetValue(className, out cls))
        return $"{className}.{shortName}"; // fallback (类未找到，下游会另报错)

    var simple = ShortClassName(className);
    var prefix = $"{className}.{simple}";

    // 单 ctor: 仅 "{className}.{simple}" 命中
    if (cls.Methods.ContainsKey(simple))
        return prefix;

    // 多 ctor: "{simple}$N" 命中（按 arity 选）
    var candidates = cls.Methods.Keys
        .Where(k => k == simple || k.StartsWith(simple + "$"))
        .ToList();
    foreach (var c in candidates)
    {
        if (cls.Methods[c].Params.Count == argCount + 1)  // +1 for `this`
            return $"{className}.{c}";
    }

    // 无匹配 → 报错或返回 fallback
    _diags.Error(...);
    return $"{prefix}$1";  // 占位
}
```

注意：方法表 keys 在 stdlib 中是不带 `Class.` prefix 的（仅 `simple` 或
`simple$N`）。具体 key 形式需阶段 1 验证。

### Codegen

`FunctionEmitterExprs.cs` `EmitBoundNew`：

```csharp
private TypedReg EmitBoundNew(BoundNew n)
{
    switch (n.QualName)
    {
        case "StringBuilder":
            // ... existing ...
        default:
            var argRegs = n.Args.Select(EmitExpr).ToList();
            string qualCls = _ctx.QualifyClassName(n.QualName);
            // 新：用 BoundNew.CtorName（已带 $N）；以前的 ctorKey 推导被替换
            argRegs = FillDefaults(n.CtorName, argRegs);
            var dst = Alloc(IrType.Ref);
            Emit(new ObjNewInstr(dst, qualCls, n.CtorName, argRegs));
            return dst;
    }
}
```

注意：`BoundNew.CtorName` 是 short form（`Foo.Foo$2`），还是 qualified
（`Std.Exception.Exception$2`）— 设计选 **qualified**，与现存 ctor 函数
名约定一致。`QualifyClassName` 仍用于 ObjNewInstr.ClassName（保持原语义）。

### IR / zbc

`IrModule.cs`:

```csharp
public sealed record ObjNewInstr(
    TypedReg Dst, string ClassName, string CtorName, List<TypedReg> Args) : IrInstr;
```

`ZbcWriter.Instructions.cs`:

```csharp
case ObjNewInstr i:
    w.Write(Opcodes.ObjNew); w.Write(TypeTagFromIrType(i.Dst.Type)); WriteReg(w, i.Dst);
    w.Write((uint)pool.Idx(i.ClassName));
    w.Write((uint)pool.Idx(i.CtorName));   // 新增
    WriteArgs(w, i.Args);
    break;

// pool intern：
case ObjNewInstr i:
    pool.Intern(i.ClassName);
    pool.Intern(i.CtorName);  // 新增
    break;
```

`ZbcReader.Instructions.cs`:

```csharp
case Opcodes.ObjNew:
    var dst = ReadReg(...);
    var cls = pool.Get(reader.ReadUInt32());
    var ctor = pool.Get(reader.ReadUInt32());  // 直接读，无兼容分支
    var args = ReadArgs(...);
    return new ObjNewInstr(dst, cls, ctor, args);
```

### Rust VM

`bytecode.rs`:

```rust
pub enum Instruction {
    // ...
    ObjNew {
        #[serde(with = "typed_reg_serde")] dst: Reg,
        class_name: String,
        ctor_name: String,
        #[serde(with = "typed_reg_vec_serde")] args: Vec<Reg>,
    },
    // ...
}
```

`exec_instr.rs ObjNew`:

```rust
Instruction::ObjNew { dst, class_name, ctor_name, args } => {
    let type_desc = module.type_registry.get(class_name).cloned()
        .or_else(|| crate::metadata::lazy_loader::try_lookup_type(class_name))
        .unwrap_or_else(|| std::sync::Arc::new(make_fallback_type_desc(module, class_name)));
    let slots = vec![Value::Null; type_desc.fields.len()];
    let obj_rc = Rc::new(RefCell::new(ScriptObject { type_desc, slots, native: NativeData::None }));
    let obj_val = Value::Object(obj_rc);

    // 直查 ctor_name（删除 ${class}.${simple} 推断）
    let ctor_fn = module.func_index.get(ctor_name.as_str())
        .and_then(|&idx| module.functions.get(idx));
    if let Some(ctor) = ctor_fn {
        let mut ctor_args = vec![obj_val.clone()];
        ctor_args.extend(collect_args(&frame.regs, args)?);
        super::exec_function(module, ctor, &ctor_args)?;
    } else if let Some(lazy_ctor) = crate::metadata::lazy_loader::try_lookup_function(ctor_name) {
        let mut ctor_args = vec![obj_val.clone()];
        ctor_args.extend(collect_args(&frame.regs, args)?);
        super::exec_function(module, lazy_ctor.as_ref(), &ctor_args)?;
    }
    // 没找到 ctor 时静默跳过（保持现有"ctor 可选"语义）

    frame.set(*dst, obj_val);
}
```

## Testing Strategy

### 单元测试

- `TypeCheckerTests.NewExpr_SingleCtor_ResolvesCtorName` — 单 ctor 类
  生成 `BoundNew.CtorName == "Foo.Foo"`
- `TypeCheckerTests.NewExpr_OverloadCtor_ResolvesByArity` — 双 ctor 类按
  args 数量选 `Foo.Foo$1` 或 `Foo.Foo$2`
- `ZbcRoundTripTests` 增加 ObjNew 含 CtorName 的 round-trip 验证

### Golden tests

- `run/96_ctor_overload`：用户类 双 ctor，`new Foo(1)` + `new Foo(1, "x")`
- `run/97_stdlib_exception_double_ctor`：恢复 Exception 双 ctor 后端到端
  验证（结合 Wave 2 后插的 InnerException 链）

### 回归

- 全量 `dotnet test` / `test-vm.sh` / `cargo test` 全绿
- 重生成所有 source.zbc（`regen-golden-tests.sh`）确保 0.5 zbc 全部测试可读
- 手动构造 0.4 zbc 测试兼容路径（可选；最低优先级）

## 兼容性风险

| 风险 | 评估 | 缓解 |
|------|------|------|
| ZbcRoundTripTests 等基于固定 zbc 字节序列的测试失败 | 中 | 重新构造测试 fixtures；确认字段顺序 |
| 0.4 zbc 兼容路径填充错误（如类名未含 `.`） | 低 | 单测覆盖兼容路径；fallback 用 `class_name` 作 simple |
| JIT 路径中 ObjNew 实现遗漏 | 中 | 阶段 1 grep `OP_OBJ_NEW` / `ObjNew` 全部出现处确认 |
| stdlib 编译器对 ctor `$N` 的命名规则与 ResolveCtorName 不一致 | 中 | 阶段 1 验证：dump z42.core.zpkg 确认现有命名约定 |
| FillDefaults / ObjNewInstr 调用点遗漏 | 低 | 全 grep `ObjNewInstr`，确保都传 CtorName |
