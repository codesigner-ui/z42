# Design: 反射调用原语（非泛型）

> 三个 builtin glue 既有运行期基建（exec_function / make_type_from_name / ObjNew）。
> 下游 retire-test-runner 的 D1 路径见其 design.md。

## Architecture

```
z42.test / 用户代码
  Type.GetType("Demo.C")            → __type_get_type  → make_type_from_name(ctx, fqn)
  Activator.CreateInstance(t)       → __activator_create → ObjNew(分配 + 无参 ctor)
  m.Invoke(obj, object[]{...})      → __method_invoke
        ├─ MethodInfo.__qualified → ctx.module().func_index / try_lookup_function → Arc<Function>
        ├─ 组装 call_args：实例方法 obj 作 [0]；静态方法跳过；展开 object[] 元素（boxing：原始值已是 Value）
        ├─ exec_function(ctx, module, func, &call_args) → ExecOutcome
        └─ Returned(Some v)→v | Returned(None)→Null | Thrown(e)→传播（可 catch）
```

## Decisions

### D1: Invoke 复用 exec_function，不另起调用路径

`__method_invoke` 把 `(MethodInfo, obj, object[])` 归一成 `Vec<Value>` 实参，调用与普通 `Call`
指令**同一个** `exec_function`。好处：调用语义（frame 建立、ret、异常）零重复，与普通调用完全一致。
boxing（0.3.11）保证 object[] 里的原始值就是 `Value::I64` 等，直接落参数寄存器，无需额外拆箱。

### D2: 接收者与静态分发

读 `MethodInfo.IsStatic`：
- 静态：忽略 `obj`（约定传 null）；call_args = 展开的 object[]。
- 实例：call_args[0] = obj（接收者），其后接 object[]。与 z42 实例方法调用约定（reg0 = this）一致。

虚方法：`__qualified` 解析到的是声明类型的 Function；非泛型 MVP **按 MethodInfo 指向的具体方法
调用**（不做基于 obj 动态类型的 vtable 再分发）—— 与 `MethodInfo` 语义一致（它就是某个具体方法）。
动态派发若需要由调用方选对 MethodInfo。

### D3: 异常传播 —— 被调 throw 可 catch（区别于 Convert 错误）

被 Invoke 的方法体内 `throw` 经 `ExecOutcome::Thrown(exc)` **原样返回**，Invoke builtin 把它作为
z42 异常继续传播 → 调用方可 `try/catch`。这与 boxing 拆箱失败的 `Convert` 内部 `bail!`（不可捕获
VM 错误）**不同**：那是转换层错误，这是被调用户代码的正常异常。

> 实现要点：builtin 返回 `Result<Value>`；`Thrown` 需转成 VM 的可捕获异常通道而非 anyhow `bail!`。
> 若 builtin 层无法直接发可捕获异常，则 Invoke 需走能传播 `Thrown` 的路径（实施阶段核实 builtin
> 异常传播机制；必要时 Invoke 不走纯 NativeFn 而用能返回 ExecOutcome 的内部入口）。

### D4: 实参/形参不符的处理

- **arity 不符**：Invoke 前比对 `Function.param_count`（实例方法计入 this）与 call_args 长度，不符抛
  带信息异常（`TargetParameterCountException` 或 `ArgumentException`，措辞实施定）。
- **元素类型不符**：不在 Invoke 层逐个校验；落到被调函数内部的赋值/Convert 处理（与直接调用一致）。
  保持 Invoke 薄、不重复类型系统。

### D5: Type.GetType / CreateInstance 复用既有

- `__type_get_type` = 薄包装 `make_type_from_name(ctx, fqn)`（已支持主模块 + lazy loader + 合成
  类型）；未知返 Null。
- `__activator_create` = 薄包装 `ObjNew`（已做分配 + 无参 ctor 查找 + 执行 + ctor 异常传播）。
  仅无参；有参延后。

## Implementation Notes
- 三 builtin 加在 `reflection.rs`，`mod.rs` BUILTINS 注册（位置即 BuiltinId，追加在反射段尾）。
- z42 侧 `[Native("__xxx")]` extern 声明（同 MethodInfo.GetCustomAttributes 等现有模式）。
- 纯 additive：新 builtin + 新 API，不改既有行为 → z42c 自编译不动点不涉及（无编译器改动）。
- 无新语法 / 无格式 bump。

## Testing Strategy
- Rust 单测（reflection_tests）：static/instance/void Invoke + arity 不符抛 + GetType 已知/未知 + CreateInstance 无参。
- Golden（src/tests/reflection/method_invoke/）：端到端 GetType→CreateInstance→Invoke（static + instance + 返回值 + void + 被调 throw 经 catch）。
- GREEN：`cargo build` + `./xtask test vm`（含新 golden，interp+jit）+ exec/reflection 单测。
- 下游验证（非本变更）：retire-test-runner 用这些原语重写 runner。

## Deferred
### add-method-invoke-future-generic
- **触发原因**：泛型方法 Invoke / MakeGenericType / Activator.CreateInstance<T> 需运行期泛型实例化
- **前置依赖**：运行期泛型实例化（0.4.x G 流）
- **触发条件**：0.4.x G 流规划
### add-method-invoke-future-ctor-args
- **触发原因**：有参 Activator.CreateInstance(Type, object[])，需构造重载决议
- **触发条件**：有反射有参构造需求时
