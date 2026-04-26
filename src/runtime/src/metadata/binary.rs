/// Binary decoder for ZBC v0.3 and ZPK v0.1 formats.
///
/// Mirrors the C# ZbcReader + ZpkgReader exactly.
/// See `docs/design/ir.md` and `docs/design/project.md` for format specs.
use std::collections::HashMap;

use anyhow::{bail, Result};

use super::bytecode::{
    BasicBlock, ClassDesc, ConstraintBundle, ExceptionEntry, FieldDesc, Function, Instruction, Module, Terminator,
};
use super::formats::{ZBC_MAGIC, ZPKG_MAGIC};
use super::types::ExecMode;

// ── Opcode constants (mirrors C# Opcodes) ────────────────────────────────────

const OP_CONST_I: u8      = 0x00;
const OP_CONST_F: u8      = 0x01;
const OP_CONST_BOOL: u8   = 0x02;
const OP_CONST_STR: u8    = 0x03;
const OP_CONST_NULL: u8   = 0x04;
const OP_COPY: u8         = 0x05;
const OP_CONST_CHAR: u8   = 0x08;
const OP_STORE: u8        = 0x06;
const OP_LOAD: u8         = 0x07;
const OP_ADD: u8          = 0x10;
const OP_SUB: u8          = 0x11;
const OP_MUL: u8          = 0x12;
const OP_DIV: u8          = 0x13;
const OP_REM: u8          = 0x14;
const OP_NEG: u8          = 0x15;
const OP_AND: u8          = 0x16;
const OP_OR: u8           = 0x17;
const OP_NOT: u8          = 0x18;
const OP_BIT_AND: u8      = 0x19;
const OP_BIT_OR: u8       = 0x1A;
const OP_BIT_XOR: u8      = 0x1B;
const OP_BIT_NOT: u8      = 0x1C;
const OP_SHL: u8          = 0x1D;
const OP_SHR: u8          = 0x1E;
const OP_TO_STR: u8       = 0x1F;
const OP_EQ: u8           = 0x30;
const OP_NE: u8           = 0x31;
const OP_LT: u8           = 0x32;
const OP_LE: u8           = 0x33;
const OP_GT: u8           = 0x34;
const OP_GE: u8           = 0x35;
const OP_BR: u8           = 0x40;
const OP_BR_COND: u8      = 0x41;
const OP_RET: u8          = 0x42;
const OP_RET_VAL: u8      = 0x43;
const OP_THROW: u8        = 0x44;
const OP_CALL: u8         = 0x50;
const OP_BUILTIN: u8      = 0x51;
const OP_VCALL: u8        = 0x52;
const OP_FIELD_GET: u8    = 0x60;
const OP_FIELD_SET: u8    = 0x61;
const OP_STATIC_GET: u8   = 0x62;
const OP_STATIC_SET: u8   = 0x63;
const OP_OBJ_NEW: u8      = 0x70;
const OP_IS_INSTANCE: u8  = 0x71;
const OP_AS_CAST: u8      = 0x72;
const OP_ARRAY_NEW: u8     = 0x80;
const OP_ARRAY_NEW_LIT: u8 = 0x81;
const OP_ARRAY_GET: u8     = 0x82;
const OP_ARRAY_SET: u8     = 0x83;
const OP_ARRAY_LEN: u8     = 0x84;
const OP_STR_CONCAT: u8    = 0x85;

// Type-tag for i64 (used to distinguish ConstI variants)
const TT_I64: u8 = 0x05;

// ── Public entry points ───────────────────────────────────────────────────────

/// Decode a ZBC v0.3 binary buffer into a `Module`.
pub fn decode_zbc(data: &[u8]) -> Result<Module> {
    if data.len() < 16 || &data[0..4] != ZBC_MAGIC {
        bail!("not a binary zbc file (bad magic or too short)");
    }

    let sec_count = u16::from_le_bytes([data[10], data[11]]) as usize;
    let dir       = read_directory(data, sec_count)?;

    let pool     = read_strs_section(data, &dir, "STRS")
        .or_else(|_| read_strs_section(data, &dir, "BSTR"))
        .unwrap_or_default();
    let ns       = read_nspc_section(data, &dir).unwrap_or_default();
    let classes  = read_type_section(data, &dir, &pool).unwrap_or_default();
    let sigs     = read_sigs_section(data, &dir, &pool).unwrap_or_default();
    let bodies   = read_func_section(data, &dir, &pool)?;

    assemble_module(&ns, &pool, classes, sigs, bodies)
}

