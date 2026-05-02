/// Interpreter backend — tree-walking bytecode execution.
///
/// Implementation is split across submodules:
/// • mod.rs      — public API, Frame, core execution loop
/// • exec_instr.rs — instruction dispatch (one big match)
/// • dispatch.rs — object dispatch helpers (vtable, ToString, static fields)
/// • ops.rs      — register-level helpers (int_binop, numeric_lt, collect_args, …)

mod dispatch;
pub(crate) mod exec_instr;
mod ops;

pub use crate::corelib::convert::value_to_str;
use crate::metadata::{Function, Module, Terminator, Value};
use crate::vm_context::VmContext;
use anyhow::{bail, Context, Result};
use std::collections::HashMap;

// ── Execution outcome ────────────────────────────────────────────────────────

/// Outcome of executing a function.
/// User exceptions are value-based (no heap allocation), not anyhow errors.
#[derive(Debug)]
pub(crate) enum ExecOutcome {
    /// Normal return (with optional return value).
    Returned(Option<Value>),
    /// User exception thrown and not caught within this function.
    Thrown(Value),
}

// ── Public entry points ──────────────────────────────────────────────────────

/// Entry point: run a function with the given arguments.
pub fn run(ctx: &VmContext, module: &Module, func: &Function, args: &[Value]) -> Result<()> {
    match exec_function(ctx, module, func, args)? {
        ExecOutcome::Returned(_) => Ok(()),
        ExecOutcome::Thrown(val) => bail!("uncaught exception: {}", value_to_str(&val)),
    }
}

/// Variant of [`run`] that returns the function's return value (if any)
/// instead of discarding it. Used by integration tests and by embedders
/// that need the result of a script entry point. Mirrors `run` in every
/// other respect (errors, exception conversion).
pub fn run_returning(
    ctx: &VmContext,
    module: &Module,
    func: &Function,
    args: &[Value],
) -> Result<Option<Value>> {
    match exec_function(ctx, module, func, args)? {
        ExecOutcome::Returned(v) => Ok(v),
        ExecOutcome::Thrown(val) => bail!("uncaught exception: {}", value_to_str(&val)),
    }
}

/// Run with static init: clears static fields, runs ALL `*.__static_init__`
/// functions (both eager-loaded in `module.functions` and lazy-loadable from
/// declared zpkgs), then runs `func`.
///
/// 2026-04-27 fix-static-field-access: 修前只跑 `{module.name}.__static_init__`
/// (主模块)，导入的 zpkg（如 z42.math 的 `Std.Math.__static_init__`）虽然 link 进
/// merged module 但永不被调用 → `Math.PI` 等常量永远 `null`。
///
/// interp 模式下 stdlib 是 lazy-loaded，启动时除 z42.core 外都不在
/// `module.functions`。所以同时需要：
///   1. 扫主模块 functions（拿到 eagerly-loaded 的 init，含 main 自己 + z42.core）
///   2. 通过 `lazy_loader::declared_namespaces()` 拿到所有声明但未加载的命名空间，
///      调用 `try_lookup_function("<ns>.__static_init__")` 触发 lazy load
///   3. 合并 + 按 FQN 字母序去重 + 逐一调用
///
/// 副作用：所有声明的 stdlib zpkg 都会被 eagerly 加载（不再纯 lazy）。
/// 在当前 stdlib 规模（~50KB / 5 包）下成本可忽略，换取静态常量可用。
pub fn run_with_static_init(ctx: &VmContext, module: &Module, func: &Function) -> Result<()> {
    ctx.static_fields_clear();

    // 1. Eager-loaded init functions (in main + z42.core).
    let mut eager_inits: Vec<&Function> = module.functions.iter()
        .filter(|f| f.name.ends_with(".__static_init__"))
        .collect();
    eager_inits.sort_by(|a, b| a.name.cmp(&b.name));
    for init_fn in &eager_inits {
        match exec_function(ctx, module, init_fn, &[])? {
            ExecOutcome::Returned(_) => {}
            ExecOutcome::Thrown(val) =>
                bail!("uncaught exception in static init `{}`: {}", init_fn.name, value_to_str(&val)),
        }
    }

    // 2. Lazy-loadable init functions (from declared but not-yet-loaded zpkgs).
    let mut lazy_init_names: Vec<String> = ctx.declared_namespaces()
        .into_iter()
        .map(|ns| format!("{ns}.__static_init__"))
        .collect();
    lazy_init_names.sort();
    for init_name in lazy_init_names {
        let Some(init_fn) = ctx.try_lookup_function(&init_name) else { continue };
        match exec_function(ctx, module, init_fn.as_ref(), &[])? {
            ExecOutcome::Returned(_) => {}
            ExecOutcome::Thrown(val) =>
                bail!("uncaught exception in static init `{}`: {}", init_name, value_to_str(&val)),
        }
    }

    match exec_function(ctx, module, func, &[])? {
        ExecOutcome::Returned(_) => Ok(()),
        ExecOutcome::Thrown(val) => bail!("uncaught exception: {}", value_to_str(&val)),
    }
}

