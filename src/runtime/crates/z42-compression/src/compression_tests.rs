//! Unit tests for the pure algorithm implementations in `compression.rs`.
//! These run independently of z42vm — pure Rust round-trip + sanity checks.

use super::compression::*;
use super::{
    Z42_COMPRESSION_ERR_DECOMPRESS, Z42_COMPRESSION_ERR_INVALID_LEVEL,
    Z42_COMPRESSION_ERR_INVALID_MODE, Z42_COMPRESSION_ERR_UNKNOWN_SLOT,
};

const SAMPLE: &[u8] =
    b"the quick brown fox jumps over the lazy dog. the quick brown fox jumps over the lazy dog.";

// ── DEFLATE family ──────────────────────────────────────────────────────────

#[test]
fn gzip_round_trip_default_level() {
    let compressed = deflate_compress(SAMPLE, 6, 2).unwrap();
    let decompressed = deflate_decompress(&compressed, 2).unwrap();
    assert_eq!(decompressed, SAMPLE);
}

#[test]
fn zlib_round_trip_max_level() {
    let compressed = deflate_compress(SAMPLE, 9, 1).unwrap();
    let decompressed = deflate_decompress(&compressed, 1).unwrap();
    assert_eq!(decompressed, SAMPLE);
}

#[test]
fn deflate_raw_round_trip_fastest_level() {
    let compressed = deflate_compress(SAMPLE, 1, 0).unwrap();
    let decompressed = deflate_decompress(&compressed, 0).unwrap();
    assert_eq!(decompressed, SAMPLE);
}

#[test]
fn gzip_magic_bytes() {
    let compressed = deflate_compress(b"x", 6, 2).unwrap();
    assert_eq!(compressed[0], 0x1f);
    assert_eq!(compressed[1], 0x8b);
}

#[test]
fn zlib_header_is_mod_31() {
    let compressed = deflate_compress(b"x", 6, 1).unwrap();
    let header = (compressed[0] as u16) * 256 + (compressed[1] as u16);
    assert_eq!(header % 31, 0);
}

#[test]
fn gzip_shrinks_repetitive_input() {
    let long_input: Vec<u8> = b"hello world ".iter().cycle().take(10_000).copied().collect();
    let compressed = deflate_compress(&long_input, 6, 2).unwrap();
    assert!(compressed.len() < long_input.len() / 10);
}

#[test]
fn deflate_decompress_invalid_data() {
    let err = deflate_decompress(&[0xde, 0xad, 0xbe, 0xef, 0x00, 0x11, 0x22], 2).unwrap_err();
    assert_eq!(err.0, Z42_COMPRESSION_ERR_DECOMPRESS);
    assert!(err.1.contains("invalid gzip"));
}

#[test]
fn deflate_compress_invalid_level() {
    let err = deflate_compress(b"x", 0, 2).unwrap_err();
    assert_eq!(err.0, Z42_COMPRESSION_ERR_INVALID_LEVEL);
}

#[test]
fn deflate_compress_invalid_mode() {
    let err = deflate_compress(b"x", 6, 99).unwrap_err();
    assert_eq!(err.0, Z42_COMPRESSION_ERR_INVALID_MODE);
}

// ── Zstd ────────────────────────────────────────────────────────────────────

#[test]
fn zstd_round_trip_default_level() {
    let compressed = zstd_compress(SAMPLE, 3).unwrap();
    let decompressed = zstd_decompress(&compressed).unwrap();
    assert_eq!(decompressed, SAMPLE);
}

#[test]
fn zstd_round_trip_max_level() {
    let compressed = zstd_compress(SAMPLE, 22).unwrap();
    let decompressed = zstd_decompress(&compressed).unwrap();
    assert_eq!(decompressed, SAMPLE);
}

#[test]
fn zstd_magic_bytes() {
    let compressed = zstd_compress(b"x", 3).unwrap();
    assert_eq!(&compressed[..4], &[0x28, 0xb5, 0x2f, 0xfd]);
}

#[test]
fn zstd_decompress_invalid_data() {
    let err = zstd_decompress(&[0xde, 0xad, 0xbe, 0xef]).unwrap_err();
    assert_eq!(err.0, Z42_COMPRESSION_ERR_DECOMPRESS);
}

#[test]
fn zstd_compress_invalid_level() {
    let err = zstd_compress(b"x", 23).unwrap_err();
    assert_eq!(err.0, Z42_COMPRESSION_ERR_INVALID_LEVEL);
}

// ── Streaming ───────────────────────────────────────────────────────────────

#[test]
fn streaming_gzip_encoder_matches_one_shot_round_trip() {
    let one_shot = deflate_compress(SAMPLE, 6, 2).unwrap();
    let id = compressor_begin(2, 6, false).unwrap();
    let mut streamed = Vec::new();
    for chunk in SAMPLE.chunks(SAMPLE.len() / 3) {
        streamed.extend(compressor_feed(id, chunk).unwrap());
    }
    streamed.extend(compressor_finish(id).unwrap());
    let decoded_streamed = deflate_decompress(&streamed, 2).unwrap();
    let decoded_oneshot  = deflate_decompress(&one_shot, 2).unwrap();
    assert_eq!(decoded_streamed, SAMPLE);
    assert_eq!(decoded_oneshot,  SAMPLE);
}

#[test]
fn streaming_gzip_decoder_accumulates_and_decodes() {
    let compressed = deflate_compress(SAMPLE, 6, 2).unwrap();
    let id = compressor_begin(2, 0, true).unwrap();
    for chunk in compressed.chunks((compressed.len() / 4).max(1)) {
        let out = compressor_feed(id, chunk).unwrap();
        assert!(out.is_empty(), "v0 decoder buffers; feed should yield empty");
    }
    let decoded = compressor_finish(id).unwrap();
    assert_eq!(decoded, SAMPLE);
}

#[test]
fn streaming_zstd_encoder_round_trip() {
    let id = compressor_begin(10, 3, false).unwrap();
    let mut streamed = Vec::new();
    for chunk in SAMPLE.chunks(20) {
        streamed.extend(compressor_feed(id, chunk).unwrap());
    }
    streamed.extend(compressor_finish(id).unwrap());
    let decoded = zstd_decompress(&streamed).unwrap();
    assert_eq!(decoded, SAMPLE);
}

#[test]
fn compressor_slot_ids_monotonic() {
    let id1 = compressor_begin(2, 6, false).unwrap();
    let id2 = compressor_begin(2, 6, false).unwrap();
    assert!(id2 > id1);
    compressor_dispose(id1);
    compressor_dispose(id2);
}

#[test]
fn compressor_feed_unknown_slot() {
    let err = compressor_feed(99_999_999, b"x").unwrap_err();
    assert_eq!(err.0, Z42_COMPRESSION_ERR_UNKNOWN_SLOT);
}

#[test]
fn compressor_dispose_unknown_slot_silent() {
    // Idempotent — no error
    compressor_dispose(99_999_999);
}

#[test]
fn compressor_begin_unknown_algo() {
    let err = compressor_begin(42, 6, false).unwrap_err();
    assert_eq!(err.0, Z42_COMPRESSION_ERR_INVALID_MODE);
}