/// Decode a packed ZPK v0.1 binary buffer, returning one `Module` per embedded module.
pub fn decode_zpkg_packed(data: &[u8]) -> Result<Vec<Module>> {
    if data.len() < 16 || &data[0..4] != ZPKG_MAGIC {
        bail!("not a binary zpkg file (bad magic or too short)");
    }

    let sec_count = u16::from_le_bytes([data[10], data[11]]) as usize;
    let dir       = read_directory(data, sec_count)?;

    let pool     = read_strs_section(data, &dir, "STRS").unwrap_or_default();
    let all_sigs = read_zpkg_sigs_section(data, &dir, &pool).unwrap_or_default();

    let mods_data = match section_bytes(data, &dir, "MODS") {
        Some(s) => s,
        None    => return Ok(vec![]),
    };

    decode_mods_section(mods_data, &pool, &all_sigs)
}

/// Read the namespace list from a ZPK binary (fast path, no module decode).
pub fn read_zpkg_namespaces(data: &[u8]) -> Result<Vec<String>> {
    if data.len() < 16 || &data[0..4] != ZPKG_MAGIC {
        bail!("not a binary zpkg file");
    }
    let sec_count = u16::from_le_bytes([data[10], data[11]]) as usize;
    let dir       = read_directory(data, sec_count)?;
    let pool      = read_strs_section(data, &dir, "STRS").unwrap_or_default();

    let nspc_data = match section_bytes(data, &dir, "NSPC") {
        Some(s) => s,
        None    => return Ok(vec![]),
    };

    let mut r  = Reader::new(nspc_data);
    let count  = r.u32()? as usize;
    let mut ns = Vec::with_capacity(count);
    for _ in 0..count {
        ns.push(pool_str(&pool, r.u32()? as usize)?.to_owned());
    }
    Ok(ns)
}

/// Returns package entry-point hint from META section of a ZPK binary, or None.
pub fn read_zpkg_entry(data: &[u8]) -> Result<Option<String>> {
    if data.len() < 16 || &data[0..4] != ZPKG_MAGIC {
        bail!("not a binary zpkg file");
    }
    let sec_count = u16::from_le_bytes([data[10], data[11]]) as usize;
    let dir       = read_directory(data, sec_count)?;

    let meta = match section_bytes(data, &dir, "META") {
        Some(s) => s,
        None    => return Ok(None),
    };
    let mut r = Reader::new(meta);
    let _name    = r.inline_str()?;
    let _version = r.inline_str()?;
    let entry    = r.inline_str()?;
    Ok(if entry.is_empty() { None } else { Some(entry) })
}

/// Returns true if the ZPK binary has the EXE flag set.
pub fn read_zpkg_is_exe(data: &[u8]) -> bool {
    data.len() >= 10 && (u16::from_le_bytes([data[8], data[9]]) & 0x02) != 0
}

/// Read dependency list from a ZPK binary.
pub fn read_zpkg_deps(data: &[u8]) -> Result<Vec<(String, Vec<String>)>> {
    if data.len() < 16 || &data[0..4] != ZPKG_MAGIC {
        bail!("not a binary zpkg file");
    }
    let sec_count = u16::from_le_bytes([data[10], data[11]]) as usize;
    let dir       = read_directory(data, sec_count)?;
    let pool      = read_strs_section(data, &dir, "STRS").unwrap_or_default();

    let deps_data = match section_bytes(data, &dir, "DEPS") {
        Some(s) => s,
        None    => return Ok(vec![]),
    };

    let mut r     = Reader::new(deps_data);
    let count     = r.u32()? as usize;
    let mut deps  = Vec::with_capacity(count);
    for _ in 0..count {
        let file  = pool_str(&pool, r.u32()? as usize)?.to_owned();
        let ns_ct = r.u16()? as usize;
        let mut nss = Vec::with_capacity(ns_ct);
        for _ in 0..ns_ct {
            nss.push(pool_str(&pool, r.u32()? as usize)?.to_owned());
        }
        deps.push((file, nss));
    }
    Ok(deps)
}

// ── Section directory ─────────────────────────────────────────────────────────