// ── Frame ────────────────────────────────────────────────────────────────────

pub(crate) struct Frame {
    pub regs: Vec<Value>,
    /// 2026-05-02 impl-closure-l3-escape-stack: frame-local arena 持有不逃逸
    /// closure 的 env。`Value::StackClosure { env_idx }` 索引这里。frame drop
    /// 时整个 arena 一并释放（内嵌的 Value 走 normal Drop / GcRef 减引用计数）。
    pub env_arena: Vec<Vec<Value>>,
}

impl Frame {
    pub fn new(args: &[Value], max_reg: u32) -> Self {
        let size = if max_reg > 0 { max_reg as usize } else { args.len() };
        let mut regs = vec![Value::Null; size.max(args.len())];
        for (i, v) in args.iter().enumerate() {
            regs[i] = v.clone();
        }
        Frame { regs, env_arena: Vec::new() }
    }

    pub fn set(&mut self, reg: u32, val: Value) {
        let idx = reg as usize;
        if idx >= self.regs.len() {
            self.regs.resize(idx + 1, Value::Null);
        }
        self.regs[idx] = val;
    }

    #[inline(always)]
    pub fn get(&self, reg: u32) -> Result<&Value> {
        let idx = reg as usize;
        if idx < self.regs.len() {
            Ok(&self.regs[idx])
        } else {
            anyhow::bail!("undefined register %{reg}")
        }
    }
}

// ── Debug: source line resolution ─────────────────────────────────────────────

fn resolve_line(table: &[crate::metadata::bytecode::LineEntry], block: u32, instr: u32) -> u32 {
    let mut line = 0u32;
    for entry in table {
        if entry.block > block || (entry.block == block && entry.instr > instr) { break; }
        line = entry.line;
    }
    line
}

// ── Core execution loop ──────────────────────────────────────────────────────

/// Phase 3f: RAII guard 保证 push_frame_regs / pop_frame_regs 严格配对，
/// 即使 exec_function 通过 `?` early return 或 panic 展开。Drop 时自动 pop。
struct FrameGuard<'a> {
    ctx: &'a VmContext,
}
impl Drop for FrameGuard<'_> {
    fn drop(&mut self) {
        self.ctx.pop_frame_regs();
    }
}

