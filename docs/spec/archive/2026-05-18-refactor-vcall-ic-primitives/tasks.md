# Tasks: extend vcall IC to primitive receivers (L2)

> 状态：🟡 进行中 | 创建：2026-05-17 | 类型：refactor（runtime VM 内部 + 1 处常量声明）
> Spec 类型：minimal mode

## 背景

L1 (typed extractors) 已 dogfood 完。L2 计划"inline-cached call sites"原本以为
是大手术，但分析后发现 **大部分已经实现**（`metadata/resolver.rs` 在 module load
时 walk 所有 IR sites + 填 `Function.resolved.{method_tokens, builtin_tokens,
type_tokens, vcall_ic, field_ic, static_field_tokens}`，dispatch hot path 都走
`Vec` 直接 index）。

**剩下的真缺口**：`exec_vcall.rs:106-138` 的 primitive receiver 路径。`vcall_ic`
当前只对 `Value::Object(rc)` 触发；`Value::I64.CompareTo` / `Value::Str.Length`
/ `Value::Char.ToString()` 等通过下面这段绕过 IC：

```rust
if let Some(class_name) = primitive_class_name(&obj_val) {  // 5 fixed strings
    let primary  = format!("{}.{}", class_name, method);    // ← heap alloc per call
    let overload = format!("{}.{}${}", class_name, method, arity);  // ← second alloc
    for func_name in [primary.as_str(), overload.as_str()] {
        if let Some(&idx) = module.func_index.get(func_name) {  // ← HashMap lookup
            ...
```

热路径影响：`Bench.StringOps` 类 hot loop 里每 iter 都重复同一个 primary 字符串
constructing + lookup。`s.Length` 在 500k iter 里就是 500k × (2 format! + 2 HashMap.get)。

## 设计决策

| Decision | 选项 | 决定 | 理由 |
|----------|------|------|------|
| 1. primitive 用什么 TypeId | 在现有 type_registry 注册 / synthetic 常量 | synthetic 常量（`PRIM_I64/F64/...`） | type_registry 只对 user-defined classes；primitive 没 TypeDesc，不该硬塞 |
| 2. synthetic ID 取值 | 高位（≈ 0xFFFE_xxxx）/ 低位 | 高位 | 低位与真 TypeId（从 0 递增）潜在冲突；高位与 UNRESOLVED (0xFFFFFFFF) 留 margin |
| 3. cache slot 复用 | 复用现有 `VCallIC.{cached_type_id, cached_fn_idx}` / 新加 primitive-specific slot | 复用 | 同样的"type match + fn idx"语义；slot 只多承载 5 个 synthetic types |
| 4. miss 路径 | 保留 format!() / 改 const-string | 保留 format!() | 5 个 primitive class names × N 个 method names 组合空间小但不闭合；miss 路径成本不在 hot path |
| 5. polymorphic site | 退到 slow path / 同 object IC 覆写 | 同 object IC 行为：覆写 | 同时支持 i64+str 走同一 site 罕见但允许（如 `Assert.Equal<T>` 内 `expected.Equals(actual)`）；polymorphic site IC 命中率低但语义正确 |
| 6. 不动 dispatch ABI | yes | yes | `vcall()` 入参 / 返回值 / `Function.resolved.vcall_ic` 类型都不变；纯实现优化 |

## 实施

- [ ] 1.1 MODIFY `src/runtime/src/metadata/tokens.rs`
  - 加 `PRIM_TYPE_BASE = 0xFFFE_0000` + 5 个 `PRIM_I64/F64/BOOL/CHAR/STR` 常量（也加 STD_ARRAY 一个，处理 `[].Length` 等数组路径）
- [ ] 1.2 MODIFY `src/runtime/src/interp/exec_vcall.rs`
  - 加 `value_synthetic_type_id(&Value) -> Option<u32>`（primitive + Array → Some(PRIM_*)，object → None）
  - 修 fast-path IC check（line 70）：放宽到接受 `value_synthetic_type_id` 命中的情况，不只是 `Value::Object`
  - 修 primitive_class_name path（line 106-138）：成功 resolve 后写回 `vcall_ic.{cached_type_id = synthetic_id, cached_fn_idx = idx}`

## GREEN

- [ ] 2.1 `cargo build --release` clean
- [ ] 2.2 `cargo test --release` 全过（392 lib + 子 suite）
- [ ] 2.3 `dotnet test src/compiler/z42.Tests` 全过（1288）
- [ ] 2.4 `./scripts/test-stdlib.sh` 全绿

## 归档

- [ ] 3.1 mv → `docs/spec/archive/2026-05-17-refactor-vcall-ic-primitives/`
- [ ] 3.2 commit + push

## 实施期发现

1. **平行 session 反复反掉 runtime 改动**：本对话内 L1 / L2 都有过被静默 revert
   的经历（runtime 文件被外部 session 用 git checkout 类操作覆盖）。最终通过
   cherry-pick (L1) + 二次重做 (L2) 救回。Backlog 候选：runtime/* 加 CODEOWNERS
   或 branch protection 减少这种冲突。
2. **Pre-existing failures 与本 spec 无关**：commit 时 dotnet test 有 10 个
   failures，但 stash 验证后确认全部是 pre-existing（parallel stdlib 工作 +
   with-tidx FormatGoldenTests fixture 漂移）：
   - 7 个 GoldenTests (z42.math/text/collections/test) — Std.Cli/Std.Test
     namespace not loaded（flat view stale by parallel rename work）
   - 3 个 Z42.Tests.Zbc.FormatGoldenTests (`with-tidx` fixture) — 平行 TIDX
     工作的 fixture 漂移
3. **cargo test 全绿**：392 lib + 子 suite（4/10/4/8/3）全过；L2 的 IC 改动
   不破坏任何 Rust 单测。