fn read_directory(data: &[u8], sec_count: usize) -> Result<HashMap<[u8; 4], (usize, usize)>> {
    let mut dir = HashMap::new();
    let mut pos = 16usize;
    for _ in 0..sec_count {
        if pos + 12 > data.len() { break; }
        let tag: [u8; 4] = data[pos..pos+4].try_into().unwrap();
        let offset = u32::from_le_bytes(data[pos+4..pos+8].try_into().unwrap()) as usize;
        let size   = u32::from_le_bytes(data[pos+8..pos+12].try_into().unwrap()) as usize;
        dir.insert(tag, (offset, size));
        pos += 12;
    }
    Ok(dir)
}

fn section_bytes<'a>(
    data: &'a [u8],
    dir: &HashMap<[u8; 4], (usize, usize)>,
    tag: &str,
) -> Option<&'a [u8]> {
    let tag_bytes: [u8; 4] = tag.as_bytes().try_into().ok()?;
    let (offset, size) = dir.get(&tag_bytes)?;
    data.get(*offset..*offset + *size)
}

// ── STRS section ──────────────────────────────────────────────────────────────

fn read_strs_section(
    data: &[u8],
    dir:  &HashMap<[u8; 4], (usize, usize)>,
    tag:  &str,
) -> Result<Vec<String>> {
    let sec = section_bytes(data, dir, tag)
        .ok_or_else(|| anyhow::anyhow!("section {tag} not found"))?;
    let mut r = Reader::new(sec);
    let count = r.u32()? as usize;
    let mut entries = Vec::with_capacity(count);
    for _ in 0..count {
        let off = r.u32()? as usize;
        let len = r.u32()? as usize;
        entries.push((off, len));
    }
    let data_base = r.pos;
    let mut result = Vec::with_capacity(count);
    for (off, len) in entries {
        let start = data_base + off;
        let end   = start + len;
        if end > sec.len() { bail!("STRS entry out of bounds"); }
        result.push(std::str::from_utf8(&sec[start..end])?.to_owned());
    }
    Ok(result)
}

// ── NSPC section ─────────────────────────────────────────────────────────────

fn read_nspc_section(
    data: &[u8],
    dir:  &HashMap<[u8; 4], (usize, usize)>,
) -> Result<String> {
    let sec = section_bytes(data, dir, "NSPC")
        .ok_or_else(|| anyhow::anyhow!("no NSPC"))?;
    if sec.len() < 2 { return Ok(String::new()); }
    let len = u16::from_le_bytes([sec[0], sec[1]]) as usize;
    if len == 0 || sec.len() < 2 + len { return Ok(String::new()); }
    Ok(std::str::from_utf8(&sec[2..2+len])?.to_owned())
}

// ── TYPE section ──────────────────────────────────────────────────────────────

fn read_type_section(
    data: &[u8],
    dir:  &HashMap<[u8; 4], (usize, usize)>,
    pool: &[String],
) -> Result<Vec<ClassDesc>> {
    let sec = match section_bytes(data, dir, "TYPE") {
        Some(s) => s,
        None    => return Ok(vec![]),
    };
    decode_type_section(sec, pool)
}

fn decode_type_section(sec: &[u8], pool: &[String]) -> Result<Vec<ClassDesc>> {
    let mut r = Reader::new(sec);
    let count = r.u32()? as usize;
    let mut classes = Vec::with_capacity(count);
    for _ in 0..count {
        let name     = pool_str(pool, r.u32()? as usize)?.to_owned();
        let base_raw = r.u32()?;
        let base     = if base_raw == u32::MAX {
            None
        } else {
            Some(pool_str(pool, base_raw as usize)?.to_owned())
        };
        let fld_count = r.u16()? as usize;
        let mut fields = Vec::with_capacity(fld_count);
        for _ in 0..fld_count {
            let fname = pool_str(pool, r.u32()? as usize)?.to_owned();
            let ftype = type_tag_to_str(r.u8()?).to_owned();
            fields.push(FieldDesc { name: fname, type_tag: ftype });
        }
        // Generic type parameters + per-tp constraints (L3-G3a)
        let tp_count = r.u8()? as usize;
        let mut type_params = Vec::with_capacity(tp_count);
        let mut type_param_constraints = Vec::with_capacity(tp_count);
        for _ in 0..tp_count {
            type_params.push(pool_str(pool, r.u32()? as usize)?.to_owned());
            type_param_constraints.push(decode_constraint_bundle(&mut r, pool)?);
        }
        classes.push(ClassDesc {
            name, base_class: base, fields, type_params, type_param_constraints,
        });
    }
    Ok(classes)
}

