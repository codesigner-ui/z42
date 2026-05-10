# Tasks: JIT Stack Trace (parity with interp)

> 状态：🟢 已完成 | 完成：2026-05-10 | 创建：2026-05-10
> 类型：feat（最小化模式）
> 紧接：[2026-05-10-exception-stack-trace](../../archive/2026-05-10-exception-stack-trace/)

**变更说明：** 让 JIT 模式下的 throw 也填充 `Std.Exception.StackTrace`，并产生与 interp 一致的多帧调用链。复用上批已建好的 `VmContext.call_stack` + `populate_stack_trace`，仅需把 push/pop/line 三类钩子接到 JIT 的 entry/call/throw 位置。

**原因：** 上批仅 interp。当前纯 JIT-→JIT-→throw 的链路下 `e.StackTrace` 仍为 null。补齐让两个执行路径行为一致。

## 关键设计

| 钩子 | 位置 | 作用 |
|---|---|---|
| 入口 push | `JitModule::run_fn`（顶层入口）+ `jit_call/jit_call_indirect/jit_vcall` 助手（内部调用）| 把当前 callee 的 `(name, file)` push 到 `call_stack` |
| 出口 pop | 同上路径，`jit_fn` 返回后 | 与 push 1:1 配对 |
| caller line stamp | `jit_call/jit_call_indirect/jit_vcall` 助手收 `caller_line: u32` 新参 | 在调 callee 前 `update_top_frame_line` |
| throw populate | `jit_throw` 助手收 `throw_line: u32` 新参 | stamp 行 + 调 `populate_stack_trace` |

`FnEntry` 扩展承载 `name + file`（callee metadata 在 helper 内可达，无需逆向 lookup）。

## 阶段 1: FnEntry 扩展

- [x] 1.1 [src/runtime/src/jit/frame.rs](../../src/runtime/src/jit/frame.rs) `FnEntry` 加 `name: Arc<str>, file: Arc<str>`，去 `Copy`，加 `Clone`
- [x] 1.2 [src/runtime/src/jit/mod.rs](../../src/runtime/src/jit/mod.rs) `compile_module` 构建 FnEntry 时填 name + file（取自 `func.line_table.first().file`）
- [x] 1.3 调整 fn_entries / fn_entries_by_id 类型签名（HashMap<String, FnEntry> 仍 OK；Option<FnEntry> 自动支持 Clone）
- [x] 1.4 helper 端 `.copied()` → `.cloned()`（call.rs / vcall.rs / call_indirect 路径）

## 阶段 2: 入口 + 内部 call push/pop

- [x] 2.1 [src/runtime/src/jit/mod.rs](../../src/runtime/src/jit/mod.rs) `JitModule::run_fn` push call_stack（与现有 push_frame_state 配对，pop 同样在 jit_fn 返回后）
  - 取 entry 的 name + file
  - RAII guard 风格优先（panic-safe）
- [x] 2.2 [src/runtime/src/jit/helpers/call.rs](../../src/runtime/src/jit/helpers/call.rs) `jit_call` 在 invoke callee 前 push call_frame(callee.name, callee.file)，invoke 后 pop
- [x] 2.3 同样改 `jit_call_indirect`（如有独立 helper；否则在共享路径补）
- [x] 2.4 [src/runtime/src/jit/helpers/vcall.rs](../../src/runtime/src/jit/helpers/vcall.rs) `jit_vcall`：先 dispatch 解析得 callee，再 push 然后 invoke

## 阶段 3: caller line 透传

- [x] 3.1 `jit_call` / `jit_call_indirect` / `jit_vcall` 签名 + 一个新参 `caller_line: u32`，在 push callee frame 之前 `vm_ctx.update_top_frame_line(caller_line)`
- [x] 3.2 [src/runtime/src/jit/helpers/registry.rs](../../src/runtime/src/jit/helpers/registry.rs) 更新对应的 cranelift 签名（多一个 i32 参数）
- [x] 3.3 [src/runtime/src/jit/translate.rs](../../src/runtime/src/jit/translate.rs) 在 emit Call/CallIndirect/VCall 处用 `interp::resolve_line(...)` 算出 line，作为 i32 const 传入

## 阶段 4: throw line + populate

- [x] 4.1 [src/runtime/src/jit/helpers/control.rs](../../src/runtime/src/jit/helpers/control.rs) `jit_throw` 加 `throw_line: u32`，stamp top frame line + `populate_stack_trace(&val, vm_ctx, module)`
- [x] 4.2 [src/runtime/src/jit/helpers/registry.rs](../../src/runtime/src/jit/helpers/registry.rs) 更新签名
- [x] 4.3 [src/runtime/src/jit/translate.rs](../../src/runtime/src/jit/translate.rs) `Terminator::Throw` emit 处算 line 传入

## 阶段 5: 测试

- [x] 5.1 移除 [src/tests/exceptions/stack_trace_field.interp_only](../../src/tests/exceptions/stack_trace_field.interp_only) marker — 确认 JIT 也通过相同断言
- [x] 5.2 新加纯 JIT 链路 case（如有 `[ExecMode("Jit")]` 注解 + 多层调用 + throw + assert StackTrace 内容）—— 或直接用现有 stack_trace_field 在两 mode 下双跑覆盖

## 阶段 6: 验证

- [x] 6.1 `dotnet build src/compiler/z42.slnx` 全绿
- [x] 6.2 `cargo build --manifest-path src/runtime/Cargo.toml` + `cargo test --lib` 全绿
- [x] 6.3 `./scripts/test-vm.sh` interp + jit 全绿
- [x] 6.4 手动 smoke：编译三层调用 throw → 两 mode 都见 trace 三帧

## Scope

| 文件 | 类型 | 说明 |
|---|---|---|
| `src/runtime/src/jit/frame.rs` | MODIFY | FnEntry 扩展 name+file |
| `src/runtime/src/jit/mod.rs` | MODIFY | compile_module 填 metadata；run_fn push call_stack |
| `src/runtime/src/jit/helpers/call.rs` | MODIFY | jit_call 加 caller_line + push/pop call_frame |
| `src/runtime/src/jit/helpers/vcall.rs` | MODIFY | jit_vcall 同款 |
| `src/runtime/src/jit/helpers/control.rs` | MODIFY | jit_throw 加 throw_line + populate |
| `src/runtime/src/jit/helpers/registry.rs` | MODIFY | 4 个 helper 的 cranelift sig 更新 |
| `src/runtime/src/jit/translate.rs` | MODIFY | Call/CallIndirect/VCall/Throw 处传 line const |
| `src/tests/exceptions/stack_trace_field.interp_only` | DELETE | JIT 也通过 |

**只读引用：**
- `src/runtime/src/exception/mod.rs` — populate_stack_trace 已就位
- `src/runtime/src/vm_context.rs` — call_stack API 已就位

## 备注

- **CallIndirect**：可能与 jit_call 共享 helper；按实际情况调整阶段 3.1
- **`jit_install_catch`**：不需改 — 它消费 pending_exception 不知道 trace 来源
- **栈深度限制**：JIT 不会比 interp 深，无新风险
- **性能**：每个 JIT call 额外 1-2 个 Cell::set + Vec push/pop，预期 < 1% slowdown（已有 push_frame_state pattern 可参照成本）
