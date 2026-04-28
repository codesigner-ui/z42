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

pub(super) mod typed_reg_serde {
    use serde::{self, Deserialize, Deserializer, Serializer};

    /// Intermediate representation for a register that may be either a plain
    /// integer or a `{"id": N, "type": "..."}` object.
    #[derive(Deserialize)]
    #[serde(untagged)]
    enum RegOrTypedReg {
        Plain(u32),
        Typed { id: u32 },
    }

    pub fn deserialize<'de, D>(deserializer: D) -> Result<u32, D::Error>
    where
        D: Deserializer<'de>,
    {
        match RegOrTypedReg::deserialize(deserializer)? {
            RegOrTypedReg::Plain(v) => Ok(v),
            RegOrTypedReg::Typed { id } => Ok(id),
        }
    }

    pub fn serialize<S>(value: &u32, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        serializer.serialize_u32(*value)
    }
}

pub(super) mod typed_reg_vec_serde {
    use serde::{self, Deserialize, Deserializer, Serializer};

    #[derive(Deserialize)]
    #[serde(untagged)]
    enum RegOrTypedReg {
        Plain(u32),
        Typed { id: u32 },
    }

    pub fn deserialize<'de, D>(deserializer: D) -> Result<Vec<u32>, D::Error>
    where
        D: Deserializer<'de>,
    {
        let items: Vec<RegOrTypedReg> = Vec::deserialize(deserializer)?;
        Ok(items.into_iter().map(|r| match r {
            RegOrTypedReg::Plain(v) => v,
            RegOrTypedReg::Typed { id } => id,
        }).collect())
    }

    pub fn serialize<S>(value: &[u32], serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        use serde::ser::SerializeSeq;
        let mut seq = serializer.serialize_seq(Some(value.len()))?;
        for &v in value {
            seq.serialize_element(&v)?;
        }
        seq.end()
    }
}

pub(super) mod typed_reg_opt_serde {
    use serde::{self, Deserialize, Deserializer, Serializer};

    #[derive(Deserialize)]
    #[serde(untagged)]
    enum RegOrTypedReg {
        Plain(u32),
        Typed { id: u32 },
    }

    pub fn deserialize<'de, D>(deserializer: D) -> Result<Option<u32>, D::Error>
    where
        D: Deserializer<'de>,
    {
        let opt: Option<RegOrTypedReg> = Option::deserialize(deserializer)?;
        Ok(opt.map(|r| match r {
            RegOrTypedReg::Plain(v) => v,
            RegOrTypedReg::Typed { id } => id,
        }))
    }

    pub fn serialize<S>(value: &Option<u32>, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        match value {
            Some(v) => serializer.serialize_u32(*v),
            None => serializer.serialize_none(),
        }
    }
}