/// Decode one constraint bundle from the TYPE/SIGS reader cursor (v0.6).
fn decode_constraint_bundle(r: &mut Reader, pool: &[String]) -> Result<ConstraintBundle> {
    let flags = r.u8()?;
    let requires_class       = (flags & 0x01) != 0;
    let requires_struct      = (flags & 0x02) != 0;
    let has_base             = (flags & 0x04) != 0;
    let has_type_param       = (flags & 0x08) != 0;
    let requires_constructor = (flags & 0x10) != 0;
    let requires_enum        = (flags & 0x20) != 0;
    let base_class = if has_base {
        Some(pool_str(pool, r.u32()? as usize)?.to_owned())
    } else { None };
    let type_param_constraint = if has_type_param {
        Some(pool_str(pool, r.u32()? as usize)?.to_owned())
    } else { None };
    let iface_count = r.u8()? as usize;
    let mut interfaces = Vec::with_capacity(iface_count);
    for _ in 0..iface_count {
        interfaces.push(pool_str(pool, r.u32()? as usize)?.to_owned());
    }
    Ok(ConstraintBundle {
        requires_class, requires_struct, base_class, interfaces, type_param_constraint,
        requires_constructor, requires_enum,
    })
}

// ── SIGS section ──────────────────────────────────────────────────────────────

type SigEntry = (String, usize, String, ExecMode);

fn read_sigs_section(
    data: &[u8],
    dir:  &HashMap<[u8; 4], (usize, usize)>,
    pool: &[String],
) -> Result<Vec<SigEntry>> {
    let sec = match section_bytes(data, dir, "SIGS") {
        Some(s) => s,
        None    => return Ok(vec![]),
    };
    decode_sigs_section(sec, pool)
}

fn decode_sigs_section(sec: &[u8], pool: &[String]) -> Result<Vec<SigEntry>> {
    let mut r = Reader::new(sec);
    let count = r.u32()? as usize;
    let mut sigs = Vec::with_capacity(count);
    for _ in 0..count {
        let name       = pool_str(pool, r.u32()? as usize)?.to_owned();
        let param_count = r.u16()? as usize;
        let ret_type   = type_tag_to_str(r.u8()?).to_owned();
        let exec_mode  = exec_mode_from_byte(r.u8()?);
        sigs.push((name, param_count, ret_type, exec_mode));
    }
    Ok(sigs)
}

fn read_zpkg_sigs_section(
    data: &[u8],
    dir:  &HashMap<[u8; 4], (usize, usize)>,
    pool: &[String],
) -> Result<Vec<SigEntry>> {
    // ZPKG SIGS section has the same layout as ZBC SIGS
    read_sigs_section(data, dir, pool)
}

// ── FUNC section ─────────────────────────────────────────────────────────────

type FuncBody = (Vec<BasicBlock>, Vec<ExceptionEntry>, Vec<crate::metadata::bytecode::LineEntry>);

fn read_func_section(
    data: &[u8],
    dir:  &HashMap<[u8; 4], (usize, usize)>,
    pool: &[String],
) -> Result<Vec<FuncBody>> {
    let sec = match section_bytes(data, dir, "FUNC") {
        Some(s) => s,
        None    => return Ok(vec![]),
    };
    decode_func_section(sec, pool)
}