pub(crate) fn exec_function(ctx: &VmContext, module: &Module, func: &Function, args: &[Value]) -> Result<ExecOutcome> {
    let mut frame = Frame::new(args, func.max_reg);
    // Phase 3f + impl-closure-l3-escape-stack: 把当前 frame.regs +
    // frame.env_arena 指针都注册到 GC root。env_arena 在 stack closure 创建
    // 路径才会有非空内容，但即便空也要注册以保持 push/pop 严格 1:1。
    // SAFETY: regs/env_arena Vec 在 frame 内（栈上），地址稳定到本函数返回；
    // FrameGuard 在 Drop 时 pop（含 ?-propagated error 与 panic）。
    ctx.push_frame_state(
        &frame.regs as *const Vec<Value>,
        &frame.env_arena as *const Vec<Vec<Value>>,
    );
    let _frame_guard = FrameGuard { ctx };

    // Spec C2: scope `CURRENT_VM` to this z42 frame so `z42_*` extern
    // entry points fired by native callbacks can locate the active VM.
    // The guard nests safely if a native callback re-enters z42 through
    // `exec_function`; on exit the previous pointer is restored.
    let _vm_guard = crate::native::exports::VmGuard::enter(ctx);

    let block_map = &func.block_index;
    let mut block_idx = 0usize;

    'exec: loop {
        let block = func
            .blocks
            .get(block_idx)
            .with_context(|| format!("block index {block_idx} out of range"))?;

        for (instr_idx, instr) in block.instructions.iter().enumerate() {
            // exec_instr returns:
            //   Ok(None)       — normal instruction completion
            //   Ok(Some(val))  — a callee threw an exception (value-based propagation)
            //   Err(e)         — internal VM error
            match exec_instr::exec_instr(ctx, module, &mut frame, instr) {
                Ok(None) => {}
                Ok(Some(thrown_val)) => {
                    // User exception from a callee — try to find a local handler
                    if let Some(entry_idx) = find_handler(func, block_idx, block_map) {
                        let entry = &func.exception_table[entry_idx];
                        frame.set(entry.catch_reg, thrown_val);
                        block_idx = *block_map.get(entry.catch_label.as_str())
                            .with_context(|| format!("undefined block `{}`", entry.catch_label))?;
                        continue 'exec;
                    }
                    // No handler — propagate up as ExecOutcome::Thrown (no anyhow allocation)
                    return Ok(ExecOutcome::Thrown(thrown_val));
                }
                Err(e) => {
                    // Internal error — enrich with source location.
                    // (consolidate-vm-state, 2026-04-28: removed legacy
                    // UserException sentinel branch — JIT helpers now report
                    // exceptions via `ctx.set_exception` + extern-C return code,
                    // not via `anyhow::Error` wrapping.)
                    let loc = resolve_line(&func.line_table, block_idx as u32, instr_idx as u32);
                    return Err(e.context(format!("  at {} (line {})", func.name, loc)));
                }
            }
        }

        match &block.terminator {
            Terminator::Ret { reg: None }      => return Ok(ExecOutcome::Returned(None)),
            Terminator::Ret { reg: Some(r) }   => return Ok(ExecOutcome::Returned(Some(frame.get(*r)?.clone()))),
            Terminator::Br  { label }          => {
                block_idx = *block_map.get(label.as_str())
                    .with_context(|| format!("undefined block `{label}`"))?;
            }
            Terminator::BrCond { cond, true_label, false_label } => {
                let go_true = match frame.get(*cond)? {
                    Value::Bool(b) => *b,
                    other => bail!("BrCond expects bool, got {:?}", other),
                };
                let label = if go_true { true_label } else { false_label };
                block_idx = *block_map.get(label.as_str())
                    .with_context(|| format!("undefined block `{label}`"))?;
            }
            Terminator::Throw { reg } => {
                let val = frame.get(*reg)?.clone();
                if let Some(entry_idx) = find_handler(func, block_idx, block_map) {
                    let entry = &func.exception_table[entry_idx];
                    frame.set(entry.catch_reg, val);
                    block_idx = *block_map.get(entry.catch_label.as_str())
                        .with_context(|| format!("undefined block `{}`", entry.catch_label))?;
                } else {
                    // No local handler — propagate via value, not anyhow
                    return Ok(ExecOutcome::Thrown(val));
                }
            }
        }
    }
}

/// Find the index into `func.exception_table` that covers the given block index.
fn find_handler(func: &Function, block_idx: usize, block_map: &HashMap<String, usize>) -> Option<usize> {
    for (i, entry) in func.exception_table.iter().enumerate() {
        let start_idx = *block_map.get(&entry.try_start)?;
        let end_idx   = *block_map.get(&entry.try_end)?;
        if block_idx >= start_idx && block_idx < end_idx {
            return Some(i);
        }
    }
    None
}
