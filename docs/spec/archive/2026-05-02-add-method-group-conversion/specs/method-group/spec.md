# Spec: D1b — 方法组转换 + I12 调用站点缓存

## ADDED Requirements

### Requirement: Method group → delegate value with module-level cache

把顶层函数 / 静态方法的名字赋给 delegate 类型的变量时，编译器必须在 module-level static slot 缓存生成的 `Value::FuncRef`，避免每次执行该 site 时重新分配。

#### Scenario: 顶层函数 → delegate 缓存
- **WHEN** `int Helper() { return 42; } void Main() { IntFn0 f = Helper; var r = f(); }` 多次进入 Main
- **THEN** Codegen emit `LoadFnCached` 而非 `LoadFn`；首次调用时 cache slot 写入 `Value::FuncRef("Demo.Helper")`；后续命中直接 load slot

#### Scenario: 同一函数多 site 共享 slot
- **WHEN** 多个 call site 都把 `Helper` 转为 delegate（`IntFn0 a = Helper; IntFn0 b = Helper;`）
- **THEN** Codegen 把它们映射到同一个 cache slot id（去重）

#### Scenario: 静态方法 → delegate 缓存
- **WHEN** `class C { static int Foo() {...} } void Main() { IntFn0 f = C.Foo; }`
- **THEN** 行为同顶层函数；slot key 是 fully-qualified `Demo.C.Foo`

#### Scenario: 实例方法不在 v1 范围
- **WHEN** `class C { int Bar() {...} } var c = new C(); IntFn0 f = c.Bar;`
- **THEN** 编译报错（v1 仅支持静态方法组转换）；后续独立 spec 加 instance method group

### Requirement: Cache slot lifetime = module lifetime

cache slot 在 module 加载时按 zbc 元数据预分配；首次写入后值持久存在直到 module 卸载（z42 当前不卸载 module）。

#### Scenario: 跨函数调用稳定
- **WHEN** 函数 A 缓存 `Helper`，函数 B 后续也写 `Helper` 到 delegate
- **THEN** 两者命中同一 slot（共享去重），都直接 load 已缓存的 FuncRef

### Requirement: alias 直接 Call 路径不受影响

mono spec 的 alias direct-call 优化（`var f = Helper; f();`）已经把 BoundCall 折叠为 `Call("Demo.Helper", ...)`，根本不 emit LoadFn。该路径在 D1b 后保留 —— 直接 Call 路径**不需要**缓存。

#### Scenario: alias 立即调用仍直接 Call
- **WHEN** `var f = Helper; f();`
- **THEN** Codegen emit `CallInstr(dst, "Demo.Helper", args)`（不经 LoadFn / LoadFnCached）

### Requirement: 不需要缓存的 LoadFn 路径仍走 LoadFnInstr

某些场景没有 cache 收益（例如 lambda lifted name 在 EmitLambdaLiteral 走的 LoadFn —— lambda 字面量本身已经 lift 过，再 cache 只浪费 slot）。这些路径保留 LoadFnInstr。

#### Scenario: no-capture lambda 不走 cache
- **WHEN** `IntFn1 sq = (int x) => x * x;`
- **THEN** EmitLambdaLiteral emit `LoadFnInstr`（不是 cached），lambda lifted name 不分配 slot

## MODIFIED Requirements

### Requirement: BoundIdent → LoadFn 路径升级为缓存版

**Before**（mono spec 加的）：BoundIdent 解析为顶层函数 / 静态方法 → emit `LoadFnInstr(dst, qualName)`。

**After**：emit `LoadFnCachedInstr(dst, qualName, slotId)`，其中 slotId 由 IrGen 维护的全模块去重 dict 分配；首次见到该 fnName 时 `slotId = nextSlotId++`，后续命中复用。

### Requirement: zbc 模块 metadata 增加 cache slot 数

**Before**: `IrModule` / Rust `Module` 不包含 cache slot 信息。

**After**: 增加 `func_ref_cache_slots: u32` 字段，编码为 module header 的 1 个新字段；module 加载时调 `vm_ctx.alloc_func_ref_slots(n)` 预分配。

## IR Mapping

| 新指令 | 操作数 | 语义 |
|--------|--------|------|
| `LoadFnCached` | dst: TypedReg, fn_name: string, slot_id: u32 | 检查 `vm_ctx.func_ref_slots[slot_id]`：若为 `Value::Null` → 构造 `Value::FuncRef(fn_name)`、写入 slot、写入 dst；否则直接复制 slot 到 dst |

## Pipeline Steps

- [ ] Lexer / Parser / AST — 无变更
- [ ] TypeChecker — 无变更
- [ ] IR Codegen — BoundIdent → LoadFn 改为 LoadFnCached + slot 分配
- [ ] zbc 编码 / 解码 — 新 opcode + module-level slot count 字段
- [ ] VM interp — LoadFnCached 分支
- [ ] VM JIT — `jit_load_fn_cached` helper + translate match arm