fn decode_func_section(sec: &[u8], pool: &[String]) -> Result<Vec<FuncBody>> {
    let mut r = Reader::new(sec);
    let func_count = r.u32()? as usize;
    let mut result = Vec::with_capacity(func_count);

    for _ in 0..func_count {
        let _reg_count  = r.u16()?;
        let block_count = r.u16()? as usize;
        let instr_len   = r.u32()? as usize;
        let exc_count   = r.u16()? as usize;
        let line_count  = r.u16()? as usize;

        let mut block_offsets = Vec::with_capacity(block_count);
        for _ in 0..block_count {
            block_offsets.push(r.u32()? as usize);
        }

        let mut raw_exc = Vec::with_capacity(exc_count);
        for _ in 0..exc_count {
            let try_s    = r.u16()? as usize;
            let try_e    = r.u16()? as usize;
            let catch_b  = r.u16()? as usize;
            let catch_t  = r.u32()?;
            let catch_r  = r.u16()?;
            raw_exc.push((try_s, try_e, catch_b, catch_t, catch_r));
        }

        let mut line_table = Vec::with_capacity(line_count);
        for _ in 0..line_count {
            let blk  = r.u16()? as u32;
            let ins  = r.u16()? as u32;
            let line = r.u32()?;
            let file_id = r.u32()?;
            let file = if file_id == u32::MAX { None } else {
                pool.get(file_id as usize).cloned()
            };
            line_table.push(crate::metadata::bytecode::LineEntry { block: blk, instr: ins, line, file });
        }

        let instr_bytes = r.bytes(instr_len)?;

        // Decode blocks
        let mut blocks = Vec::with_capacity(block_count);
        for bi in 0..block_count {
            let start = block_offsets[bi];
            let end   = if bi + 1 < block_count { block_offsets[bi + 1] } else { instr_len };
            let label = block_label(bi);
            let (instrs, term) = decode_block(&instr_bytes[start..end], pool)?;
            blocks.push(BasicBlock { label, instructions: instrs, terminator: term });
        }

        // Resolve exception table
        let exc_table: Vec<ExceptionEntry> = raw_exc
            .into_iter()
            .map(|(try_s, try_e, catch_b, catch_t, catch_r)| ExceptionEntry {
                try_start:   block_label(try_s),
                try_end:     if try_e < block_count { block_label(try_e) }
                             else { format!("block_{}", block_count) },
                catch_label: block_label(catch_b),
                catch_type:  if catch_t == u32::MAX { None }
                             else { pool.get(catch_t as usize).map(|s| s.clone()) },
                catch_reg:   catch_r as u32,
            })
            .collect();

        result.push((blocks, exc_table, line_table));
    }
    Ok(result)
}

// ── MODS section (zpkg packed mode) ──────────────────────────────────────────

fn decode_mods_section(
    sec:      &[u8],
    pool:     &[String],
    all_sigs: &[SigEntry],
) -> Result<Vec<Module>> {
    let mut r = Reader::new(sec);
    let mod_count = r.u32()? as usize;
    let mut modules = Vec::with_capacity(mod_count);

    for _ in 0..mod_count {
        let ns          = pool_str(pool, r.u32()? as usize)?.to_owned();
        let _src_file   = r.u32()?;  // source file index — not used by VM
        let _src_hash   = r.u32()?;  // source hash index — not used by VM
        let func_count  = r.u16()? as usize;
        let first_sig   = r.u32()? as usize;

        let func_body_size = r.u32()? as usize;
        let func_bytes     = r.bytes(func_body_size)?;
        let type_body_size = r.u32()? as usize;
        let type_bytes     = if type_body_size > 0 { Some(r.bytes(type_body_size)?) } else { None };

        let bodies  = decode_func_section(func_bytes, pool)?;
        let classes = match type_bytes {
            Some(tb) => decode_type_section(tb, pool)?,
            None     => vec![],
        };

        let sigs_slice: Vec<SigEntry> = (first_sig..first_sig + func_count)
            .map(|i| all_sigs.get(i).cloned()
                .unwrap_or_else(|| (format!("func#{}", i), 0, "void".to_owned(), ExecMode::Interp)))
            .collect();

        let module = assemble_module(&ns, pool, classes, sigs_slice, bodies)?;
        modules.push(module);
    }
    Ok(modules)
}

// ── Module assembly ───────────────────────────────────────────────────────────

