use crate::metadata::{NativeData, TypeDesc, Value};
use crate::vm_context::VmContext;
use anyhow::{bail, Result};
use std::collections::HashMap;
use std::sync::Arc;

// ── Object protocol ───────────────────────────────────────────────────────────

/// Returns a `Std.Type` object with `__name` and `__fullName` derived from
/// the runtime class of the argument.
pub fn builtin_obj_get_type(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let class_name = match args.first() {
        Some(Value::Object(rc)) => rc.borrow().type_desc.name.clone(),
        Some(Value::Null) => bail!("__obj_get_type: null reference"),
        _ => bail!("__obj_get_type: expected an object"),
    };
    let simple_name = class_name.split('.').next_back().unwrap_or(&class_name).to_string();

    // Build a minimal Type object with __name and __fullName slots.
    let mut field_index = HashMap::new();
    field_index.insert("__name".to_string(), 0usize);
    field_index.insert("__fullName".to_string(), 1usize);
    let type_desc = Arc::new(TypeDesc {
        name: crate::metadata::well_known_names::STD_TYPE.to_string(),
        base_name: None,
        fields: vec![
            crate::metadata::FieldSlot { name: "__name".to_string(), type_tag: "str".to_string() },
            crate::metadata::FieldSlot { name: "__fullName".to_string(), type_tag: "str".to_string() },
        ],
        field_index,
        vtable: Vec::new(),
        vtable_index: HashMap::new(), type_params: vec![], type_args: vec![],
        type_param_constraints: vec![],
    });
    Ok(ctx.heap().alloc_object(
        type_desc,
        vec![Value::Str(simple_name), Value::Str(class_name)],
        NativeData::None,
    ))
}

/// Reference equality: true iff both arguments point to the same heap allocation,
/// or both are null.
pub fn builtin_obj_ref_eq(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let result = match (args.first(), args.get(1)) {
        (Some(Value::Object(a)), Some(Value::Object(b))) => crate::gc::GcRef::ptr_eq(a, b),
        (Some(Value::Null), Some(Value::Null))           => true,
        (Some(Value::Null), _) | (_, Some(Value::Null)) => false,
        _                                                => false,
    };
    Ok(Value::Bool(result))
}

/// 2026-05-04 expose-weak-ref-builtin (D-1a)：把 GC 的 `make_weak` 暴露给
/// stdlib。Object/Array 弱化返回 WeakHandle（包装 NativeData::WeakRef 的
/// ScriptObject）；原子值（I64 / Str / Bool / Char / FuncRef / Closure 等）
/// 不可弱化，返回 Null。
pub fn builtin_obj_make_weak(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let target = match args.first() {
        Some(v) => v,
        None => return Ok(Value::Null),
    };
    let weak = match ctx.heap().make_weak(target) {
        Some(w) => w,
        None => return Ok(Value::Null),
    };
    let type_desc = weak_handle_type_desc();
    Ok(ctx.heap().alloc_object(type_desc, Vec::new(), NativeData::WeakRef(weak)))
}

/// 2026-05-04 D-1b: 提取 delegate 的 captured target（receiver 对象）。
/// 用于 `Std.WeakRef<TD>` 在 ctor 时拿到 target 后做 `MakeWeak` —— 让多播
/// 订阅器只持 listener 的弱引用，避免 lapsed listener 内存泄漏。
///
/// 语义（lenient，与 `__obj_make_weak` 同款）：
/// - `Closure { env: [first, ...] }` 当 `first is Object` → 返回该 Object
///   （instance method 组转换的 thunk 把 receiver 放在 env[0]，D-1b Phase 1）
/// - `Closure { env: [] }` / `env[0]` 非 Object（如基础类型 / Null）→ Null
/// - `StackClosure` → Null（stack 上不能 weak hold；用户场景退化 strong）
/// - `FuncRef` → Null（free function 无 receiver）
/// - 非 delegate / Null → Null
pub fn builtin_delegate_target(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Closure { env, .. }) => {
            let env_ref = env.borrow();
            match env_ref.first() {
                Some(Value::Object(o)) => Ok(Value::Object(o.clone())),
                Some(Value::Array(a)) => Ok(Value::Array(a.clone())),
                _ => Ok(Value::Null),
            }
        }
        _ => Ok(Value::Null),
    }
}

/// 2026-05-04 D-1b: 提取 Closure 的 fn_name（thunk 函数全限定名）。
/// 与 `__delegate_target` 配套使用 —— `Std.WeakRef<TD>` 不强持原 handler
/// （那会反向锁住 receiver），而是存 `(WeakHandle, fnName)`，Get 时用
/// `__make_closure(fnName, [upgradedTarget])` 重建一个新 Closure。
///
/// 语义：
/// - `Closure { fn_name }` → 返回 fn_name 字符串
/// - `StackClosure { fn_name }` → 返回 fn_name 字符串
/// - `FuncRef(name)` → 返回 name
/// - 其他 → Null
pub fn builtin_delegate_fn_name(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Closure { fn_name, .. }) => Ok(Value::Str(fn_name.clone())),
        Some(Value::StackClosure { fn_name, .. }) => Ok(Value::Str(fn_name.clone())),
        Some(Value::FuncRef(name)) => Ok(Value::Str(name.clone())),
        _ => Ok(Value::Null),
    }
}

