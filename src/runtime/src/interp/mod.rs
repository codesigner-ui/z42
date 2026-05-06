/// Interpreter backend — tree-walking bytecode execution.
///
/// Implementation is split across submodules:
/// • mod.rs      — public API, Frame, core execution loop
/// • exec_instr.rs — instruction dispatch (one big match)
/// • dispatch.rs — object dispatch helpers (vtable, ToString, static fields)
/// • ops.rs      — register-level helpers (int_binop, numeric_lt, collect_args, …)

pub(crate) mod dispatch;
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
    /// Spec impl-ref-out-in-runtime (Decision R2 architecture E):
    /// `(param_reg, original_ref_kind)` pairs. When the function was called
    /// with a `ref`/`out`/`in` argument, the entry path deref'd the Ref
    /// (storing the underlying value into `regs[param_reg]` so all
    /// instruction handlers see a normal value) and stashed the original
    /// `RefKind` here. At function exit, every entry's final `regs[param_reg]`
    /// value is stored back through the corresponding Ref to the caller's
    /// lvalue. Net semantic: caller sees the post-call value of its
    /// `ref`/`out` lvalue, identical to true cross-frame refs but without
    /// requiring 80+ instruction handlers to be deref-aware.
    pub ref_writebacks: Vec<(u32, crate::metadata::types::RefKind)>,
}

impl Frame {
    pub fn new(args: &[Value], max_reg: u32) -> Self {
        let size = if max_reg > 0 { max_reg as usize } else { args.len() };
        let mut regs = vec![Value::Null; size.max(args.len())];
        for (i, v) in args.iter().enumerate() {
            regs[i] = v.clone();
        }
        Frame {
            regs,
            env_arena: Vec::new(),
            ref_writebacks: Vec::new(),
        }
    }

    /// Set a register's raw value (no deref). For ref-aware store-through
    /// (transparently writing through `Value::Ref` to the underlying
    /// caller slot / array elem / object field), use `set_thru_ref`
    /// (spec impl-ref-out-in-runtime).
    pub fn set(&mut self, reg: u32, val: Value) {
        let idx = reg as usize;
        if idx >= self.regs.len() {
            self.regs.resize(idx + 1, Value::Null);
        }
        self.regs[idx] = val;
    }

    /// Get a register's raw value (no deref). For ref-aware read-through
    /// (transparently dereferencing `Value::Ref`), use `get_deref`
    /// (spec impl-ref-out-in-runtime).
    #[inline(always)]
    pub fn get(&self, reg: u32) -> Result<&Value> {
        let idx = reg as usize;
        if idx < self.regs.len() {
            Ok(&self.regs[idx])
        } else {
            anyhow::bail!("undefined register %{reg}")
        }
    }

    /// Spec impl-ref-out-in-runtime (Decision R2): read a register's value
    /// with transparent deref. If the register holds a `Value::Ref`, the
    /// underlying value (from caller frame / array elem / object field) is
    /// returned. Otherwise behaves like `get` but returns owned `Value`.
    /// Use this in instruction handlers that read user-visible values.
    #[allow(dead_code)]
    pub fn get_deref(&self, reg: u32, ctx: &VmContext) -> Result<Value> {
        match self.get(reg)? {
            Value::Ref { kind } => deref_ref(kind, ctx),
            other => Ok(other.clone()),
        }
    }

    /// Spec impl-ref-out-in-runtime (Decision R2): write a register with
    /// transparent store-through. If the register currently holds a
    /// `Value::Ref`, the write is forwarded to the underlying location and
    /// the Ref itself is preserved so subsequent reads/writes still
    /// indirect. Otherwise the register is overwritten.
    #[allow(dead_code)]
    pub fn set_thru_ref(&mut self, reg: u32, val: Value, ctx: &VmContext) -> Result<()> {
        let idx = reg as usize;
        if idx >= self.regs.len() {
            self.regs.resize(idx + 1, Value::Null);
        }
        let kind_to_store = match &self.regs[idx] {
            Value::Ref { kind } => Some(kind.clone()),
            _ => None,
        };
        match kind_to_store {
            Some(kind) => store_thru_ref(&kind, val, ctx),
            None => { self.regs[idx] = val; Ok(()) }
        }
    }
}

