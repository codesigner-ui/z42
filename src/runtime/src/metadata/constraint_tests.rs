//! L3-G3a verify_constraints tests.
//! Covers: loader populates TypeDesc/Function constraints; verify pass flags bad refs;
//! `Std.*` refs are passed through for lazy loader to resolve.

use super::bytecode::{ClassDesc, ConstraintBundle, Function, Module};
use super::loader::{build_type_registry, verify_constraints};
use super::types::ExecMode;

fn empty_module(name: &str) -> Module {
    Module {
        name: name.to_owned(),
        string_pool: vec![],
        classes: vec![],
        functions: vec![],
        type_registry: std::collections::HashMap::new(),
        func_index: std::collections::HashMap::new(),
    }
}

fn simple_class(name: &str) -> ClassDesc {
    ClassDesc {
        name: name.to_owned(),
        base_class: None,
        fields: vec![],
        type_params: vec![],
        type_param_constraints: vec![],
    }
}

fn generic_fn(name: &str, tp: &str, bundle: ConstraintBundle) -> Function {
    Function {
        name: name.to_owned(),
        param_count: 0,
        ret_type: "void".into(),
        exec_mode: ExecMode::Interp,
        blocks: vec![],
        exception_table: vec![],
        is_static: false,
        max_reg: 0,
        line_table: vec![],
        local_vars: vec![],
        type_params: vec![tp.into()],
        type_param_constraints: vec![bundle],
        block_index: std::collections::HashMap::new(),
    }
}

#[test]
fn loader_populates_typedesc_constraints() {
    let mut cls = simple_class("Box");
    cls.type_params = vec!["T".into()];
    cls.type_param_constraints = vec![ConstraintBundle {
        base_class: Some("Animal".into()),
        ..Default::default()
    }];
    let mut m = empty_module("demo");
    m.classes = vec![cls, simple_class("Animal")];
    build_type_registry(&mut m);

    let td = m.type_registry.get("Box").expect("Box in registry");
    assert_eq!(td.type_param_constraints.len(), 1);
    assert_eq!(td.type_param_constraints[0].base_class.as_deref(), Some("Animal"));
}

#[test]
fn verify_accepts_known_class_constraint() {
    let mut m = empty_module("demo");
    m.classes = vec![simple_class("Animal"), {
        let mut c = simple_class("Box");
        c.type_params = vec!["T".into()];
        c.type_param_constraints = vec![ConstraintBundle {
            base_class: Some("Animal".into()),
            ..Default::default()
        }];
        c
    }];
    build_type_registry(&mut m);
    verify_constraints(&m).expect("Animal resolves in registry");
}

#[test]
fn verify_rejects_unknown_base_class() {
    let mut m = empty_module("demo");
    let mut c = simple_class("Box");
    c.type_params = vec!["T".into()];
    c.type_param_constraints = vec![ConstraintBundle {
        base_class: Some("Ghost".into()),
        ..Default::default()
    }];
    m.classes = vec![c];
    build_type_registry(&mut m);
    let err = verify_constraints(&m).expect_err("Ghost is undefined");
    assert!(err.to_string().contains("InvalidConstraintReference"),
        "unexpected error: {err}");
    assert!(err.to_string().contains("Ghost"));
}

#[test]
fn verify_allows_std_namespace_reference() {
    // Std.* is resolved lazily by the zpkg loader; verify pass lets it through.
    let mut m = empty_module("demo");
    m.functions = vec![generic_fn("demo.Max", "T", ConstraintBundle {
        interfaces: vec!["Std.IComparable".into()],
        ..Default::default()
    })];
    build_type_registry(&mut m);
    verify_constraints(&m).expect("Std.* references are lazy-loaded");
}

#[test]
fn loader_preserves_type_param_constraint() {
    // L3-G2.5 bare-typeparam: constraint `U: T` stored as type_param_constraint.
    let mut cls = simple_class("Container");
    cls.type_params = vec!["T".into(), "U".into()];
    cls.type_param_constraints = vec![
        ConstraintBundle::default(),
        ConstraintBundle {
            type_param_constraint: Some("T".into()),
            ..Default::default()
        },
    ];
    let mut m = empty_module("demo");
    m.classes = vec![cls];
    build_type_registry(&mut m);

    let td = m.type_registry.get("Container").expect("Container in registry");
    assert_eq!(td.type_param_constraints.len(), 2);
    assert_eq!(td.type_param_constraints[0].type_param_constraint, None);
    assert_eq!(td.type_param_constraints[1].type_param_constraint.as_deref(), Some("T"));

    // verify pass passes through bare-tp constraints without registry lookup.
    verify_constraints(&m).expect("bare typeparam is local-only, no registry lookup needed");
}

#[test]
fn verify_allows_interface_like_name() {
    // Interfaces aren't stored in the class type_registry; soft-allow names starting with "I<Upper>".
    let mut m = empty_module("demo");
    m.functions = vec![generic_fn("demo.F", "T", ConstraintBundle {
        interfaces: vec!["IMyLocal".into()],
        ..Default::default()
    })];
    build_type_registry(&mut m);
    verify_constraints(&m).expect("I<Upper>-prefixed names are interface-shaped");
}
