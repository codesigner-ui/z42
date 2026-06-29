# Tasks: 反射调用原语（Method.Invoke / Type.GetType / Activator.CreateInstance，非泛型）

> 状态：🟢 已完成 | 创建：2026-06-29 | 完成：2026-06-30
> 里程碑：0.3.12 | 前置：boxing 0.3.11 ✅（ea2ba2a0）

## 进度概览
- [x] 阶段 1: 异常传播核实 — D3 路径定为 ctx.pending_thrown（builtin 透出原异常值，interp exec_call::builtin + jit jit_builtin 两路径消费）
- [x] 阶段 2: Type.GetType + ~~Activator.CreateInstance~~（Activator 延后，见 proposal 更正）
- [x] 阶段 3: MethodInfo.Invoke（__method_invoke builtin，复用 exec_function）
- [x] 阶段 4: 测试 — golden src/tests/reflection/method_invoke（static/instance/void/throw-catch）
- [x] 阶段 5: 文档 — reflection.md + roadmap
- [x] 阶段 6: GREEN — interp 190/0（含 method_invoke）+ jit method_invoke 直验通过

> **实证更正**：①Activator.CreateInstance **延后**（obj_new 是指令处理器不便复用 + ctor 命名约定，
> 风险高；实例方法 Invoke 用普通 new 建实例即可测）——见 Deferred add-method-invoke-future-activator。
> ②**异常传播**走 ctx.pending_thrown：builtin 遇 Thrown 存原异常值 + bail，exec_call::builtin（interp）
> 与 jit_builtin（jit）的 Err 处理都先 take_pending_thrown 透出原类型（否则被包成 Std.Exception）。
> ③**Scope 补充（root-cause 修复）**：golden runner `_runVmGoldens` 此前不设 Z42_LIBS，继承 xtask
> apphost 的陈旧 Z42_LIBS（如 .z42/libs）→ 用新 stdlib 函数（Std.Type.GetType）的 golden 运行期报
> "undefined function"。修：`scripts/xtask_test_vm.z42` 把 Z42_LIBS 钉到 fresh `_libsDir(root)`。
> 这是 boxing 没暴露（未加新 stdlib 函数）的既有 harness 隐患，由本变更触发。
> 子系统：runtime（reflection builtin）+ stdlib（z42.core API）。开工前查 ACTIVE.md 登记（全空闲才开）。

## 进度概览
- [ ] 阶段 1: 核实 builtin 异常传播机制（Thrown 能否经 NativeFn 可 catch 传播 → 定 D3 实现路径）
- [ ] 阶段 2: Type.GetType + Activator.CreateInstance（最简，复用 make_type_from_name / ObjNew）
- [ ] 阶段 3: MethodInfo.Invoke（核心：arg 组装 + exec_function + 返回/异常）
- [ ] 阶段 4: 测试（Rust 单测 + golden 端到端）
- [ ] 阶段 5: 文档同步
- [ ] 阶段 6: GREEN 验证

## 阶段 1: 异常传播核实
- [ ] 1.1 读 builtin 调用点（exec_call::builtin）+ ExecOutcome::Thrown 传播：NativeFn 返回 `Result<Value>` 时，被调函数 throw 如何回到 z42 try/catch？
- [ ] 1.2 定 D3 实现路径：若纯 NativeFn 无法传播可 catch 异常，Invoke 走能返回/传播 ExecOutcome 的内部入口

## 阶段 2: Type.GetType + Activator.CreateInstance
- [ ] 2.1 `reflection.rs`：`builtin_type_get_type`（包装 make_type_from_name；未知→Null）
- [ ] 2.2 `reflection.rs`：`builtin_activator_create`（包装 ObjNew 无参 ctor）
- [ ] 2.3 `mod.rs` BUILTINS 注册 `__type_get_type` / `__activator_create`
- [ ] 2.4 z42 声明：Type.z42 `GetType(fqn)`；新 `Reflection/Activator.z42`（Std.Activator.CreateInstance）
- [ ] 2.5 Rust 单测：GetType 已知/未知；CreateInstance 无参实例 + 字段默认

## 阶段 3: MethodInfo.Invoke
- [ ] 3.1 `reflection.rs`：`builtin_method_invoke`
      - 读 MethodInfo `__qualified` + `IsStatic`
      - 解析 Function（func_index / try_lookup_function）
      - 组装 call_args（实例 obj→[0]；展开 object[]）
      - arity 比对（不符抛带信息异常，D4）
      - exec_function → Returned(Some/None)→v/Null；Thrown→传播（D3）
- [ ] 3.2 `mod.rs` 注册 `__method_invoke`
- [ ] 3.3 z42 声明：MethodInfo.z42 `[Native("__method_invoke")] object Invoke(object, object[])`
- [ ] 3.4 Rust 单测：static / instance / void→null / arity 不符抛 / 被调 throw 传播

## 阶段 4: 测试
- [ ] 4.1 golden `src/tests/reflection/method_invoke/`：GetType→CreateInstance→Invoke（static+instance+返回值+void+被调 throw 经 catch）
- [ ] 4.2 spec scenarios 逐条覆盖

## 阶段 5: 文档
- [ ] 5.1 `reflection.md`：Invoke/GetType/Activator（非泛型）段 + 移出对应 Deferred（标 0.3.12 落地）
- [ ] 5.2 `roadmap.md`：0.3.12 Method.Invoke 打勾
- [ ] 5.3 Deferred Backlog：add-method-invoke-future-generic / -ctor-args

## 阶段 6: GREEN
- [ ] 6.1 `cargo build --release`（z42vm）无错
- [ ] 6.2 reflection/exec Rust 单测全绿
- [ ] 6.3 `./xtask test vm`（interp+jit，含新 golden）无回归
- [ ] 6.4 ACTIVE.md 释放锁；归档

## 备注
- 纯 additive（新 builtin + 新 API），无编译器改动 → 自编译不动点不涉及。
- 下游 retire-test-runner（0.3.13）消费本变更原语，不在本 scope。
