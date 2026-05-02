# Proposal: 修复 class field 默认初始化（实例字段 init 表达式 / 类型默认值）

## Why

当前 z42 在三层都静默丢失 class 实例字段的初始值：

1. **TypeChecker** ([`TypeChecker.cs:404`](../../../src/compiler/z42.Semantics/TypeCheck/TypeChecker.cs#L404)) `BindStaticFieldInits` 只过滤 `f.IsStatic`。Parser 已为 `int n = 5;` 产出 `FieldDecl.Initializer`，但 TypeCheck 阶段从不绑定该表达式 → `SemanticModel` 里没有 `BoundInstanceInits`。
2. **Codegen** ([`FunctionEmitter.cs:76`](../../../src/compiler/z42.Semantics/Codegen/FunctionEmitter.cs#L76)) `EmitMethod` 的 ctor 分支只发射 base ctor call，不注入字段初始化代码；无显式 ctor 的类（`ResolveCtorName` 返回 className → VM 跳过 ctor）连一个 ctor 都没有。
3. **VM ObjNew** ([`exec_instr.rs:305`](../../../src/runtime/src/interp/exec_instr.rs#L305) + [`helpers_object.rs:200`](../../../src/runtime/src/jit/helpers_object.rs#L200)) `let slots = vec![Value::Null; len];` 不分类型一律 Null。

后果：
- `class Box { int n = 5; }` + `new Box().n` 返回 `Null`（应为 `5`）
- `class Box { int n; }` + `new Box().n` 返回 `Null`（应为 `0`）
- `int` 字段被 Null 污染会让所有下游算术/比较 silently 类型不一致

## What Changes

- **P1（TypeChecker）**：扩展字段 init 绑定逻辑，覆盖 instance fields；`SemanticModel` 暴露 `BoundInstanceInits: IReadOnlyDictionary<FieldDecl, BoundExpr>`。
- **P2（Codegen）**：每个 ctor body 头部（base ctor call 之后、用户代码之前）按字段声明顺序注入 `this.<field> = <init-expr>`，仅对**有显式 Initializer** 的字段发射；无显式 ctor 的类，若任何字段有显式 init → 合成隐式 ctor `<ClassName>.<ClassName>`（无参，仅 base ctor + 字段 init）。
- **P3（VM ObjNew）**：`FieldSlot` 增加 `type_tag: String`（zbc 已有 `FieldDesc.type_tag`，仅 plumb 一次），`ObjNew` 按 type_tag 选默认值（`int*`/`f64`→0、`bool`→false、`str`/ref→Null）；interp + JIT 镜像。

> P3 处理"无显式 init"的字段；P2 处理"有显式 init"的字段。两者互补，覆盖所有组合。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.cs` | MODIFY | 扩展 `BindStaticFieldInits` → `BindFieldInits`（同时绑定 instance + static），或新增独立方法；保留入口位置 |
| `src/compiler/z42.Semantics/TypeCheck/SemanticModel.cs` | MODIFY | 新增 `BoundInstanceInits` 字段 + 构造参数 |
| `src/compiler/z42.Semantics/Codegen/FunctionEmitter.cs` | MODIFY | `EmitMethod` ctor 分支注入字段 init；新增 `SynthesizeImplicitCtorIfNeeded` 入口 |
| `src/compiler/z42.Semantics/Codegen/IrGen.cs` | MODIFY | 调用合成隐式 ctor 的入口（按需） |
| `src/runtime/src/metadata/types.rs` | MODIFY | `FieldSlot` 增加 `type_tag: String` |
| `src/runtime/src/metadata/loader.rs` | MODIFY | 构造 `FieldSlot` 时填 `type_tag` |
| `src/runtime/src/corelib/object.rs` | MODIFY | corelib `Object` 的 FieldSlot 同步填 type_tag（`__name`/`__fullName` → `"str"`） |
| `src/runtime/src/interp/exec_instr.rs` | MODIFY | `ObjNew` slots 改用 `default_value_for(type_tag)` |
| `src/runtime/src/jit/helpers_object.rs` | MODIFY | `jit_obj_new` 镜像同上 |
| `src/runtime/src/metadata/types.rs` 或新增 `default_value.rs` | NEW or MODIFY | `default_value_for(&str) -> Value` 单一入口 |
| `src/runtime/tests/golden/run/class_field_default_init/source.z42` | NEW | golden source |
| `src/runtime/tests/golden/run/class_field_default_init/expected_output.txt` | NEW | golden 期望输出 |
| `src/runtime/tests/golden/run/class_field_default_init/source.zbc` | NEW | regen 产物 |
| `src/compiler/z42.Tests/ClassFieldInitTypeCheckTests.cs` | NEW | TypeCheck 单元测试（instance init 类型检查 / 类型不匹配报错） |
| `docs/design/class.md` 或 `docs/design/language-overview.md` | MODIFY | 同步字段默认值规则（条件性，见 design.md） |
| `docs/roadmap.md` | MODIFY | 标注 fix 完成 |

**只读引用**：

- `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.Members.cs` — `ResolveCtorName` 的"无显式 ctor"路径理解
- `src/compiler/z42.IR/IrModule.cs` — `ObjNewInstr` 字段 / `IrFunction` 结构
- `src/runtime/src/metadata/bytecode.rs` — 确认 `FieldDesc.type_tag` 已存在
- `src/runtime/tests/golden/run/07_class_basic/source.z42` — 既有 class 测试基线

## Out of Scope

- ❌ 不引入 readonly / property auto-default / record 默认值等新语法
- ❌ 不修改静态字段 init 流程（`__static_init__` 已正确）
- ❌ 不修改继承时 base class 字段 init 的执行顺序（保持"base ctor 先跑、子类字段 init 后跑"的 C# 语义）
- ❌ 不为 generic class type-param 字段加专门规则（`T` 字段统一 Null，未来 generic 默认值另起 spec）
- ❌ 不优化重复字段 init 的代码大小（每个 ctor 重复发射，未来可考虑提取共享 init 函数）

## Open Questions

- [ ] **隐式 ctor 与显式 base 类**：`class Box : Parent { int n = 5; }` 如果 Parent 没有零参 ctor，Box 不能合成无参隐式 ctor → 报错 vs 跳过合成？建议**报错**（DiagnosticCodes.NeedsExplicitConstructor），与 C# 一致。
- [ ] **字段 init 顺序 vs base ctor**：C# 的顺序是"字段 init → base ctor → 用户 ctor body"。z42 现有实现是"base ctor → 用户 body"。本次 fix 选用 C# 顺序，还是保持 z42 简化版（base ctor → 字段 init → 用户 body）？建议**简化版**（base 先跑，避免字段 init 引用未初始化的 base 状态），明确写入 design.md。
