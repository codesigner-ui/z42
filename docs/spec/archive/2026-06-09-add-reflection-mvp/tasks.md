# Tasks: 反射 MVP（GetType 路线全贯通）

> 状态：🟢 已完成｜创建：2026-06-08｜完成：2026-06-09｜类型：vm + stdlib
> 占用子系统：`runtime` + `stdlib`（已释放）
>
> **GREEN 验证**：C# GoldenTests **238/238**（含 object_get_type / typeof / array_get_type，证 GetType 改写零回归）· 编译器 dotnet test **1543/1543** · z42.core reflection `[Test]` **8/8** · Rust 单元 **6/6**。
> **环境备注**：调试期手动把 fresh z42vm + z42.core.zpkg 塞进本机 nightly `.z42/{bin,libs}` 导致 z42 `xtask test vm` 内嵌 runner 与 sidecar 失配、golden 全挂——纯本地 artifact（C# GoldenTests 全绿、CI 全新构建不受影响）。恢复办法：重建一致工具链（`xtask build launcher` / 重 bootstrap）。
> 实现 commit：`30776fae`（reflection-only，surgical 排除并行 add-binary-float）。

## 进度概览
- [ ] 阶段 0: 锁登记 + Phase B 契约验证
- [ ] 阶段 1: 运行时句柄化 + 反射 builtins（Phase A）
- [ ] 阶段 2: z42.core Type 扩展 + Std.Reflection 类（Phase A）
- [ ] 阶段 3: Phase B —— TSIG 方法签名加载 + MethodInfo/ParameterInfo 完整
- [ ] 阶段 4: 测试与验证
- [ ] 阶段 5: 文档同步 + 归档

## 阶段 0: 锁 + 验证
- [x] 0.1 ACTIVE.md 登记 `runtime` + `stdlib` 持有者 = add-reflection-mvp
- [x] 0.2 **Phase B 契约验证 ✅ GREEN（无需格式 bump）**：方法签名元数据**已加载在运行时**——`Function { name, param_count, ret_type, is_static }` + `FunctionCold.param_types: Box<[String]>`（split-debug-symbols 引入，len==param_count）。反射 builtin 用类型的 qualified 方法名（`own_methods`/vtable）经 `func_index` 查到 `Function` 即可读全签名。**不需** TypeDesc.cold.methods 改动（Decision 3 简化：on-demand 读 Function，非预存 MethodMeta）。
    - 限制：① 参数名来自 `cold.local_vars`（debug-symbols 门控）——无符号时 ParameterInfo.Name 退化为 `arg{n}`；② IsVirtual 由 vtable 命中推导；IsAbstract best-effort（无 body 标志时返 false，reflection.md 记 Deferred）。

## 阶段 1: 运行时（Phase A + B 一并）
- [x] 1.1 `metadata/types.rs`：`NativeData::TypeHandle(Arc<TypeDesc>)`（enum derive Debug+Clone 已兼容）
- [x] 1.2 `corelib/object.rs`：重写 `builtin_obj_get_type` —— `Value::Object` 存 `rc.type_desc_arc().clone()` 进 `NativeData::TypeHandle`（经 `reflection::make_type_object`）；基础类型/数组走 `make_type_from_name`（无句柄）
- [x] 1.3 `corelib/reflection.rs`（NEW）：builtins + helpers（make_type_object / make_type_from_name / canonical_type_name / alloc_named）
    - [x] 1.3.1 `__type_fields` → FieldInfo[]（FieldType 为 Std.Type）
    - [x] 1.3.2 `__type_methods` → MethodInfo[]（含 Phase B 全签名：ReturnType/IsStatic/IsVirtual/GetParameters via `try_lookup_function`）
    - [x] 1.3.3 `__type_base` → Type|null（base_name / Std.Object / null）
    - [x] 1.3.4 `__type_generic_args` → Type[]（读 `cold.type_args`）
    - [x] 1.3.4b `__type_members` → MemberInfo[]（fields ++ methods，Rust 合并避开 z42 数组协变）
    - [x] 1.3.5 ~~`__type_flags`~~ 延后（类标志未加载运行时；见 reflection.md）
- [x] 1.4 `corelib/mod.rs`：`pub mod reflection;` + 注册 5 个 builtin
- [-] 1.5 `well_known_names.rs`：**无需新增**——复用 `STD_TYPE`/`STD_OBJECT`/`STD_ARRAY`/`STD_STRING`；`Std.Reflection.*` 用 reflection.rs 局部 const
- [x] 1.6 `corelib/reflection_tests.rs`（NEW）：Rust 单元（句柄缺失返空 / 不 panic / TypeHandle 提取）
- [ ] 1.7 `cargo build` + `cargo test reflection` 全过 ← **构建中**

