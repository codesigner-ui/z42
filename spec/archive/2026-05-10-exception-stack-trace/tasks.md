# Tasks: Exception Stack Trace (populate Std.Exception.StackTrace)

> 状态：🟢 已完成 | 完成：2026-05-10 | 创建：2026-05-10
> 类型：feat（最小化模式 + design 段）

**变更说明：** 在 z42 抛出 `Std.Exception`（或子类）实例时，自动生成调用栈快照写入其 `StackTrace` 字段；未捕获顶层异常输出该 trace。基础设施全在（`line_table` / `Frame` / `frame_states`），缺一个并行的 `call_stack: Vec<FrameInfo>` 跟踪 (func, file, line)。

**原因：** stdlib `Exception.StackTrace` 字段从首版起就存在但**永不填充**（注释明说 "Phase 1 恒为 null"）。这是 z42 调试体验最直观的痛点。

**Scope 限定：** 仅 interp 路径（JIT path 触发 throw 走相同 helper 才能感知 — 留作 follow-up 评估）。仅 Exception 子类享受自动 trace；`throw "foo"` 等 Phase 1 字符串 throw 保持现状（无 trace）。

## 阶段 1: VmContext call-stack 维护

- [x] 1.1 [src/runtime/src/vm_context.rs](../../src/runtime/src/vm_context.rs) 加 `FrameInfo { func_name, file, line: Cell<u32> }` + `call_stack: RefCell<Vec<FrameInfo>>`
- [x] 1.2 暴露 API：`push_call_frame(info)` / `pop_call_frame()` / `update_top_frame_line(line)` / `snapshot_call_stack() -> Vec<FrameInfo snapshot>`
- [x] 1.3 [src/runtime/src/interp/mod.rs](../../src/runtime/src/interp/mod.rs) `exec_function` 入口 push FrameInfo（file 取自 `func.line_table.first().file` 或 fallback `<unknown>`）；FrameGuard Drop 时同步 pop

## 阶段 2: 调用边界更新 caller line

需要 caller 在调用 callee 前记下"我此刻执行到哪行"，否则栈展示永远显示 line=0。

- [x] 2.1 在 Call / VCall / CallIndirect / CallNative 等 handler **进入 callee 前**，调 `ctx.update_top_frame_line(resolve_line(&func.line_table, block_idx, instr_idx))`
- [x] 2.2 抽公共助手避免每个 handler 重复（如 `frame_loc!(ctx, func, block_idx, instr_idx)` 宏 / `update_caller_loc` fn）

## 阶段 3: throw 时填 StackTrace

- [x] 3.1 [src/runtime/src/interp/mod.rs](../../src/runtime/src/interp/mod.rs) `Terminator::Throw { reg }` 分支：
  - 把 throwing 帧的 line 更新到当前 (block_idx, instr_idx)（便于 trace 顶部精确）
  - 调 `populate_stack_trace(&val, ctx, module)` 助手
- [x] 3.2 同样在 `Ok(Some(thrown_val))` 跨函数 propagation 分支处理（callee 已 throw 但当前帧无 handler）—— 但 callee 已经在自己的 throw 点写好了，外层只需透传。**确认是否需重复写**：理论不需，新 spec 只在最初 throw 点写一次。
- [x] 3.3 `populate_stack_trace` 实现：
  - 仅当 `val` 是 `Value::Object` 且 `type_desc` 是 `Std.Exception` 或其子类（walk base_name chain）
  - 仅当 `StackTrace` 字段当前为 `Value::Null`（避免覆盖手设值 / 重复填）
  - 通过 `field_index.get("StackTrace")` 取 slot，写入格式化字符串

## 阶段 4: trace 格式化 + uncaught 输出

- [x] 4.1 [src/runtime/src/exception.rs](../../src/runtime/src/exception.rs) NEW 或扩展 — `format_stack_trace(stack: &[FrameInfo]) -> String`
  - 格式：每帧 `  at <func_name> (<file>:<line>)`，按"最近 → 最早"顺序（顶层最先抛出）
