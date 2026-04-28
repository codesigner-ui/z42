//! TypedReg-compatible serde helpers for `bytecode::Instruction`.
//!
//! The C# compiler emits register references as `{"id": N, "type": "i32"}`
//! (TypedReg) instead of plain integers. These helpers allow the Rust VM to
//! accept **both** formats so that old `.z42ir.json` files (plain int) and new
//! ones (TypedReg object) deserialize correctly. On serialization we always
//! write a plain integer (the VM does not need the type tag at this stage).
//!
//! Imported by `bytecode.rs` as `use super::bytecode_serde::{...};` so that
//! `#[serde(with = "typed_reg_serde")]` field attributes resolve correctly.
//!
//! Three modules — one per container shape (`u32` / `Vec<u32>` / `Option<u32>`)
//! — are required because `#[serde(with = "...")]` resolves to a *specific*
//! `(deserialize, serialize)` signature pair that varies by container. The
//! common parts (the wire-format enum + the `Plain | Typed → u32` mapping)
//! live as private items at the top of this file; each module's body shrinks
//! to a thin shim over them.

use serde::{Deserialize, Deserializer, Serialize, Serializer};

// ── Shared wire-format ──────────────────────────────────────────────────

/// Intermediate representation for a register that may be either a plain
/// integer or a `{"id": N, "type": "..."}` object. Used by all three modules.
#[derive(Deserialize)]
#[serde(untagged)]
enum RegOrTypedReg {
    Plain(u32),
    Typed { id: u32 },
}

impl RegOrTypedReg {
    /// Collapse either wire shape down to the plain `u32` register id —
    /// the VM has no use for the optional type tag.
    fn to_reg(self) -> u32 {
        match self {
            RegOrTypedReg::Plain(v) => v,
            RegOrTypedReg::Typed { id } => id,
        }
    }
}

// ── Per-container shims ─────────────────────────────────────────────────
//
// Each module has the exact `(deserialize, serialize)` pair signature that
// `#[serde(with = "<module>")]` looks up. Function bodies are now one-liners
// over `RegOrTypedReg` + `to_reg`.

pub(super) mod typed_reg_serde {
    use super::*;

    pub fn deserialize<'de, D: Deserializer<'de>>(d: D) -> Result<u32, D::Error> {
        RegOrTypedReg::deserialize(d).map(RegOrTypedReg::to_reg)
    }

    pub fn serialize<S: Serializer>(value: &u32, s: S) -> Result<S::Ok, S::Error> {
        s.serialize_u32(*value)
    }
}

pub(super) mod typed_reg_vec_serde {
    use super::*;

    pub fn deserialize<'de, D: Deserializer<'de>>(d: D) -> Result<Vec<u32>, D::Error> {
        let items: Vec<RegOrTypedReg> = Vec::deserialize(d)?;
        Ok(items.into_iter().map(RegOrTypedReg::to_reg).collect())
    }

    pub fn serialize<S: Serializer>(value: &[u32], s: S) -> Result<S::Ok, S::Error> {
        value.serialize(s)
    }
}

pub(super) mod typed_reg_opt_serde {
    use super::*;

    pub fn deserialize<'de, D: Deserializer<'de>>(d: D) -> Result<Option<u32>, D::Error> {
        let opt: Option<RegOrTypedReg> = Option::deserialize(d)?;
        Ok(opt.map(RegOrTypedReg::to_reg))
    }

    pub fn serialize<S: Serializer>(value: &Option<u32>, s: S) -> Result<S::Ok, S::Error> {
        value.serialize(s)
    }
}