fn assemble_module(
    ns:      &str,
    pool:    &[String],
    classes: Vec<ClassDesc>,
    sigs:    Vec<SigEntry>,
    bodies:  Vec<FuncBody>,
) -> Result<Module> {
    if sigs.len() != bodies.len() {
        bail!(
            "SIGS ({}) and FUNC ({}) count mismatch in module '{}'",
            sigs.len(), bodies.len(), ns
        );
    }

    let mut used_str_indices: Vec<bool> = vec![false; pool.len()];

    let functions: Vec<Function> = sigs.into_iter().zip(bodies).map(
        |((name, param_count, ret_type, exec_mode), (blocks, exception_table, line_table))| {
            // Mark strings used by ConstStr instructions
            for block in &blocks {
                for instr in &block.instructions {
                    if let Instruction::ConstStr { idx, .. } = instr {
                        if (*idx as usize) < used_str_indices.len() {
                            used_str_indices[*idx as usize] = true;
                        }
                    }
                }
            }
            Function {
                name,
                param_count,
                ret_type,
                exec_mode,
                blocks,
                exception_table,
                is_static: false,
                max_reg: 0,
                line_table,
                local_vars: vec![],
                type_params: vec![],
                type_param_constraints: vec![],
                block_index: std::collections::HashMap::new(),
            }
        }
    ).collect();

    // Rebuild string pool from only the strings referenced by ConstStr instructions.
    // Remap ConstStr indices from global pool to local pool indices.
    let mut global_to_local: HashMap<u32, u32> = HashMap::new();
    let mut string_pool: Vec<String> = Vec::new();

    for f in &functions {
        for block in &f.blocks {
            for instr in &block.instructions {
                if let Instruction::ConstStr { idx, .. } = instr {
                    if !global_to_local.contains_key(idx) {
                        let local = string_pool.len() as u32;
                        global_to_local.insert(*idx, local);
                        let s = pool.get(*idx as usize).cloned().unwrap_or_default();
                        string_pool.push(s);
                    }
                }
            }
        }
    }

    // Apply the remap (mutate ConstStr indices)
    let functions = remap_const_str(functions, &global_to_local);

    Ok(Module {
        name: ns.to_owned(),
        string_pool,
        classes,
        functions,
    })
}

fn remap_const_str(mut functions: Vec<Function>, map: &HashMap<u32, u32>) -> Vec<Function> {
    for f in &mut functions {
        for block in &mut f.blocks {
            for instr in &mut block.instructions {
                if let Instruction::ConstStr { idx, .. } = instr {
                    if let Some(&local) = map.get(idx) {
                        *idx = local;
                    }
                }
            }
        }
    }
    functions
}

// ── Block decoding ────────────────────────────────────────────────────────────

fn decode_block(data: &[u8], pool: &[String]) -> Result<(Vec<Instruction>, Terminator)> {
    let mut r      = Reader::new(data);
    let mut instrs = Vec::new();

    while r.remaining() > 0 {
        let op  = r.u8()?;
        let typ = r.u8()?;
        let dst = r.u16()? as u32;

        match op {
            OP_RET => return Ok((instrs, Terminator::Ret { reg: None })),
            OP_RET_VAL => return Ok((instrs, Terminator::Ret { reg: Some(dst) })),
            OP_BR => {
                let label = block_label(r.u16()? as usize);
                return Ok((instrs, Terminator::Br { label }));
            }
            OP_BR_COND => {
                let true_label  = block_label(r.u16()? as usize);
                let false_label = block_label(r.u16()? as usize);
                return Ok((instrs, Terminator::BrCond { cond: dst, true_label, false_label }));
            }
            OP_THROW => return Ok((instrs, Terminator::Throw { reg: dst })),
            _ => instrs.push(decode_instr(op, typ, dst, &mut r, pool)?),
        }
    }
    // Block must end with a terminator; add a fallback void return
    Ok((instrs, Terminator::Ret { reg: None }))
}

