# Tasks: add-multicast-exception-generic (D-8b-1)

> 状态：🟢 已完成 | 创建：2026-05-07 | 完成：2026-05-07 | 类型：feat(stdlib + compiler) — minimal 模式

**变更说明**：新增 stdlib `Std.MulticastException<R> : MulticastException`，携带 `Results: R[]` 字段；为 `MulticastFunc<T,R>.Invoke(continueOnException=true)` 的"返回 R[] 占位 + 失败聚合"语义打基础（design.md K8）。

**原因**：deferred.md D-8b-1 — D-8b-0 (class arity overloading) 解锁后，同名 `MulticastException` (non-generic) 与 `MulticastException<R>` (generic) 现可共存（前者已存在为 D2d-2-Action 落地，后者为本变更新增）。

**前置依赖（已解除）**：
- D-8b-0：class arity overloading shadow-only mangling ✅ 2026-05-07 落地
- D-8b-2：catch-by-generic-type 类型过滤 ✅ 2026-05-06 落地

**文档影响**：[docs/design/delegates-events.md](../../docs/design/delegates-events.md) §7 + [docs/deferred.md](../../docs/deferred.md)

---

## Tasks

- [x] 1.1 [src/libraries/z42.core/src/Exceptions/MulticastException.z42](../../src/libraries/z42.core/src/Exceptions/MulticastException.z42) 增 `class MulticastException<R> : MulticastException` —
      字段 `Results: R[]`；构造器接受 `Exception[] failures, int[] indices, int totalHandlers, R[] results` 四参 + `: base(failures, indices, totalHandlers)` ctor delegation；override `ToString()` 在父类前缀基础上加 results 长度
- [x] 1.2 [src/tests/exceptions/multicast_exception_generic/](../../src/tests/exceptions/multicast_exception_generic/) golden — 构造 `MulticastException<int>` + 访问 Results / Failures / TotalHandlers + `SuccessCount()` 走父类 method + ToString 走子类 override
- [x] 1.3 验证：`./scripts/build-stdlib.sh` 重新编译 z42.core.zpkg；现有 multicast_exception_aggregate golden 不破
- [x] 1.4 [docs/design/delegates-events.md](../../docs/design/delegates-events.md) §7 加 `MulticastException<R>` 类型签名 + 实施记录
- [x] 1.5 [docs/deferred.md](../../docs/deferred.md) D-8b-1 移到"已落地"
- [x] 1.6 全套验证（dotnet test 1093/1093 ✅ + test-vm interp 147/147 + jit 143/143 ✅ + cargo test ✅）
- [ ] 1.7 commit + push + 归档 spec → `spec/archive/2026-05-07-add-multicast-exception-generic/`

---

## 实施备注

### Out of Scope（本变更不做）

- **`MulticastFunc<T,R>.Invoke(continueOnException=true)` 改造**：让其抛 `MulticastException<R>` 替代当前抛非泛型 `MulticastException`；属于 design K8 完整语义（含 default(R) 填占位），需要 D-8b-3 Phase 2 generic-T `default(T)` 支持先到位
- **`MulticastPredicate.Invoke` 改造**：同上，待 Phase 2 跟进
- **AggregateException 泛型化**：`AggregateException<R>` 不在本变更范围

### 实施期发现并修复的两个跨阶段降级 bug

**症状**：`new MulticastException<int>(...)` 后所有字段（`Failures` / `Results`）都是 null —— ctor 没被调用。

1. **TSIG producer emit 注册键而非源码名**：`ExportedTypeExtractor.ExtractClasses` `foreach (var (name, ct) in sem.Classes)` 中 `name` 是 SymbolCollector 注册键（D-8b-0 mangled 后形如 `Foo$N`），但 emit 时 `new ExportedClassDef(name, ...)` 把这个 mangled key 当作类的 `Name` 字段写进 zpkg。consumer `ImportedSymbolLoader` 重建 `Z42ClassType` 时 `cls.Name = "Foo$N"`，但其 `Methods` 字典里 ctor 的键仍是源码裸名 `Foo`。`ResolveCtorName(qualName="Foo$N")` 用 `cls.Name` 做 `ctorBaseName` 查 `cls.Methods["Foo$N"]` → 找不到 → `hasExplicitCtor=false` → IR 不 emit `ctor=`，VM 跳过 ctor 调用。
   **修复**：`ExtractClasses` 改 emit `ct.Name`（源码裸名）；consumer `ImportedSymbolLoader` 在 `byName` 预扫时按 ExportedClassDef.Name 分组，发现同名 + 不同 arity → 加入 `importMangleNames` 集合 → 通过 `ImportKey()` 在导入注册时 re-apply `Name$N` mangle，同时 `BuildClassSkeleton` 把 `HasArityMangle = (importKey != cls.Name)` 写进 `Z42ClassType`，让消费者 IrName 仍走 `Foo$N`。
2. **FunctionEmitter ctor 检测漏 mangled className**：`isCtor = !isStatic && method.Name == className` 在 IR-side `className` 是 mangled (`Foo$N`) 时 false，导致 base ctor 调用 + 字段初始化都跳过。
   **修复**：emit 端先 `var sourceClassName = className.Contains('$') ? className[..className.IndexOf('$')] : className;` 再比对。

两个修复均属"症状级补丁 → 根因修复"路径：消费者拿到的 `Z42ClassType` 名字与方法键现物理对齐，不再依赖每个消费点手工去 mangled。

### 验证场景

| 场景 | 验证位置 |
|------|---------|
| `new MulticastException<int>(failures, indices, total, results)` 构造成功 + 父类字段 `Failures` 由 base ctor 设置 | golden multicast_exception_generic（"Failures not null"）|
| `Results: R[]` 字段由子类 ctor 设置 + 类型正确 | golden（"Results not null"）|
| 父类字段 `TotalHandlers` 跨继承访问 | golden 输出 `3` |
| 父类 `SuccessCount()` method 跨继承调用 | golden 输出 `2` |
| 子类 override `ToString()` 路由到子类版本 | golden 输出 `MulticastException<R>: 1 of 3 handlers failed; 3 results` |
| 与非泛型 `MulticastException` 共存（D-8b-0 验证） | 现有 multicast_exception_aggregate golden 不破 |
