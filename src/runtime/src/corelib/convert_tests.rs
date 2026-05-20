use super::*;
use crate::metadata::Value;
use crate::vm_context::VmContext;

fn ctx() -> std::pin::Pin<Box<VmContext>> { VmContext::new() }

fn parse(builtin: fn(&VmContext, &[Value]) -> anyhow::Result<Value>, s: &str)
    -> anyhow::Result<i64>
{
    match builtin(&ctx(), &[Value::Str(s.to_string())])? {
        Value::I64(n) => Ok(n),
        other => panic!("expected I64, got {:?}", other),
    }
}

#[test]
fn int_parse_accepts_i32_range() {
    assert_eq!(parse(builtin_int_parse, "0").unwrap(), 0);
    assert_eq!(parse(builtin_int_parse, "-2147483648").unwrap(), i32::MIN as i64);
    assert_eq!(parse(builtin_int_parse, "2147483647").unwrap(), i32::MAX as i64);
}

#[test]
fn int_parse_rejects_out_of_range() {
    assert!(parse(builtin_int_parse, "2147483648").is_err());
    assert!(parse(builtin_int_parse, "-2147483649").is_err());
}

#[test]
fn i8_parse_range() {
    assert_eq!(parse(builtin_i8_parse, "-128").unwrap(), -128);
    assert_eq!(parse(builtin_i8_parse, "127").unwrap(), 127);
    assert!(parse(builtin_i8_parse, "128").is_err());
    assert!(parse(builtin_i8_parse, "-129").is_err());
}

#[test]
fn i16_parse_range() {
    assert_eq!(parse(builtin_i16_parse, "-32768").unwrap(), -32768);
    assert_eq!(parse(builtin_i16_parse, "32767").unwrap(), 32767);
    assert!(parse(builtin_i16_parse, "32768").is_err());
}

#[test]
fn u8_parse_range() {
    assert_eq!(parse(builtin_u8_parse, "0").unwrap(), 0);
    assert_eq!(parse(builtin_u8_parse, "255").unwrap(), 255);
    assert!(parse(builtin_u8_parse, "256").is_err());
    assert!(parse(builtin_u8_parse, "-1").is_err());
}

#[test]
fn u16_parse_range() {
    assert_eq!(parse(builtin_u16_parse, "65535").unwrap(), 65535);
    assert!(parse(builtin_u16_parse, "65536").is_err());
    assert!(parse(builtin_u16_parse, "-1").is_err());
}

#[test]
fn u32_parse_range() {
    assert_eq!(parse(builtin_u32_parse, "4294967295").unwrap(), u32::MAX as i64);
    assert!(parse(builtin_u32_parse, "4294967296").is_err());
    assert!(parse(builtin_u32_parse, "-1").is_err());
}

#[test]
fn u64_parse_preserves_bits_above_i64_max() {
    // u64::MAX = 0xFFFF_FFFF_FFFF_FFFF — bit-cast to i64 → -1
    assert_eq!(parse(builtin_u64_parse, "18446744073709551615").unwrap(), -1);
    // i64::MAX + 1 → bit-cast to i64 → i64::MIN
    assert_eq!(parse(builtin_u64_parse, "9223372036854775808").unwrap(), i64::MIN);
    // values within i64::MAX round-trip unchanged
    assert_eq!(parse(builtin_u64_parse, "12345").unwrap(), 12345);
}

#[test]
fn u64_parse_rejects_negative_and_overflow() {
    assert!(parse(builtin_u64_parse, "-1").is_err());
    assert!(parse(builtin_u64_parse, "18446744073709551616").is_err());
}

#[test]
fn parse_rejects_non_numeric() {
    assert!(parse(builtin_int_parse, "abc").is_err());
    assert!(parse(builtin_u8_parse, "12x").is_err());
    assert!(parse(builtin_u64_parse, "").is_err());
}

#[test]
fn parse_trims_whitespace() {
    assert_eq!(parse(builtin_int_parse, "  42 ").unwrap(), 42);
    assert_eq!(parse(builtin_u16_parse, "\t100\n").unwrap(), 100);
}