fn decode_instr(
    op:   u8,
    typ:  u8,
    dst:  u32,
    r:    &mut Reader,
    pool: &[String],
) -> Result<Instruction> {
    Ok(match op {
        OP_CONST_STR  => Instruction::ConstStr  { dst, idx: r.u32()? },
        OP_CONST_I if typ == TT_I64
                      => Instruction::ConstI64  { dst, val: r.i64()? },
        OP_CONST_I    => Instruction::ConstI32  { dst, val: r.i32()? },
        OP_CONST_F    => Instruction::ConstF64  { dst, val: r.f64()? },
        OP_CONST_BOOL => Instruction::ConstBool { dst, val: r.u8()? != 0 },
        OP_CONST_CHAR => {
            let code_point = r.i32()? as u32;
            let val = char::from_u32(code_point).unwrap_or('\0');
            Instruction::ConstChar { dst, val }
        }
        OP_CONST_NULL => Instruction::ConstNull { dst },
        OP_COPY       => Instruction::Copy      { dst, src: r.u16()? as u32 },
        OP_STORE => {
            let var = pool_str(pool, r.u32()? as usize)?.to_owned();
            let src = r.u16()? as u32;
            Instruction::Store { var, src }
        }
        OP_LOAD => Instruction::Load { dst, var: pool_str(pool, r.u32()? as usize)?.to_owned() },

        OP_ADD    => bin(dst, r, Instruction::Add)?,
        OP_SUB    => bin(dst, r, Instruction::Sub)?,
        OP_MUL    => bin(dst, r, Instruction::Mul)?,
        OP_DIV    => bin(dst, r, Instruction::Div)?,
        OP_REM    => bin(dst, r, Instruction::Rem)?,
        OP_AND    => bin(dst, r, Instruction::And)?,
        OP_OR     => bin(dst, r, Instruction::Or)?,
        OP_BIT_AND   => bin(dst, r, Instruction::BitAnd)?,
        OP_BIT_OR    => bin(dst, r, Instruction::BitOr)?,
        OP_BIT_XOR   => bin(dst, r, Instruction::BitXor)?,
        OP_SHL    => bin(dst, r, Instruction::Shl)?,
        OP_SHR    => bin(dst, r, Instruction::Shr)?,
        OP_STR_CONCAT => bin(dst, r, Instruction::StrConcat)?,
        OP_EQ     => bin(dst, r, Instruction::Eq)?,
        OP_NE     => bin(dst, r, Instruction::Ne)?,
        OP_LT     => bin(dst, r, Instruction::Lt)?,
        OP_LE     => bin(dst, r, Instruction::Le)?,
        OP_GT     => bin(dst, r, Instruction::Gt)?,
        OP_GE     => bin(dst, r, Instruction::Ge)?,

        OP_NEG     => Instruction::Neg     { dst, src: r.u16()? as u32 },
        OP_NOT     => Instruction::Not     { dst, src: r.u16()? as u32 },
        OP_BIT_NOT => Instruction::BitNot  { dst, src: r.u16()? as u32 },
        OP_TO_STR  => Instruction::ToStr   { dst, src: r.u16()? as u32 },
        OP_ARRAY_LEN => Instruction::ArrayLen { dst, arr: r.u16()? as u32 },

        OP_CALL => {
            let func = pool_str(pool, r.u32()? as usize)?.to_owned();
            let args = read_args(r)?;
            Instruction::Call { dst, func, args }
        }
        OP_BUILTIN => {
            let name = pool_str(pool, r.u32()? as usize)?.to_owned();
            let args = read_args(r)?;
            Instruction::Builtin { dst, name, args }
        }
        OP_VCALL => {
            let method = pool_str(pool, r.u32()? as usize)?.to_owned();
            let obj    = r.u16()? as u32;
            let args   = read_args(r)?;
            Instruction::VCall { dst, obj, method, args }
        }
        OP_FIELD_GET => {
            let obj        = r.u16()? as u32;
            let field_name = pool_str(pool, r.u32()? as usize)?.to_owned();
            Instruction::FieldGet { dst, obj, field_name }
        }
        OP_FIELD_SET => {
            let obj        = r.u16()? as u32;
            let field_name = pool_str(pool, r.u32()? as usize)?.to_owned();
            let val        = r.u16()? as u32;
            Instruction::FieldSet { obj, field_name, val }
        }
        OP_STATIC_GET => {
            Instruction::StaticGet { dst, field: pool_str(pool, r.u32()? as usize)?.to_owned() }
        }
        OP_STATIC_SET => {
            let field = pool_str(pool, r.u32()? as usize)?.to_owned();
            let val   = r.u16()? as u32;
            Instruction::StaticSet { field, val }
        }
        OP_OBJ_NEW => {
            let class_name = pool_str(pool, r.u32()? as usize)?.to_owned();
            let ctor_name  = pool_str(pool, r.u32()? as usize)?.to_owned();
            let args       = read_args(r)?;
            Instruction::ObjNew { dst, class_name, ctor_name, args }
        }
        OP_IS_INSTANCE => {
            let obj        = r.u16()? as u32;
            let class_name = pool_str(pool, r.u32()? as usize)?.to_owned();
            Instruction::IsInstance { dst, obj, class_name }
        }
        OP_AS_CAST => {
            let obj        = r.u16()? as u32;
            let class_name = pool_str(pool, r.u32()? as usize)?.to_owned();
            Instruction::AsCast { dst, obj, class_name }
        }
        OP_ARRAY_NEW     => Instruction::ArrayNew { dst, size: r.u16()? as u32 },
        OP_ARRAY_NEW_LIT => { let elems = read_args(r)?; Instruction::ArrayNewLit { dst, elems } }
        OP_ARRAY_GET     => Instruction::ArrayGet { dst, arr: r.u16()? as u32, idx: r.u16()? as u32 },
        OP_ARRAY_SET     => {
            let arr = r.u16()? as u32;
            let idx = r.u16()? as u32;
            let val = r.u16()? as u32;
            Instruction::ArraySet { arr, idx, val }
        }
        other => bail!("unknown opcode 0x{:02X}", other),
    })
}