- [x] 4.2 [src/runtime/src/interp/mod.rs](../../src/runtime/src/interp/mod.rs) `run` / `run_main` 顶层 ExecOutcome::Thrown 分支：
  - 如果 val 是 Exception 子类 + StackTrace 非 null：输出 `Unhandled exception: <Message>\n<StackTrace>`
  - 否则 fallback 现有 `bail!("uncaught exception: {}")`
- [x] 4.3 helper：`is_exception_subclass(type_desc, registry) -> bool`（walk base_name chain 直到 `Std.Exception` 或 None）

## 阶段 5: 测试

- [x] 5.1 [src/runtime/src/exception_tests.rs](../../src/runtime/src/exception_tests.rs) 或扩展 `vm_context_tests.rs` — call_stack push/pop/update API 单测
- [x] 5.2 端到端 golden / assert：
  - z42 程序 `throw new Exception("oops")` → catch → `Assert.NotNull(e.StackTrace)` + `Assert.Contains("at Demo.Foo", e.StackTrace)`
  - 嵌套调用 `Main → A → B → throw` → catch in Main → trace 含 3 帧
  - 重新抛出（rethrow）现有 Exception 不重写 StackTrace
  - 顶层 uncaught Exception 输出含 trace
- [x] 5.3 现有 exception 类 golden 不应回归（`exception_base` / `exception_subclass` / `nested_exceptions` / 等）—— stdlib `StackTrace = null` 默认值改成 trace string，可能影响 toString 测试。审计：是否有断言 `e.StackTrace == null` 的测试？预期无。

## 阶段 6: 文档 + 验证

- [x] 6.1 [src/libraries/z42.core/src/Exceptions/Exception.z42](../../src/libraries/z42.core/src/Exceptions/Exception.z42) 注释更新："Phase 1 恒为 null" → "interp throws populate at throw site"
- [x] 6.2 [docs/design/exceptions.md](../../docs/design/exceptions.md) 加 stack-trace 段（如已存在该文档）
- [x] 6.3 [docs/design/vm-architecture.md](../../docs/design/vm-architecture.md) 简短记录 call_stack 维护策略
- [x] 6.4 dotnet test + cargo test --lib + test-vm.sh 全绿

## Scope

| 文件 | 类型 | 说明 |
|---|---|---|
| `src/runtime/src/vm_context.rs` | MODIFY | call_stack + FrameInfo + API |
| `src/runtime/src/interp/mod.rs` | MODIFY | FrameGuard 同步 push/pop；Throw populate；uncaught 输出 |
| `src/runtime/src/interp/exec_call.rs` 或相关 | MODIFY | update caller line 在 Call handler |
| `src/runtime/src/interp/exec_object.rs` | MODIFY (if needed) | VCall / CallIndirect |
| `src/runtime/src/exception.rs` | MODIFY 或扩展 | format_stack_trace + is_exception_subclass |
| `src/runtime/src/exception_tests.rs` | MODIFY 或 NEW | 单测 |
| `src/libraries/z42.core/src/Exceptions/Exception.z42` | MODIFY | 注释更新 |
| `src/tests/exceptions/stack_trace_basic.z42` | NEW | golden + assert |
| `docs/design/vm-architecture.md` | MODIFY | call_stack 段 |
| `docs/design/exceptions.md` | MODIFY (if exists) | trace 段 |

**只读引用：**
- `func.line_table` / `resolve_line` — 已存在，无需改

## 备注

- **JIT 路径**：当前 spec 仅 interp。JIT 经 `set_exception` 抛出时不走 `Terminator::Throw`；要让 trace 在 JIT 也工作需要在 jit_set_exception 等 helper 里同样调 populate。先 interp 落地稳定再扩 — 留 follow-up。
- **性能**：call_stack push/pop O(1)；caller-line update 是 1 个 Cell::set per Call-instr；trace snapshot 仅在 throw 时；stack 深度通常 < 20。预期 < 0.5% interp slowdown。
- **format_stack_trace** 不复用 C# DiagnosticRenderer 风格 — 这是 stdout 输出，Plain 风格即可。颜色/Pretty 留 follow-up。
