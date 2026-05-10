//! Build identifier for split-debug-symbols (zbc 1.2 / zpkg 0.3+).
//!
//! A build_id is BLAKE3-128 (first 16 bytes of BLAKE3-256) of the entire
//! main binary file (zbc or zpkg), with the BLID section's 16-byte payload
//! zeroed before hashing. The same build_id is written into both the main
//! file's BLID section and its sidecar (`.zsym`), so the loader can verify
//! pairing.
//!
//! "Integrity-zeroed" hashing: the BLID section is required to be the last
//! section of the main file (writer enforces this), so the trailing 16 bytes
//! are the BLID payload. We zero them by hashing `bytes[..len-16]` then
//! feeding 16 zero bytes.

pub const SIZE: usize = 16;

/// Computes BLAKE3-128 over `bytes`, treating the trailing 16 bytes as zero.
/// Returns a 16-byte build_id.
///
/// Caller is responsible for ensuring the BLID section's 16 bytes are at the
/// tail of `bytes`. The function does not inspect section directory; it just
/// zeroes the trailing 16 bytes for the hash input.
pub fn compute(bytes: &[u8]) -> [u8; SIZE] {
    assert!(bytes.len() >= SIZE, "input must be at least {SIZE} bytes");
    let mut hasher = blake3::Hasher::new();
    hasher.update(&bytes[..bytes.len() - SIZE]);
    hasher.update(&[0u8; SIZE]);
    let mut out = [0u8; SIZE];
    let hash = hasher.finalize();
    out.copy_from_slice(&hash.as_bytes()[..SIZE]);
    out
}

/// Formats the first 4 bytes of a build_id as 8 lowercase hex chars,
/// matching the trace fallback `[build:abcd1234]` suffix.
pub fn short_hex(build_id: &[u8]) -> String {
    assert!(build_id.len() >= 4, "build_id must be at least 4 bytes");
    format!(
        "{:02x}{:02x}{:02x}{:02x}",
        build_id[0], build_id[1], build_id[2], build_id[3],
    )
}

#[cfg(test)]
#[path = "build_id_tests.rs"]
mod build_id_tests;
