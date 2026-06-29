# Proposal: 反射调用原语（Method.Invoke / Type.GetType / Activator.CreateInstance，非泛型）

> 状态：DRAFT（待 User 审批）
> 里程碑：0.3.12（前置：boxing 0.3.11 ✅ ea2ba2a0）
> 子系统：runtime（reflection builtin）+ stdlib（z42.core 反射 API 声明）

## Why

退役 Rust test-runner（`retire-test-runner`，0.3.13）要用 z42 原生反射重写 runner：
`Type.GetType(fqn)` → `TestDiscovery.FindTests(Type)` → 实例化测试类 → `MethodInfo.Invoke`。
当前反射只读元数据齐全（C1–C3 + GetMethods/GetParameters/attributes），但**缺三个调用原语**：

- `MethodInfo.Invoke(object obj, object[] args)` —— **完全缺失**（反射执行方法）；
- `Type.GetType(string fqn)` —— **缺失**（FQN 字符串 → Type；只有 typeof(T)/obj.GetType()）；
- 反射实例化（无参构造）—— 无 `Activator`；实例方法 Invoke 需要一个 `obj`。

boxing（0.3.11）已落地，`object[]` 现可装/传原始值实参 —— Invoke 的直接前置已满足。

不做（划走）：
- **泛型方法 Invoke / MakeGenericType / Activator.CreateInstance<T>**：依赖运行期泛型实例化，0.4.x G 流。
- **有参 Activator.CreateInstance(Type, args)**（重载决议构造）：本次只无参；有参延后。
- **ref/out 参数 Invoke**：延后（需 impl-ref-out 运行期）。
- **z42.test TestRunner v2 / z42b test verb**：retire-test-runner（本变更只提供其依赖的调用原语）。

## What Changes

1. **`MethodInfo.Invoke(object obj, object[] args) → object`**：新 builtin `__method_invoke`。
   从 MethodInfo 的 `__qualified` 解析 `Function` → 组装实参（实例方法 obj 作第 0 参数，静态方法
   obj 传 null）→ 展开 object[] → 复用 `exec_function` 执行 → 返回值（void → null；异常 → 传播为
   可被 z42 catch 的 throw，经 `ExecOutcome::Thrown`）。
2. **`Type.GetType(string fqn) → Type`**：新 builtin `__type_get_type`，复用现成
   `make_type_from_name(ctx, fqn)`（主模块 type_registry + lazy loader + 合成类型）；未知 → null。
3. **`Activator.CreateInstance(Type) → object`**（无参构造）：新类 `Std.Activator` + builtin
   `__activator_create`，复用现成 `ObjNew`（分配 + 找无参 ctor + 执行）。

## 前置依赖
| 前置 | 状态 |
|------|------|
| boxing（prim↔object，object[] 传原始值）| ✅ 0.3.11（ea2ba2a0）|
| 反射只读元数据 + GetMethods/GetParameters/attributes | ✅ C1–C3 + 扩展 |
| `make_type_from_name` / `ObjNew` / `exec_function` 基建 | ✅ 已存在 |

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/corelib/reflection.rs` | MODIFY | 新增 `builtin_method_invoke` / `builtin_type_get_type` / `builtin_activator_create` |
| `src/runtime/src/corelib/mod.rs` | MODIFY | BUILTINS 注册 `__method_invoke` / `__type_get_type` / `__activator_create` |
| `src/libraries/z42.core/src/Reflection/MethodInfo.z42` | MODIFY | 加 `[Native("__method_invoke")] public extern object Invoke(object obj, object[] args);` |
| `src/libraries/z42.core/src/Type.z42` | MODIFY | 加 `[Native("__type_get_type")] public static extern Type GetType(string fqn);` |
| `src/libraries/z42.core/src/Reflection/Activator.z42` | NEW | `Std.Activator`：`[Native("__activator_create")] public static extern object CreateInstance(Type t);` |
| `src/runtime/src/corelib/reflection_tests.rs`（或对应）| MODIFY/NEW | Rust 单测：static/instance Invoke + void + 异常传播 + GetType + CreateInstance |
| `src/tests/reflection/method_invoke/`（golden）| NEW | 端到端：GetType→CreateInstance→Invoke（static + instance + void + 返回值 + 抛异常 catch）|
| `docs/design/language/reflection.md` | MODIFY | Invoke/GetType/Activator 段（非泛型）+ 移出对应 Deferred |
| `docs/roadmap.md` | MODIFY | 0.3.12 Method.Invoke 打勾 |
| `docs/spec/changes/ACTIVE.md` | MODIFY | 登记本变更子系统占用 |

**只读引用**：
- `src/runtime/src/interp/exec_object.rs`（ObjNew）/ `interp/mod.rs`（exec_function）/ `corelib/reflection.rs`（make_type_from_name / build_method_info）
- `docs/spec/changes/retire-test-runner/design.md`（D1 使用路径）

## Out of Scope
- 泛型方法 Invoke / MakeGenericType / Activator.CreateInstance<T>（0.4.x G）
- 有参 CreateInstance（重载决议）；ref/out 参数；z42.test runner / z42b test verb（retire-test-runner）

## Open Questions
- [ ] Invoke 时 obj/args 类型与签名不符（实参数/类型 mismatch）：抛 `TargetException`/`ArgumentException` 还是 `InvalidCastException`？→ design 定（倾向：arity 不符抛带信息异常；元素类型不符由被调内部 Convert/赋值处理）。
- [ ] 异常传播：被调方法 throw 时，Invoke 是否原样传播（可 catch）？→ design 定（倾向：经 `ExecOutcome::Thrown` 原样传播，z42 可 try/catch；区别于 boxing 拆箱的不可捕获 Convert 错误）。