/// Spec impl-ref-out-in-runtime: dereference a `Value::Ref { kind }` into
/// the underlying value. Stack kind looks up `ctx.frame_state_at(frame_idx)`
/// (raw pointer, safe under design Decision 9: refs don't escape call
/// stack). Array/Field kinds borrow the held GcRef and read.
pub(crate) fn deref_ref(
    kind: &crate::metadata::types::RefKind, ctx: &VmContext,
) -> Result<Value> {
    use crate::metadata::types::RefKind;
    match kind {
        RefKind::Stack { frame_idx, slot } => {
            let regs_ptr = ctx.frame_state_at(*frame_idx as usize)
                .ok_or_else(|| anyhow::anyhow!(
                    "ref points to popped frame (frame_idx={frame_idx})"))?;
            // SAFETY: spec Decision 9 — refs never escape the call stack;
            // when this deref runs, the target frame is still active.
            let regs = unsafe { &*regs_ptr };
            let v = regs.get(*slot as usize)
                .ok_or_else(|| anyhow::anyhow!(
                    "ref target slot %{slot} out of frame range"))?;
            // Sanity guard: ref-to-ref nesting not supported (codegen never
            // produces it; defend in case of malformed bytecode).
            if let Value::Ref { .. } = v {
                anyhow::bail!("ref-to-ref nesting not supported");
            }
            Ok(v.clone())
        }
        RefKind::Array { gc_ref, idx } => {
            let arr = gc_ref.borrow();
            arr.get(*idx)
                .cloned()
                .ok_or_else(|| anyhow::anyhow!(
                    "ref array index {idx} out of bounds (len={})", arr.len()))
        }
        RefKind::Field { gc_ref, field_name } => {
            let obj = gc_ref.borrow();
            let slot = *obj.type_desc.field_index.get(field_name)
                .ok_or_else(|| anyhow::anyhow!(
                    "ref field `{field_name}` not found on type `{}`",
                    obj.type_desc.name))?;
            Ok(obj.slots.get(slot).cloned().unwrap_or(Value::Null))
        }
    }
}

/// Spec impl-ref-out-in-runtime: store a value through a `Value::Ref` to
/// the underlying location. Mirror of `deref_ref` for the write path.
pub(crate) fn store_thru_ref(
    kind: &crate::metadata::types::RefKind, val: Value, ctx: &VmContext,
) -> Result<()> {
    use crate::metadata::types::RefKind;
    match kind {
        RefKind::Stack { frame_idx, slot } => {
            let regs_ptr = ctx.frame_state_at(*frame_idx as usize)
                .ok_or_else(|| anyhow::anyhow!(
                    "ref points to popped frame (frame_idx={frame_idx})"))?;
            // SAFETY: same as deref_ref; the frame is still active.
            // We need *mut here — cast from *const Vec<Value>. The frame's
            // regs Vec is borrowed from `Frame` which is `&mut` during exec
            // of its instructions, so the Vec is uniquely owned by that
            // frame. Cross-frame mutation requires us to coerce to mut.
            let regs = unsafe { &mut *(regs_ptr as *mut Vec<Value>) };
            let slot_idx = *slot as usize;
            if slot_idx >= regs.len() {
                regs.resize(slot_idx + 1, Value::Null);
            }
            regs[slot_idx] = val;
            Ok(())
        }
        RefKind::Array { gc_ref, idx } => {
            let mut arr = gc_ref.borrow_mut();
            if *idx >= arr.len() {
                anyhow::bail!(
                    "ref array index {idx} out of bounds (len={})", arr.len());
            }
            arr[*idx] = val;
            Ok(())
        }
        RefKind::Field { gc_ref, field_name } => {
            let mut obj = gc_ref.borrow_mut();
            let slot_opt = obj.type_desc.field_index.get(field_name).copied();
            match slot_opt {
                Some(slot) if slot < obj.slots.len() => {
                    obj.slots[slot] = val;
                    Ok(())
                }
                _ => anyhow::bail!(
                    "ref field `{field_name}` not found on type `{}`",
                    obj.type_desc.name),
            }
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

    // Spec impl-ref-out-in-runtime (Decision R2 architecture E):
    // 入口 copy-in：扫描 params，对每个持 Value::Ref 的 reg：
    //   1. 通过 RefKind 解引用得到底层值
    //   2. 将 reg[i] 替换为底层值（callee 体内所有指令读到的是普通 Value）
    //   3. 把原 RefKind 存入 frame.ref_writebacks，留给出口 copy-out 用
    // 这样 callee 的 80+ 指令 handler 完全不需要感知 Ref —— 透明性由
    // 入口/出口完成，仅一次 Vec 分配代价。
    // 注意：deref 必须在 push_frame_state 之前完成，因为此时 callee 自己的
    // frame 尚未入栈，Ref::Stack { frame_idx } 指向 caller 的栈位置，
    // ctx.frame_state_at 能找到正确 regs 指针。
    for i in 0..(func.param_count as usize).min(frame.regs.len()) {
        if let Value::Ref { kind } = &frame.regs[i] {
            let kind_clone = kind.clone();
            let underlying = deref_ref(&kind_clone, ctx)?;
            frame.regs[i] = underlying;
            frame.ref_writebacks.push((i as u32, kind_clone));
        }
    }

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
                    if let Some(entry_idx) = find_handler(
                        func, block_idx, block_map, &module.type_registry, &thrown_val,
                    ) {
                        let entry = &func.exception_table[entry_idx];
                        frame.set(entry.catch_reg, thrown_val);
                        block_idx = *block_map.get(entry.catch_label.as_str())
                            .with_context(|| format!("undefined block `{}`", entry.catch_label))?;
                        continue 'exec;
                    }
                    // No handler — propagate up as ExecOutcome::Thrown (no anyhow allocation)
                    // Spec impl-ref-out-in-runtime: writebacks still run on
                    // throw paths so any modifications callee made to ref/out
                    // params before the throw are visible to caller (matches
                    // C# DA model: caller in catch block sees mutations).
                    run_ref_writebacks(&frame, ctx)?;
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
            Terminator::Ret { reg: None }      => {
                run_ref_writebacks(&frame, ctx)?;
                return Ok(ExecOutcome::Returned(None));
            }
            Terminator::Ret { reg: Some(r) }   => {
                let ret_val = frame.get(*r)?.clone();
                run_ref_writebacks(&frame, ctx)?;
                return Ok(ExecOutcome::Returned(Some(ret_val)));
            }
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
                if let Some(entry_idx) = find_handler(
                    func, block_idx, block_map, &module.type_registry, &val,
                ) {
                    let entry = &func.exception_table[entry_idx];
                    frame.set(entry.catch_reg, val);
                    block_idx = *block_map.get(entry.catch_label.as_str())
                        .with_context(|| format!("undefined block `{}`", entry.catch_label))?;
                } else {
                    // No local handler — propagate via value, not anyhow
                    run_ref_writebacks(&frame, ctx)?;
                    return Ok(ExecOutcome::Thrown(val));
                }
            }
        }
    }
}

