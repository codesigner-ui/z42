//! Tests for `bytecode::Instruction` layout + serde wire format.
//!
//! slim-instruction-enum (2026-06-11): name-bearing cold variants are boxed
//! (`Variant(Box<XxxInsn>)`) so the enum stays ≤32 B. These tests pin both the
//! size invariant and the (unchanged) JSON wire format.

use super::{CallInsn, Instruction, ObjNewInsn, StaticSetInsn};

#[test]
fn instruction_size_is_slim() {
    let sz = std::mem::size_of::<Instruction>();
    assert!(sz <= 32, "Instruction = {sz} B (slim-instruction-enum target ≤32)");
}

/// A boxed newtype variant whose inner type is a struct must, under
/// `#[serde(tag = "op")]`, merge the tag into the struct's fields — producing
/// the exact same `{"op":..., <fields>}` JSON as the pre-boxing struct variant.
#[test]
fn boxed_variant_json_wire_format_unchanged() {
    let call = Instruction::Call(Box::new(CallInsn {
        dst: 3,
        func: "Foo.bar".into(),
        args: vec![1, 2].into(),
    }));
    let json = serde_json::to_value(&call).unwrap();

    // Flat shape: tag + payload fields at the top level, no Box wrapper key.
    assert_eq!(json["op"], "call");
    assert_eq!(json["dst"], 3);
    assert_eq!(json["func"], "Foo.bar");
    assert_eq!(json["args"], serde_json::json!([1, 2]));
    assert!(json.get("0").is_none(), "newtype index key leaked into JSON");
    assert!(json.get("data").is_none(), "wrapper field leaked into JSON");

    // Round-trip: JSON → Instruction → JSON must be byte-identical.
    let back: Instruction = serde_json::from_value(json.clone()).unwrap();
    assert_eq!(serde_json::to_value(&back).unwrap(), json);
}

/// `ObjNew` carries `Box<[String]> type_args` with `#[serde(default)]`; confirm
/// the boxed payload still flattens and round-trips, including the default.
#[test]
fn objnew_typeargs_roundtrip() {
    let obj = Instruction::ObjNew(Box::new(ObjNewInsn {
        dst: 0,
        class_name: "Std.Collections.List".into(),
        ctor_name: "List.ctor".into(),
        args: vec![].into(),
        type_args: vec!["int".to_string()].into(),
    }));
    let json = serde_json::to_value(&obj).unwrap();
    assert_eq!(json["op"], "obj_new");
    assert_eq!(json["class_name"], "Std.Collections.List");
    assert_eq!(json["type_args"], serde_json::json!(["int"]));

    let back: Instruction = serde_json::from_value(json.clone()).unwrap();
    assert_eq!(serde_json::to_value(&back).unwrap(), json);
}

/// A boxed variant with no `dst` (`StaticSet`) still flattens correctly.
#[test]
fn staticset_json_wire_format_unchanged() {
    let set = Instruction::StaticSet(Box::new(StaticSetInsn {
        field: "Mod.counter".into(),
        val: 7,
    }));
    let json = serde_json::to_value(&set).unwrap();
    assert_eq!(json["op"], "static_set");
    assert_eq!(json["field"], "Mod.counter");
    assert_eq!(json["val"], 7);

    let back: Instruction = serde_json::from_value(json.clone()).unwrap();
    assert_eq!(serde_json::to_value(&back).unwrap(), json);
}