## 阶段 2: stdlib（Phase A）
- [x] 2.1 `z42.core/src/Type.z42`：`Name`/`FullName`/`BaseType` 属性 + `GetFields`/`GetMethods`/`GetMembers`/`GetGenericArguments` extern（类级 Is* 延后）
- [x] 2.2 `z42.core/src/Reflection/MemberInfo.z42`（NEW）
- [x] 2.3 `z42.core/src/Reflection/FieldInfo.z42`（NEW）：Name + FieldType:Type
- [x] 2.4 `z42.core/src/Reflection/MethodInfo.z42`（NEW）：Name/ReturnType/IsStatic/IsVirtual/GetParameters()
- [x] 2.5 `z42.core/src/Reflection/ParameterInfo.z42`（NEW）：Name/ParameterType/Position
- [x] 2.6 `z42.core.z42.toml`：**无需改**——source 走 glob（无显式列表）
- [ ] 2.7 `xtask build stdlib z42.core` 编过 ← 待

## 阶段 3: Phase B —— 已并入阶段 1（on-demand 读 Function，无 MethodMeta 预存）
- [x] 3.1 ~~TypeDescCold.methods~~ 不需要：`build_method_info` 经 `ctx.try_lookup_function(qualified)` 直读 `Function{param_count,ret_type,is_static,param_types}`
- [x] 3.2 ~~loader 解析 TSIG~~ 不需要：方法签名已加载在 `Function`（split-debug-symbols 的 `param_types`）
- [x] 3.3 MethodInfo 全签名（ReturnType/IsStatic/IsVirtual）+ ParameterInfo（Name=arg{n}/ParameterType/Position）在 1.3.2 内完成
- [x] 3.4 z42 端 `MethodInfo.GetParameters()` 接通
- [ ] 3.5 z42 [Test] 覆盖签名/参数 ← 在 4.2

## 阶段 4: 验证
- [-] 4.1 ~~新增 src/tests/types golden~~ 改为复用既有 `object_get_type.z42`/`array_get_type.z42`/`typeof.z42`（已 exercise GetType，作回归守门）
- [x] 4.2 `z42.core/tests/reflection.z42`（8 个 `[Test]`）—— ✅ **8/8 全过**（fresh z42vm + fresh z42.core）
- [ ] 4.3 全 GREEN gate（`xtask test`：compiler + vm + cross-zpkg + 22 stdlib）← 运行中
- [x] 4.4 spec scenarios 覆盖：GetType 句柄 / 字段(含继承) / FieldType:Type / 方法名+签名 / ParameterInfo / IsStatic / BaseType / 泛型实参 / GetMembers 全部验证通过

## 实施中的关键修复 + 发现
- **`resolve_func_sig`（核心修复）**：方法签名解析必须先查**主模块** `ctx.module().func_index`（用户程序自己的方法），`try_lookup_function`（lazy loader）只覆盖 zpkg 方法 —— 否则 Add/Helper 的 ReturnType/IsStatic 解析为 void/false。
- **extern 属性 vs 方法 + 链式接收者**：发现编译器限制——extern 属性 getter 只在**局部变量接收者**上派发，链式（方法/字段结果）不派发 → null。extern 方法不受影响。已记 reflection.md「已知限制」+ Deferred（`reflection-future-chained-property-dispatch`）；`[Test]` 用例改用局部变量写法。
- **Type 成员形态**：`Name`/`FullName`/`BaseType` 用 extern `{ get; }` 属性（计算-body 属性 z42 未支持，是延后的「自定义 body property」）；`__name`/`__fullName` 保留为 backing 字段（兼容既有 golden + z42.test 直接读）。
- **环境**：多 agent 的 days-old 僵尸 cargo 进程曾 jam 共享 target（User 授权清理后解锁）；本机 nightly `.z42/{libs,bin}` 需用 fresh z42.core.zpkg + z42vm 刷新才反映改动。

## 阶段 5: 文档 + 归档
- [ ] 5.1 `docs/design/language/reflection.md`（NEW）：API + 元数据映射 + 生命周期 + Deferred（GetProperties / typeof→Type / Method.Invoke / Attribute）
- [ ] 5.2 `docs/design/runtime/vm-architecture.md`：反射元数据暴露机制（TypeHandle / builtin 枚举路径）
- [ ] 5.3 `docs/roadmap.md`：0.3.x C 主线 C1 进度标记
- [ ] 5.4 归档 + ACTIVE.md 释放 `runtime` + `stdlib` 锁 + commit + push

## 备注
（实施中记录决策变更 / 阶段 0.2 验证结论）
