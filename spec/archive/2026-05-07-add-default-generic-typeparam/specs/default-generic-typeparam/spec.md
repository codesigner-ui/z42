# Spec: default-generic-typeparam (Phase 2)

## ADDED Requirements

### Requirement: `default(T)` resolves at runtime when T is a generic type-parameter of the receiver class

#### Scenario: Field type access in method body of generic class

- **WHEN** 用户在 `class Foo<R>` 的 instance method body 写 `R x = default(R);` 且 R 是 Foo 的 type-parameter
- **THEN** TypeChecker 通过（不再报 E0421），IrGen emit `DefaultOf(dst, idx)`（idx = R 在 Foo.TypeParams 中的 0-based index）
- **AND** VM interp dispatch `DefaultOf`：从 `frame.regs[0]` 取 `this`，走 `Object → type_desc.type_args[idx]` 拿到 resolved type tag（如 `int`），调 `default_value_for("int")` → `Value::I64(0)` 写入 dst
- **AND** 同一指令在 `Foo<string>` 实例上跑产出 `Value::Null`（string 的 zero）

#### Scenario: Field initializer with generic-T default

- **WHEN** `class Foo<T> { public T x = default(T); }` 中 ctor 自动 init `x` 字段
- **THEN** ctor 体内合成的 field-init 走 `DefaultOf(field_slot_reg, idx_of_T)`
- **AND** `new Foo<int>()` 后 `obj.x == 0`，`new Foo<string>()` 后 `obj.x == null`

#### Scenario: Multiple type-params, second-position lookup

- **WHEN** `class Pair<K, V> { V getDefaultV() { return default(V); } }` 跑在 `Pair<int, string>` 实例上
- **THEN** `DefaultOf(dst, 1)` （V 是 type_params 第 2 个）解析 `type_args[1]` = `"string"` → `Value::Null`

#### Scenario: Inherited generic-T default via base class

- **WHEN** `class Bar<R> : Foo<R>` 调用父类 method `Foo<R>.GetDefault()` 内含 `default(R)`
- **THEN** 子类实例 `new Bar<int>().GetDefault()` 时 `this.type_desc` 是 Bar 的 desc，但其 `type_args` 链路应保留 `R = int`（依赖现有 generic 继承体系；Phase 2 不引入新机制）
- **AND** 若现有 generic 继承 type_args 传递不完整，本 spec 报告并升级为 follow-up（不阻塞 Phase 2 主体）

### Requirement: `default(T)` outside in-class type-param scope gracefully degrades

#### Scenario: Generic free function (no `this`)

- **WHEN** `void GenericFunc<T>() { T x = default(T); }` 被调用
- **THEN** TypeChecker 通过（不报 E0421，与 in-class 路径一致）
- **AND** IrGen emit `DefaultOf(dst, 0)`（参数 index 默认 0；method-level type-param 的 idx 当前无 codegen 路径）
- **AND** VM interp 检测 `frame.regs[0]` 不是 `Value::Object` 或 `type_args` 为空 → 退化为 `default_value_for("unknown")` = `Value::Null`
- **AND** 不抛异常 / 不 panic

#### Scenario: Method-level type-param on non-generic class

- **WHEN** `class Foo { void m<U>() { U x = default(U); } }`
- **THEN** 同 generic free function：编译通过 + 运行时退化为 Null
- **AND** 单元测试明确记录这是 graceful-degradation 边界（不是 supported feature）

#### Scenario: Static method on generic class

- **WHEN** `class Foo<T> { static T defaultT() { return default(T); } }`
- **THEN** static method 无 `this`，走 graceful-degradation 同上

### Requirement: Existing fully-resolved `default(T)` 路径不受影响

#### Scenario: Phase 1 primitive / class / array path 保持

- **WHEN** `default(int)` / `default(MyClass)` / `default(int[])` / `default(Foo<int>)`
- **THEN** TypeChecker 仍走 Phase 1 路径（resolve 出 concrete type），IrGen 仍 emit Phase 1 lowered Const* 指令（ConstI64(0) / ConstNull / ...）
- **AND** 不 emit DefaultOf，不依赖 runtime type_args 查表

## MODIFIED Requirements

### Requirement: E0421 InvalidDefaultType 仅对真未知类型 / 不支持构造触发

**Before**: `Z42GenericParamType` → E0421 "default(<T>) on generic type parameter is not yet supported (Phase 2 deferred)"

**After**: `Z42GenericParamType` 不再触发 E0421；改为 emit `DefaultOf` 指令进入 runtime 解析路径。E0421 保留但仅对真正不支持的 target type 触发（如 void、`Z42UnknownType` after resolve fail）。

## IR Mapping

新 IR 指令：

```
DefaultOf  dst=Reg  param_index=byte
```

- 操作数 `param_index`：当前 receiver class 的 `type_params` 数组中的 0-based 索引
- 编码：`opcode_byte (0xA3) | type_tag (Ref/I64/...) | dst_reg (u16) | param_index (u8)`
- 实际 emit 时机：仅在 `BoundDefault` 的 target type 是 `Z42GenericParamType` 时；fully-resolved 路径仍走 Phase 1 Const*

`type_tag` 字段位置上写 `Ref`（ABI 上 IR 的 `dst.Type` 一般是 generic param 的 erased 类型 `Object`/`Ref`），运行时按 resolved type_args 重新决定 Value 形态。

## Pipeline Steps

- [x] Lexer — 不变（Phase 1 已加 `default` keyword）
- [x] Parser / AST — 不变（Phase 1 已加 `DefaultExpr` 节点）
- [ ] TypeChecker — 解除 generic-param E0421 gate；记录 generic-param scope info（class-level vs method-level）
- [ ] IR Codegen — 新 `DefaultOfInstr` record + `BoundDefault → DefaultOfInstr` 路径
- [ ] zbc binary writer / reader — 加新 opcode 编 / 解码
- [ ] ZASM text writer — 加新 opcode 文本格式
- [ ] VM interp — 加 `DefaultOf` dispatch handler
- [ ] VM JIT — 加 `jit_default_of` helper + translate.rs 编码分支
- [ ] zbc 版本 0.8 → 0.9
