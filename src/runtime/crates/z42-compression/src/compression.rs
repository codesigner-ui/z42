//! Algorithm implementations. Pure Rust (no z42 type deps); each function
//! returns `Result<Vec<u8>, (error_code, message)>` so [`lib.rs`] can
//! translate failures into the C ABI return code + thread-local
//! last-error slot.

use std::collections::HashMap;
use std::io::{Read, Write};
use std::sync::{Mutex, OnceLock};
use std::sync::atomic::{AtomicU64, Ordering};

use flate2::Compression as FlateLevel;
use flate2::read::{DeflateDecoder, GzDecoder, ZlibDecoder};
use flate2::write::{DeflateEncoder, GzEncoder, ZlibEncoder};

use crate::{
    Z42_COMPRESSION_ERR_COMPRESS, Z42_COMPRESSION_ERR_DECOMPRESS,
    Z42_COMPRESSION_ERR_INVALID_INPUT, Z42_COMPRESSION_ERR_INVALID_LEVEL,
    Z42_COMPRESSION_ERR_INVALID_MODE, Z42_COMPRESSION_ERR_UNKNOWN_SLOT,
};

// ── Mode IDs (must stay in sync with z42 Std.Compression.Algo) ──────────────
const ALGO_DEFLATE_RAW: i32 = 0;
const ALGO_ZLIB:        i32 = 1;
const ALGO_GZIP:        i32 = 2;
const ALGO_ZSTD:        i32 = 10;

type AlgoResult = Result<Vec<u8>, (i32, String)>;

fn flate_level(level: i32) -> Result<FlateLevel, (i32, String)> {
    match level {
        1..=9 => Ok(FlateLevel::new(level as u32)),
        n => Err((Z42_COMPRESSION_ERR_INVALID_LEVEL,
                  format!("level must be 1..=9, got {n}"))),
    }
}

fn zstd_level(level: i32) -> Result<i32, (i32, String)> {
    match level {
        1..=22 => Ok(level),
        n => Err((Z42_COMPRESSION_ERR_INVALID_LEVEL,
                  format!("level must be 1..=22, got {n}"))),
    }
}

// ── One-shot ─────────────────────────────────────────────────────────────────

pub fn deflate_compress(input: &[u8], level: i32, mode: i32) -> AlgoResult {
    let level = flate_level(level)?;
    let mut out = Vec::with_capacity(input.len() / 2 + 32);
    match mode {
        ALGO_DEFLATE_RAW => {
            let mut enc = DeflateEncoder::new(&mut out, level);
            enc.write_all(input).map_err(|e| (Z42_COMPRESSION_ERR_COMPRESS, e.to_string()))?;
            enc.finish().map_err(|e| (Z42_COMPRESSION_ERR_COMPRESS, e.to_string()))?;
        }
        ALGO_ZLIB => {
            let mut enc = ZlibEncoder::new(&mut out, level);
            enc.write_all(input).map_err(|e| (Z42_COMPRESSION_ERR_COMPRESS, e.to_string()))?;
            enc.finish().map_err(|e| (Z42_COMPRESSION_ERR_COMPRESS, e.to_string()))?;
        }
        ALGO_GZIP => {
            let mut enc = GzEncoder::new(&mut out, level);
            enc.write_all(input).map_err(|e| (Z42_COMPRESSION_ERR_COMPRESS, e.to_string()))?;
            enc.finish().map_err(|e| (Z42_COMPRESSION_ERR_COMPRESS, e.to_string()))?;
        }
        other => return Err((Z42_COMPRESSION_ERR_INVALID_MODE,
                             format!("unsupported deflate mode {other} (0=raw 1=zlib 2=gzip)"))),
    }
    Ok(out)
}

pub fn deflate_decompress(input: &[u8], mode: i32) -> AlgoResult {
    let mut out = Vec::with_capacity(input.len() * 2);
    match mode {
        ALGO_DEFLATE_RAW => DeflateDecoder::new(input).read_to_end(&mut out)
            .map_err(|e| (Z42_COMPRESSION_ERR_DECOMPRESS, format!("invalid raw deflate data: {e}")))?,
        ALGO_ZLIB => ZlibDecoder::new(input).read_to_end(&mut out)
            .map_err(|e| (Z42_COMPRESSION_ERR_DECOMPRESS, format!("invalid zlib data: {e}")))?,
        ALGO_GZIP => GzDecoder::new(input).read_to_end(&mut out)
            .map_err(|e| (Z42_COMPRESSION_ERR_DECOMPRESS, format!("invalid gzip data: {e}")))?,
        other => return Err((Z42_COMPRESSION_ERR_INVALID_MODE,
                             format!("unsupported deflate mode {other} (0=raw 1=zlib 2=gzip)"))),
    };
    Ok(out)
}

pub fn zstd_compress(input: &[u8], level: i32) -> AlgoResult {
    let level = zstd_level(level)?;
    zstd::stream::encode_all(input, level)
        .map_err(|e| (Z42_COMPRESSION_ERR_COMPRESS, e.to_string()))
}

pub fn zstd_decompress(input: &[u8]) -> AlgoResult {
    zstd::stream::decode_all(input)
        .map_err(|e| (Z42_COMPRESSION_ERR_DECOMPRESS, format!("invalid zstd data: {e}")))
}

// ── Streaming slot table ────────────────────────────────────────────────────
//
// Process-global (cdylib's static state). Thread-safe via Mutex. The
// z42-side wrapper marshals the u64 slot id back to z42 as i64 via the
// `__compressor_*` builtin set.

