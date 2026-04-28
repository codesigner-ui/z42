use super::*;

// ── static_fields ─────────────────────────────────────────────────────────────

#[test]
fn static_get_unset_returns_null() {
    let ctx = VmContext::new();
    assert!(matches!(ctx.static_get("Foo.Bar"), Value::Null));
}

#[test]
fn static_set_then_get_roundtrip() {
    let ctx = VmContext::new();
    ctx.static_set("Foo.Bar", Value::I64(42));
    assert!(matches!(ctx.static_get("Foo.Bar"), Value::I64(42)));
}

#[test]
fn static_fields_clear_drops_all() {
    let ctx = VmContext::new();
    ctx.static_set("A", Value::I64(1));
    ctx.static_set("B", Value::I64(2));
    ctx.static_fields_clear();
    assert!(matches!(ctx.static_get("A"), Value::Null));
    assert!(matches!(ctx.static_get("B"), Value::Null));
}

#[test]
fn two_contexts_static_fields_isolated() {
    let ctx1 = VmContext::new();
    let ctx2 = VmContext::new();
    ctx1.static_set("Foo", Value::I64(1));
    ctx2.static_set("Foo", Value::I64(2));
    assert!(matches!(ctx1.static_get("Foo"), Value::I64(1)));
    assert!(matches!(ctx2.static_get("Foo"), Value::I64(2)));
}

// ── pending_exception ─────────────────────────────────────────────────────────

#[test]
fn pending_exception_take_unset_is_none() {
    let ctx = VmContext::new();
    assert!(ctx.take_exception().is_none());
}

#[test]
fn pending_exception_set_then_take_consumes() {
    let ctx = VmContext::new();
    ctx.set_exception(Value::Str("oops".into()));
    let v = ctx.take_exception();
    assert!(matches!(v, Some(Value::Str(ref s)) if s == "oops"));
    // Subsequent take returns None (consumed).
    assert!(ctx.take_exception().is_none());
}

#[test]
fn two_contexts_exception_isolated() {
    let ctx1 = VmContext::new();
    let ctx2 = VmContext::new();
    ctx1.set_exception(Value::Str("ctx1".into()));
    // ctx2 stays empty.
    assert!(ctx2.take_exception().is_none());
    let v = ctx1.take_exception();
    assert!(matches!(v, Some(Value::Str(ref s)) if s == "ctx1"));
}

// ── lazy_loader install / uninstall ───────────────────────────────────────────

#[test]
fn lazy_loader_install_then_uninstall_is_clean() {
    let ctx = VmContext::new();
    ctx.install_lazy_loader(None, 0);
    assert!(ctx.try_lookup_function("Std.IO.Console.WriteLine").is_none());
    ctx.uninstall_lazy_loader();
    assert!(ctx.try_lookup_function("Anything.Foo").is_none());
}

#[test]
fn lazy_loader_two_contexts_independent() {
    let ctx1 = VmContext::new();
    let ctx2 = VmContext::new();
    ctx1.install_lazy_loader(None, 0);
    // ctx2 has no loader installed.
    assert_eq!(ctx1.declared_namespaces(), Vec::<String>::new());
    assert_eq!(ctx2.declared_namespaces(), Vec::<String>::new());
}