/// Spec impl-ref-out-in-runtime: copy-out for `ref`/`out` params. Iterate
/// `frame.ref_writebacks`; for each `(reg, original_ref_kind)`, take the
/// callee's final value of that reg and store it through the original Ref
/// to the caller's lvalue. Runs before every function-exit return path
/// (normal return + uncaught throw).
fn run_ref_writebacks(frame: &Frame, ctx: &VmContext) -> Result<()> {
    for (reg, kind) in &frame.ref_writebacks {
        let final_val = frame.regs.get(*reg as usize)
            .cloned()
            .unwrap_or(Value::Null);
        store_thru_ref(kind, final_val, ctx)?;
    }
    Ok(())
}

/// Find the index into `func.exception_table` of the first handler whose try
/// region covers `block_idx` AND whose declared `catch_type` matches the thrown
/// value's class (with subclass walk via the type registry).
///
/// catch-by-generic-type (2026-05-06): catch_type semantics —
///   None       — wildcard (user wrote `catch { }` / `catch (e)`); always matches.
///   Some("*")  — synthetic finally fallthrough catchall (compiler-generated
///                when there is no user catch but a finally block exists).
///   Some(t)    — typed catch; matches when the thrown value is an instance of
///                class `t` or any of its subclasses (sibling lineages skipped).
///
/// Source-order is preserved: exception_table entries are written in catch-clause
/// order by FunctionEmitterStmts; this loop scans them in that order and returns
/// the first match — matching C# / Java first-source-match-wins semantics.
///
/// `thrown` is expected to be a `Value::Object` (z42 throw is restricted to
/// Exception-derived class instances); non-object throws fall through to the
/// untyped catches via the wildcard branches above.
fn find_handler(
    func: &Function,
    block_idx: usize,
    block_map: &HashMap<String, usize>,
    type_registry: &HashMap<String, std::sync::Arc<crate::metadata::TypeDesc>>,
    thrown: &Value,
) -> Option<usize> {
    let thrown_class: Option<String> = match thrown {
        Value::Object(rc) => Some(rc.borrow().type_desc.name.clone()),
        _                 => None,
    };

    for (i, entry) in func.exception_table.iter().enumerate() {
        let start_idx = *block_map.get(&entry.try_start)?;
        let end_idx   = *block_map.get(&entry.try_end)?;
        if !(block_idx >= start_idx && block_idx < end_idx) { continue; }

        match entry.catch_type.as_deref() {
            None      => return Some(i),                   // user untyped catch
            Some("*") => return Some(i),                   // synthetic finally fallthrough
            Some(target) => {
                if let Some(ref derived) = thrown_class {
                    if dispatch::is_subclass_or_eq_td(type_registry, derived, target) {
                        return Some(i);
                    }
                }
                // type mismatch — try next entry
            }
        }
    }
    None
}
