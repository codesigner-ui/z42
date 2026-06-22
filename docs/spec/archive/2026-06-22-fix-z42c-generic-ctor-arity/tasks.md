# Tasks: fix-z42c-generic-ctor-arity

**变更说明：** z42c `SymbolTable.ResolveTypeP` 解析泛型实例化 `C<T>` 时，对 **arity-overloaded
同名类**（arity-0 base + arity-N 泛型，shadow-only mangling `Name$N`）误取 arity-0 base →
`new MulticastException<TResult>(...)` 实际构造/抛出非泛型 `MulticastException` base →
消费端 `catch (MulticastException<int>)` 不匹配 → 异常逃逸。
**原因：** dogfood S3 暴露——`multicast_func_aggregate` / `multicast_predicate_aggregate`
golden 在 z42c-built stdlib 下抛未捕获 `Std.MulticastException`（C#-built 正确抛泛型）。
**文档影响：** docs/design/compiler/self-hosting.md（generic arity 解析 parity）。

> 状态：🟢 已完成 | 完成：2026-06-22

- [x] 1.1 `SymbolTable.ResolveTypeP`：NamedType 且 `nt.ArgCount > 0` 时优先查 `Name$N`（primary + 限定名回退两路径）→ `Z42InstantiatedType(gdef,...)`；未命中回退 bare（Box<T> 等非 overloaded 不受影响）
- [x] 1.2 验证：z42c rebuild + z42c build stdlib → `multicast_func_aggregate` + `multicast_predicate_aggregate` 在 debug z42vm interp 输出 MATCH（泛型 catch 命中）；compiler-z42 byte-identical 7/7 + 17 units 不回归
- [x] 1.3 docs/design/compiler/self-hosting.md 同步（generic arity-overload `new`/throw 解析）

## 备注
- 镜像 TypeChecker.z42:49-51 既有 arity-mangle 取 CT 逻辑。
- 与 S3（dogfood-z42c-stdlib-build）的 multicast 阻塞直接对应；本 fix 解除其一。BLID sidecar 阻塞仍在（独立）。
