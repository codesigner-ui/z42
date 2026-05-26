use super::*;

#[test]
fn compute_is_stable() {
    let a = vec![0xAB; 64];
    let b = vec![0xAB; 64];
    assert_eq!(compute(&a), compute(&b));
}

#[test]
fn compute_differs_on_any_byte_change() {
    let a = vec![0xAB; 64];
    let mut b = vec![0xAB; 64];
    b[10] ^= 1; // perturb a non-trailing byte
    assert_ne!(compute(&a), compute(&b));

    // perturbing a trailing (BLID payload) byte must NOT change the hash —
    // those bytes are zeroed during hashing.
    b = a.clone();
    b[64 - 1] ^= 1;
    assert_eq!(compute(&a), compute(&b));
    let _ = a; // silence unused-mut warning if compiler is strict
}

#[test]
fn compute_produces_16_bytes() {
    let bytes = vec![0u8; 32];
    let id = compute(&bytes);
    assert_eq!(id.len(), SIZE);
    assert_eq!(id.len(), 16);
}

#[test]
#[should_panic]
fn compute_panics_on_too_small_input() {
    let bytes = vec![0u8; 8]; // < SIZE
    let _ = compute(&bytes);
}

#[test]
fn short_hex_lowercase_first_4_bytes() {
    let id = [0xAB, 0xCD, 0x12, 0x34, 0xFF, 0xFF];
    assert_eq!(short_hex(&id), "abcd1234");
}
