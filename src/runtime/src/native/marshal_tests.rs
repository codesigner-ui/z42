//! Round-trip tests for [`super::marshal`].

use super::dispatch::SigType;
use super::marshal::{value_to_z42, z42_to_value};
use crate::metadata::Value;

fn round_trip_int(v: i64, target: SigType) {
    let z = value_to_z42(&Value::I64(v), &target).expect("encode");
    let back = z42_to_value(&z, &target).expect("decode");
    assert_eq!(back, Value::I64(v as i64), "i64 round-trip target={target:?}");
}

#[test]
fn i64_round_trip() {
    for v in [0, 1, -1, i64::MIN, i64::MAX, 0xDEAD_BEEF] {
        round_trip_int(v, SigType::I64);
    }
}

#[test]
fn i32_round_trip_truncates_via_libffi_layer_only() {
    // marshal layer keeps full i64 around; truncation is libffi's job at
    // the cif boundary. So a Value::I64 marshaled as i32 still round-trips
    // to the same Value::I64 here.
    let z = value_to_z42(&Value::I64(0x1234_5678_9ABC_DEF0_u64 as i64), &SigType::I32).unwrap();
    let back = z42_to_value(&z, &SigType::I32).unwrap();
    assert_eq!(back, Value::I64(0x1234_5678_9ABC_DEF0_u64 as i64));
}

#[test]
fn f64_nan_round_trip() {
    let z = value_to_z42(&Value::F64(f64::NAN), &SigType::F64).unwrap();
    let back = z42_to_value(&z, &SigType::F64).unwrap();
    let Value::F64(x) = back else { panic!("expected F64") };
    assert!(x.is_nan(), "NaN must survive round-trip");
}

#[test]
fn bool_round_trip() {
    for b in [true, false] {
        let z = value_to_z42(&Value::Bool(b), &SigType::Bool).unwrap();
        let back = z42_to_value(&z, &SigType::Bool).unwrap();
        assert_eq!(back, Value::Bool(b));
    }
}

#[test]
fn null_into_pointer_target() {
    let z = value_to_z42(&Value::Null, &SigType::Ptr).unwrap();
    assert_eq!(z.payload, 0);
}

#[test]
fn pointer_int_round_trip() {
    // Native pointers are stored as Value::I64 in C2 (typed wrapper lands in C5).
    let v = Value::I64(0x1000_2000_3000_4000_u64 as i64);
    let z = value_to_z42(&v, &SigType::Ptr).unwrap();
    let back = z42_to_value(&z, &SigType::Ptr).unwrap();
    assert_eq!(back, v);
}

#[test]
fn unsupported_combo_errors() {
    // Char doesn't have a marshal path in C2 (covered when stdlib char ops
    // need to cross FFI; for now any non-i64/bool/f64/null is rejected).
    let err = value_to_z42(&Value::Char('z'), &SigType::I64).expect_err("must reject");
    let msg = format!("{err:#}");
    assert!(
        msg.contains("blittable") || msg.contains("Char"),
        "unexpected message: {msg}"
    );
}