// ── Helpers ───────────────────────────────────────────────────────────────────

fn block_label(idx: usize) -> String {
    if idx == 0 { "entry".to_owned() } else { format!("block_{}", idx) }
}

fn pool_str(pool: &[String], idx: usize) -> Result<&str> {
    pool.get(idx)
        .map(|s| s.as_str())
        .ok_or_else(|| anyhow::anyhow!("pool index {} out of bounds (pool size {})", idx, pool.len()))
}

fn type_tag_to_str(tag: u8) -> &'static str {
    match tag {
        0x00 => "void",
        0x01 => "bool",
        0x02 => "i8",
        0x03 => "i16",
        0x04 => "i32",
        0x05 => "i64",
        0x06 => "u8",
        0x07 => "u16",
        0x08 => "u32",
        0x09 => "u64",
        0x0A => "f32",
        0x0B => "f64",
        0x0C => "char",
        0x0D => "str",
        _    => "object",
    }
}

fn exec_mode_from_byte(b: u8) -> ExecMode {
    match b {
        1 => ExecMode::Jit,
        2 => ExecMode::Aot,
        _ => ExecMode::Interp,
    }
}

fn bin<F>(dst: u32, r: &mut Reader, f: F) -> Result<Instruction>
where F: Fn(u32, u32, u32) -> Instruction,
{
    let a = r.u16()? as u32;
    let b = r.u16()? as u32;
    Ok(f(dst, a, b))
}

fn read_args(r: &mut Reader) -> Result<Vec<u32>> {
    let count = r.u8()? as usize;
    let mut args = Vec::with_capacity(count);
    for _ in 0..count {
        args.push(r.u16()? as u32);
    }
    Ok(args)
}

// ── Byte reader ───────────────────────────────────────────────────────────────

struct Reader<'a> {
    data: &'a [u8],
    pos:  usize,
}

impl<'a> Reader<'a> {
    fn new(data: &'a [u8]) -> Self { Self { data, pos: 0 } }

    fn remaining(&self) -> usize { self.data.len() - self.pos }

    fn u8(&mut self) -> Result<u8> {
        if self.pos >= self.data.len() { bail!("unexpected end of data (u8)"); }
        let v = self.data[self.pos];
        self.pos += 1;
        Ok(v)
    }

    fn u16(&mut self) -> Result<u16> {
        if self.pos + 2 > self.data.len() { bail!("unexpected end of data (u16)"); }
        let v = u16::from_le_bytes([self.data[self.pos], self.data[self.pos+1]]);
        self.pos += 2;
        Ok(v)
    }

    fn u32(&mut self) -> Result<u32> {
        if self.pos + 4 > self.data.len() { bail!("unexpected end of data (u32)"); }
        let v = u32::from_le_bytes(self.data[self.pos..self.pos+4].try_into().unwrap());
        self.pos += 4;
        Ok(v)
    }

    fn i32(&mut self) -> Result<i32> { Ok(self.u32()? as i32) }

    fn i64(&mut self) -> Result<i64> {
        if self.pos + 8 > self.data.len() { bail!("unexpected end of data (i64)"); }
        let v = i64::from_le_bytes(self.data[self.pos..self.pos+8].try_into().unwrap());
        self.pos += 8;
        Ok(v)
    }

    fn f64(&mut self) -> Result<f64> {
        if self.pos + 8 > self.data.len() { bail!("unexpected end of data (f64)"); }
        let bits = u64::from_le_bytes(self.data[self.pos..self.pos+8].try_into().unwrap());
        self.pos += 8;
        Ok(f64::from_bits(bits))
    }

    fn bytes(&mut self, n: usize) -> Result<&'a [u8]> {
        if self.pos + n > self.data.len() {
            bail!("unexpected end of data (bytes {})", n);
        }
        let slice = &self.data[self.pos..self.pos+n];
        self.pos += n;
        Ok(slice)
    }

    /// Read an inline-length-prefixed UTF-8 string (u16 length prefix).
    fn inline_str(&mut self) -> Result<String> {
        let len = self.u16()? as usize;
        if len == 0 { return Ok(String::new()); }
        let bytes = self.bytes(len)?;
        Ok(std::str::from_utf8(bytes)?.to_owned())
    }
}