/// 2026-05-04 D-1b: 用给定 fn_name + env 数组构造 heap-allocated Closure。
/// 与 `__delegate_target` / `__delegate_fn_name` 配套，让 `Std.WeakRef<TD>.Get()`
/// 在 receiver 仍存活时重建一个等价于原 handler 的 Closure。
///
/// 语义：
/// - args[0]: Value::Str (fn_name)
/// - args[1]: Value::Array (env Vec<Value>)
/// - 非 string / 非 array / Null → Null（lenient）
pub fn builtin_make_closure(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let fn_name = match args.first() {
        Some(Value::Str(s)) => s.clone(),
        _ => return Ok(Value::Null),
    };
    let env_arr = match args.get(1) {
        Some(Value::Array(a)) => a.clone(),
        _ => return Ok(Value::Null),
    };
    // 把 Array 里的元素拷贝到 Vec<Value>，再走 heap.alloc_array 拿到新的 GcRef。
    // 与 jit_mk_clos 同款路径：env 是 GC 管理的 Array → Closure 持其 GcRef。
    let env_vec: Vec<Value> = env_arr.borrow().iter().cloned().collect();
    let env_val = ctx.heap().alloc_array(env_vec);
    let env = match env_val {
        Value::Array(rc) => rc,
        _ => return Ok(Value::Null),  // unreachable but lenient
    };
    Ok(Value::Closure { env, fn_name })
}

/// 2026-05-04 expose-weak-ref-builtin (D-1a)：升格 WeakHandle 弱引用。
/// 目标存活返回原 Object/Array；目标已被回收 / 输入非 WeakHandle / Null →
/// 返回 Null（lenient 处理，与 `__delegate_eq` 同款）。
pub fn builtin_obj_upgrade_weak(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let handle = match args.first() {
        Some(Value::Object(o)) => o,
        _ => return Ok(Value::Null),
    };
    let weak = {
        let obj = handle.borrow();
        match &obj.native {
            NativeData::WeakRef(w) => w.clone(),
            _ => return Ok(Value::Null),
        }
    };
    Ok(ctx.heap().upgrade_weak(&weak).unwrap_or(Value::Null))
}

/// WeakHandle 的 TypeDesc 单例（slots 为空；仅 NativeData::WeakRef 携带状态）。
fn weak_handle_type_desc() -> Arc<TypeDesc> {
    use std::sync::OnceLock;
    static CACHE: OnceLock<Arc<TypeDesc>> = OnceLock::new();
    CACHE.get_or_init(|| Arc::new(TypeDesc {
        name: "Std.WeakHandle".to_string(),
        base_name: None,
        fields: Vec::new(),
        field_index: HashMap::new(),
        vtable: Vec::new(),
        vtable_index: HashMap::new(),
        type_params: vec![],
        type_args: vec![],
        type_param_constraints: vec![],
    })).clone()
}

/// 2026-05-03 fix-delegate-reference-equality (D-5)：delegate reference
/// equality —— 三个 `Value` 变体（FuncRef / Closure / StackClosure）按
/// 各自身份语义比较。跨变体不等，非 delegate 值返回 false 不报错。
///
/// 语义参见 `delegates-events.md` 与本 spec design.md：
/// - `FuncRef(name)` —— fn name 字符串相等
/// - `Closure { env, fn_name }` —— fn_name 相等且 env GcRef::ptr_eq
/// - `StackClosure { env_idx, fn_name }` —— fn_name 相等且 env_idx 相等
pub fn builtin_delegate_eq(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let result = match (args.first(), args.get(1)) {
        (Some(Value::FuncRef(a)), Some(Value::FuncRef(b))) => a == b,
        (
            Some(Value::Closure { env: ea, fn_name: na }),
            Some(Value::Closure { env: eb, fn_name: nb }),
        ) => na == nb && crate::gc::GcRef::ptr_eq(ea, eb),
        (
            Some(Value::StackClosure { env_idx: ia, fn_name: na }),
            Some(Value::StackClosure { env_idx: ib, fn_name: nb }),
        ) => na == nb && ia == ib,
        (Some(Value::Null), Some(Value::Null))           => true,
        (Some(Value::Null), _) | (_, Some(Value::Null)) => false,
        _                                                => false,
    };
    Ok(Value::Bool(result))
}

/// Identity-based hash code derived from the Rc pointer address.
pub fn builtin_obj_hash_code(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Object(gc)) => {
            let addr = crate::gc::GcRef::as_ptr(gc) as *const _ as i64;
            Ok(Value::I64((addr & 0x7fff_ffff) as i64))
        }
        Some(Value::Null) => Ok(Value::I64(0)),
        _ => bail!("__obj_hash_code: expected an object"),
    }
}

/// Value equality — defaults to reference equality.
/// args: [this, other]
pub fn builtin_obj_equals(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let result = match (args.first(), args.get(1)) {
        (Some(Value::Object(a)), Some(Value::Object(b))) => crate::gc::GcRef::ptr_eq(a, b),
        (Some(Value::Null), Some(Value::Null))           => true,
        (Some(Value::Null), _) | (_, Some(Value::Null)) => false,
        _                                                => false,
    };
    Ok(Value::Bool(result))
}

/// Human-readable representation — returns the unqualified type name.
/// args: [this]
pub fn builtin_obj_to_str(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    match args.first() {
        Some(Value::Object(rc)) => {
            let class_name = rc.borrow().type_desc.name.clone();
            let simple = class_name.split('.').next_back().unwrap_or(&class_name).to_string();
            Ok(Value::Str(simple))
        }
        Some(Value::Null) => Ok(Value::Str("null".into())),
        _ => bail!("__obj_to_str: expected an object"),
    }
}

// 2026-04-27 wave1-assert-script: 6 `builtin_assert_*` functions removed.
// `Std.Assert` is now pure z42 script in `z42.core/src/Assert.z42`.