enum Handle {
    DeflateEnc(DeflateEncoder<Vec<u8>>),
    ZlibEnc(ZlibEncoder<Vec<u8>>),
    GzipEnc(GzEncoder<Vec<u8>>),
    ZstdEnc(zstd::stream::Encoder<'static, Vec<u8>>),
    // Decoders v0: accumulate fed chunks, bulk-decompress at finish.
    // Simpler than implementing flate2::Decompress state machine; v1
    // can upgrade to true streaming decode.
    DeflateDec(Vec<u8>),
    ZlibDec(Vec<u8>),
    GzipDec(Vec<u8>),
    ZstdDec(Vec<u8>),
}

fn slots() -> &'static Mutex<HashMap<u64, Handle>> {
    static SLOTS: OnceLock<Mutex<HashMap<u64, Handle>>> = OnceLock::new();
    SLOTS.get_or_init(|| Mutex::new(HashMap::new()))
}

fn next_slot_id() -> u64 {
    static NEXT: AtomicU64 = AtomicU64::new(1);
    NEXT.fetch_add(1, Ordering::Relaxed)
}

pub fn compressor_begin(algo: i32, level: i32, is_decompress: bool) -> Result<u64, (i32, String)> {
    let handle = if is_decompress {
        match algo {
            ALGO_DEFLATE_RAW => Handle::DeflateDec(Vec::new()),
            ALGO_ZLIB        => Handle::ZlibDec(Vec::new()),
            ALGO_GZIP        => Handle::GzipDec(Vec::new()),
            ALGO_ZSTD        => Handle::ZstdDec(Vec::new()),
            other => return Err((Z42_COMPRESSION_ERR_INVALID_MODE,
                                 format!("unknown algo {other} for decompress"))),
        }
    } else {
        match algo {
            ALGO_DEFLATE_RAW => Handle::DeflateEnc(DeflateEncoder::new(Vec::new(), flate_level(level)?)),
            ALGO_ZLIB        => Handle::ZlibEnc(ZlibEncoder::new(Vec::new(), flate_level(level)?)),
            ALGO_GZIP        => Handle::GzipEnc(GzEncoder::new(Vec::new(), flate_level(level)?)),
            ALGO_ZSTD => {
                let enc = zstd::stream::Encoder::new(Vec::new(), zstd_level(level)?)
                    .map_err(|e| (Z42_COMPRESSION_ERR_COMPRESS, e.to_string()))?;
                Handle::ZstdEnc(enc)
            }
            other => return Err((Z42_COMPRESSION_ERR_INVALID_MODE,
                                 format!("unknown algo {other} for compress"))),
        }
    };
    let id = next_slot_id();
    slots().lock().unwrap().insert(id, handle);
    Ok(id)
}

pub fn compressor_feed(slot_id: u64, chunk: &[u8]) -> Result<Vec<u8>, (i32, String)> {
    let mut s = slots().lock().unwrap();
    let h = s.get_mut(&slot_id)
        .ok_or((Z42_COMPRESSION_ERR_UNKNOWN_SLOT, format!("slot {slot_id} not found")))?;
    let out = match h {
        Handle::DeflateEnc(e) => {
            e.write_all(chunk).map_err(|e| (Z42_COMPRESSION_ERR_COMPRESS, e.to_string()))?;
            std::mem::take(e.get_mut())
        }
        Handle::ZlibEnc(e) => {
            e.write_all(chunk).map_err(|e| (Z42_COMPRESSION_ERR_COMPRESS, e.to_string()))?;
            std::mem::take(e.get_mut())
        }
        Handle::GzipEnc(e) => {
            e.write_all(chunk).map_err(|e| (Z42_COMPRESSION_ERR_COMPRESS, e.to_string()))?;
            std::mem::take(e.get_mut())
        }
        Handle::ZstdEnc(e) => {
            e.write_all(chunk).map_err(|e| (Z42_COMPRESSION_ERR_COMPRESS, e.to_string()))?;
            std::mem::take(e.get_mut())
        }
        Handle::DeflateDec(buf) | Handle::ZlibDec(buf)
        | Handle::GzipDec(buf) | Handle::ZstdDec(buf) => {
            buf.extend_from_slice(chunk);
            Vec::new()
        }
    };
    Ok(out)
}

pub fn compressor_finish(slot_id: u64) -> Result<Vec<u8>, (i32, String)> {
    let h = slots().lock().unwrap().remove(&slot_id)
        .ok_or((Z42_COMPRESSION_ERR_UNKNOWN_SLOT, format!("slot {slot_id} not found")))?;
    match h {
        Handle::DeflateEnc(e) => e.finish().map_err(|e| (Z42_COMPRESSION_ERR_COMPRESS, e.to_string())),
        Handle::ZlibEnc(e)    => e.finish().map_err(|e| (Z42_COMPRESSION_ERR_COMPRESS, e.to_string())),
        Handle::GzipEnc(e)    => e.finish().map_err(|e| (Z42_COMPRESSION_ERR_COMPRESS, e.to_string())),
        Handle::ZstdEnc(e)    => e.finish().map_err(|e| (Z42_COMPRESSION_ERR_COMPRESS, e.to_string())),
        Handle::DeflateDec(buf) => deflate_decompress(&buf, ALGO_DEFLATE_RAW),
        Handle::ZlibDec(buf)    => deflate_decompress(&buf, ALGO_ZLIB),
        Handle::GzipDec(buf)    => deflate_decompress(&buf, ALGO_GZIP),
        Handle::ZstdDec(buf)    => zstd_decompress(&buf),
    }
}

pub fn compressor_dispose(slot_id: u64) {
    let _ = slots().lock().unwrap().remove(&slot_id);
}

// Suppress dead-code lint when no callers reference these helpers under
// certain feature combinations.
#[allow(dead_code)]
const _: i32 = Z42_COMPRESSION_ERR_INVALID_INPUT;
