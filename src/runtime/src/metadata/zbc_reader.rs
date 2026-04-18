/// Binary zbc v0.3 and zpkg v0.1 reader for the Rust VM.
///
/// Mirrors the C# ZbcWriter/ZpkgWriter layout exactly.
///
/// zbc layout:
///   Header (16): magic[4] + major[2] + minor[2] + flags[2] + sec_count[2] + reserved[4]
///   Directory (sec_count × 12): tag[4] + offset[4] + size[4]
///   Sections at absolute offsets.
///
/// zpkg layout: same header/directory structure, different section tags.
use std::collections::HashMap;

use anyhow::{bail, Result};

use super::bytecode::{
    BasicBlock, ClassDesc, ExceptionEntry, FieldDesc, Function, Instruction, Module, Terminator,
};
use super::formats::{ZpkgDep, ZPKG_MAGIC, ZBC_MAGIC};
use super::types::ExecMode;

// ── Opcode constants (must match C# Opcodes.cs) ───────────────────────────────

const OP_CONST_I: u8     = 0x00;
const OP_CONST_F: u8     = 0x01;
const OP_CONST_BOOL: u8  = 0x02;
const OP_CONST_STR: u8   = 0x03;
const OP_CONST_NULL: u8  = 0x04;
const OP_COPY: u8        = 0x05;
const OP_CONST_CHAR: u8  = 0x08;
const OP_STORE: u8       = 0x06;
const OP_LOAD: u8        = 0x07;

const OP_ADD: u8         = 0x10;
const OP_SUB: u8         = 0x11;
const OP_MUL: u8         = 0x12;
const OP_DIV: u8         = 0x13;
const OP_REM: u8         = 0x14;
const OP_NEG: u8         = 0x15;
const OP_AND: u8         = 0x16;
const OP_OR: u8          = 0x17;
const OP_NOT: u8         = 0x18;
const OP_BIT_AND: u8     = 0x19;
const OP_BIT_OR: u8      = 0x1A;
const OP_BIT_XOR: u8     = 0x1B;
const OP_BIT_NOT: u8     = 0x1C;
const OP_SHL: u8         = 0x1D;
const OP_SHR: u8         = 0x1E;
const OP_TO_STR: u8      = 0x1F;

const OP_EQ: u8          = 0x30;
const OP_NE: u8          = 0x31;
const OP_LT: u8          = 0x32;
const OP_LE: u8          = 0x33;
const OP_GT: u8          = 0x34;
const OP_GE: u8          = 0x35;

const OP_BR: u8          = 0x40;
const OP_BR_COND: u8     = 0x41;
const OP_RET: u8         = 0x42;
const OP_RET_VAL: u8     = 0x43;
const OP_THROW: u8       = 0x44;

const OP_CALL: u8        = 0x50;
const OP_BUILTIN: u8     = 0x51;
const OP_VCALL: u8       = 0x52;

const OP_FIELD_GET: u8   = 0x60;
const OP_FIELD_SET: u8   = 0x61;
const OP_STATIC_GET: u8  = 0x62;
const OP_STATIC_SET: u8  = 0x63;

const OP_OBJ_NEW: u8     = 0x70;
const OP_IS_INSTANCE: u8 = 0x71;
const OP_AS_CAST: u8     = 0x72;

const OP_ARRAY_NEW: u8     = 0x80;
const OP_ARRAY_NEW_LIT: u8 = 0x81;
const OP_ARRAY_GET: u8     = 0x82;
const OP_ARRAY_SET: u8     = 0x83;
const OP_ARRAY_LEN: u8     = 0x84;
const OP_STR_CONCAT: u8    = 0x85;

// ── Type tag constants ────────────────────────────────────────────────────────

const TAG_I64: u8 = 0x05;

// ── Low-level reader helpers ──────────────────────────────────────────────────

struct Cursor<'a> {
    data: &'a [u8],
    pos: usize,
}

impl<'a> Cursor<'a> {
    fn new(data: &'a [u8]) -> Self { Cursor { data, pos: 0 } }

    fn remaining(&self) -> usize { self.data.len() - self.pos }

    fn read_u8(&mut self) -> Result<u8> {
        if self.pos >= self.data.len() { bail!("unexpected end of data (u8)") }
        let v = self.data[self.pos]; self.pos += 1; Ok(v)
    }
    fn read_u16(&mut self) -> Result<u16> {
        self.need(2)?;
        let v = u16::from_le_bytes([self.data[self.pos], self.data[self.pos+1]]);
        self.pos += 2; Ok(v)
    }
    fn read_u32(&mut self) -> Result<u32> {
        self.need(4)?;
        let v = u32::from_le_bytes(self.data[self.pos..self.pos+4].try_into().unwrap());
        self.pos += 4; Ok(v)
    }
    fn read_i32(&mut self) -> Result<i32> {
        self.need(4)?;
        let v = i32::from_le_bytes(self.data[self.pos..self.pos+4].try_into().unwrap());
        self.pos += 4; Ok(v)
    }
    fn read_i64(&mut self) -> Result<i64> {
        self.need(8)?;
        let v = i64::from_le_bytes(self.data[self.pos..self.pos+8].try_into().unwrap());
        self.pos += 8; Ok(v)
    }
    fn read_f64(&mut self) -> Result<f64> {
        self.need(8)?;
        let v = f64::from_le_bytes(self.data[self.pos..self.pos+8].try_into().unwrap());
        self.pos += 8; Ok(v)
    }
    fn read_bytes(&mut self, n: usize) -> Result<&'a [u8]> {
        self.need(n)?;
        let s = &self.data[self.pos..self.pos+n]; self.pos += n; Ok(s)
    }
    fn read_utf8_u16len(&mut self) -> Result<String> {
        let len = self.read_u16()? as usize;
        let b = self.read_bytes(len)?;
        Ok(std::str::from_utf8(b)?.to_owned())
    }
    fn need(&self, n: usize) -> Result<()> {
        if self.pos + n > self.data.len() { bail!("unexpected end of data") }
        Ok(())
    }
    fn pool_str<'p>(&self, pool: &'p [String], idx: u32) -> Result<&'p str> {
        pool.get(idx as usize)
            .map(|s| s.as_str())
            .ok_or_else(|| anyhow::anyhow!("string pool index {} out of range (pool size {})", idx, pool.len()))
    }
}

// ── Section directory ─────────────────────────────────────────────────────────

/// Public re-export for loader.rs (namespace fast-scan).
pub fn read_directory_pub(data: &[u8], sec_count: u16) -> Result<HashMap<[u8;4], (usize, usize)>> {
    read_directory(data, sec_count)
}

fn read_directory(data: &[u8], sec_count: u16) -> Result<HashMap<[u8;4], (usize, usize)>> {
    let mut dir = HashMap::new();
    if sec_count == 0 {
        // Legacy v0.2 sequential scan (no directory): header is 16 bytes,
        // each section: tag[4] + len[4] + data[len]
        let mut pos = 16usize;
        while pos + 8 <= data.len() {
            let tag: [u8;4] = data[pos..pos+4].try_into().unwrap();
            let len = u32::from_le_bytes(data[pos+4..pos+8].try_into().unwrap()) as usize;
            dir.insert(tag, (pos + 8, len));
            pos += 8 + len;
        }
    } else {
        // v0.3 directory: starts at byte 16
        let mut pos = 16usize;
        for _ in 0..sec_count {
            if pos + 12 > data.len() { break; }
            let tag: [u8;4] = data[pos..pos+4].try_into().unwrap();
            let offset = u32::from_le_bytes(data[pos+4..pos+8].try_into().unwrap()) as usize;
            let size   = u32::from_le_bytes(data[pos+8..pos+12].try_into().unwrap()) as usize;
            dir.insert(tag, (offset, size));
            pos += 12;
        }
    }
    Ok(dir)
}

fn get_section<'d>(data: &'d [u8], dir: &HashMap<[u8;4], (usize, usize)>, tag: &[u8;4]) -> Option<&'d [u8]> {
    dir.get(tag).and_then(|&(off, size)| data.get(off..off+size))
}

// ── String heap (STRS / BSTR) ─────────────────────────────────────────────────

fn read_strs(sec: &[u8]) -> Result<Vec<String>> {
    let mut c = Cursor::new(sec);
    let count = c.read_u32()? as usize;
    let mut offsets = Vec::with_capacity(count);
    for _ in 0..count {
        let off = c.read_u32()? as usize;
        let len = c.read_u32()? as usize;
        offsets.push((off, len));
    }
    let data_start = c.pos;
    let mut result = Vec::with_capacity(count);
    for (off, len) in offsets {
        let start = data_start + off;
        let end = start + len;
        if end > sec.len() { bail!("STRS section: string entry out of bounds") }
        result.push(std::str::from_utf8(&sec[start..end])?.to_owned());
    }
    Ok(result)
}

// ── NSPC section ─────────────────────────────────────────────────────────────

fn read_nspc(sec: &[u8]) -> Result<String> {
    if sec.len() < 2 { return Ok(String::new()); }
    let len = u16::from_le_bytes([sec[0], sec[1]]) as usize;
    if len == 0 || sec.len() < 2 + len { return Ok(String::new()); }
    Ok(std::str::from_utf8(&sec[2..2+len])?.to_owned())
}

// ── TYPE section ──────────────────────────────────────────────────────────────

fn read_type(sec: &[u8], pool: &[String]) -> Result<Vec<ClassDesc>> {
    let mut c = Cursor::new(sec);
    let count = c.read_u32()? as usize;
    let mut classes = Vec::with_capacity(count);
    for _ in 0..count {
        let name_idx = c.read_u32()?;
        let base_idx = c.read_u32()?;
        let fld_count = c.read_u16()? as usize;
        let name = c.pool_str(pool, name_idx)?.to_owned();
        let base_class = if base_idx == u32::MAX {
            None
        } else {
            Some(c.pool_str(pool, base_idx)?.to_owned())
        };
        let mut fields = Vec::with_capacity(fld_count);
        for _ in 0..fld_count {
            let fnam_idx = c.read_u32()?;
            let type_tag = c.read_u8()?;
            fields.push(FieldDesc {
                name: c.pool_str(pool, fnam_idx)?.to_owned(),
                type_tag: type_tag_to_str(type_tag).to_owned(),
            });
        }
        classes.push(ClassDesc { name, base_class, fields });
    }
    Ok(classes)
}

// ── SIGS section ─────────────────────────────────────────────────────────────

struct FuncSig {
    name: String,
    param_count: usize,
    ret_type: String,
    exec_mode: ExecMode,
    is_static: bool,
}

fn read_sigs(sec: &[u8], pool: &[String], has_is_static: bool) -> Result<Vec<FuncSig>> {
    let mut c = Cursor::new(sec);
    let count = c.read_u32()? as usize;
    let mut sigs = Vec::with_capacity(count);
    for _ in 0..count {
        let name_idx    = c.read_u32()?;
        let param_count = c.read_u16()? as usize;
        let ret_tag     = c.read_u8()?;
        let mode_byte   = c.read_u8()?;
        let is_static   = if has_is_static { c.read_u8()? != 0 } else { false };
        sigs.push(FuncSig {
            name: c.pool_str(pool, name_idx)?.to_owned(),
            param_count,
            ret_type: type_tag_to_str(ret_tag).to_owned(),
            exec_mode: exec_mode_from_byte(mode_byte),
            is_static,
        });
    }
    Ok(sigs)
}

// ── FUNC section ─────────────────────────────────────────────────────────────

struct FuncBody {
    blocks: Vec<BasicBlock>,
    exception_table: Vec<ExceptionEntry>,
    line_table: Vec<crate::metadata::bytecode::LineEntry>,
}

fn read_func(sec: &[u8], pool: &[String]) -> Result<Vec<FuncBody>> {
    let mut c = Cursor::new(sec);
    let func_count = c.read_u32()? as usize;
    let mut bodies = Vec::with_capacity(func_count);

    for _ in 0..func_count {
        let _reg_count  = c.read_u16()?;
        let block_count = c.read_u16()? as usize;
        let instr_len   = c.read_u32()? as usize;
        let exc_count   = c.read_u16()? as usize;
        let line_count  = c.read_u16()? as usize;

        let mut block_offsets = Vec::with_capacity(block_count);
        for _ in 0..block_count {
            block_offsets.push(c.read_u32()? as usize);
        }

        let mut raw_exc = Vec::with_capacity(exc_count);
        for _ in 0..exc_count {
            let try_start  = c.read_u16()?;
            let try_end    = c.read_u16()?;
            let catch_blk  = c.read_u16()?;
            let catch_type = c.read_u32()?;
            let catch_reg  = c.read_u16()?;
            raw_exc.push((try_start, try_end, catch_blk, catch_type, catch_reg));
        }

        let mut line_table = Vec::with_capacity(line_count);
        for _ in 0..line_count {
            let blk  = c.read_u16()? as u32;
            let ins  = c.read_u16()? as u32;
            let line = c.read_u32()?;
            let file_id = c.read_u32()?;
            let file = if file_id == u32::MAX { None } else {
                pool.get(file_id as usize).cloned()
            };
            line_table.push(crate::metadata::bytecode::LineEntry { block: blk, instr: ins, line, file });
        }

        let instr_bytes = c.read_bytes(instr_len)?;

        // Decode blocks
        let mut blocks = Vec::with_capacity(block_count);
        for bi in 0..block_count {
            let start = block_offsets[bi];
            let end   = if bi + 1 < block_count { block_offsets[bi + 1] } else { instr_len };
            let label = if bi == 0 { "entry".to_owned() } else { format!("block_{bi}") };
            let (instrs, term) = decode_block(&instr_bytes[start..end], pool)?;
            blocks.push(BasicBlock { label, instructions: instrs, terminator: term });
        }

        // Resolve exception table block indices to labels
        let exception_table = raw_exc.into_iter().map(|(ts, te, cb, ct, cr)| {
            let try_start  = block_label(ts as usize);
            let try_end    = if (te as usize) < blocks.len() {
                block_label(te as usize)
            } else {
                format!("block_{}", blocks.len())
            };
            let catch_label = block_label(cb as usize);
            let catch_type  = if ct == u32::MAX { None } else {
                pool.get(ct as usize).map(|s| s.clone())
            };
            ExceptionEntry { try_start, try_end, catch_label, catch_type, catch_reg: cr as u32 }
        }).collect();

        bodies.push(FuncBody { blocks, exception_table, line_table });
    }
    Ok(bodies)
}

fn block_label(idx: usize) -> String {
    if idx == 0 { "entry".to_owned() } else { format!("block_{idx}") }
}

// ── Block decoding ────────────────────────────────────────────────────────────

fn decode_block(data: &[u8], pool: &[String]) -> Result<(Vec<Instruction>, Terminator)> {
    let mut c = Cursor::new(data);
    let mut instrs = Vec::new();

    while c.remaining() > 0 {
        let op  = c.read_u8()?;
        let typ = c.read_u8()?;
        let dst = c.read_u16()? as u32;

        match op {
            OP_RET     => return Ok((instrs, Terminator::Ret { reg: None })),
            OP_RET_VAL => return Ok((instrs, Terminator::Ret { reg: Some(dst) })),
            OP_BR      => {
                let lbl = c.read_u16()? as usize;
                return Ok((instrs, Terminator::Br { label: block_label(lbl) }));
            }
            OP_BR_COND => {
                let t = c.read_u16()? as usize;
                let f = c.read_u16()? as usize;
                return Ok((instrs, Terminator::BrCond {
                    cond: dst,
                    true_label:  block_label(t),
                    false_label: block_label(f),
                }));
            }
            OP_THROW => return Ok((instrs, Terminator::Throw { reg: dst })),
            _ => instrs.push(decode_instr(op, typ, dst, &mut c, pool)?),
        }
    }
    Ok((instrs, Terminator::Ret { reg: None }))
}

fn decode_instr(op: u8, typ: u8, dst: u32, c: &mut Cursor, pool: &[String]) -> Result<Instruction> {
    let instr = match op {
        OP_CONST_STR  => Instruction::ConstStr { dst, idx: c.read_u32()? },
        OP_CONST_I if typ == TAG_I64
                      => Instruction::ConstI64 { dst, val: c.read_i64()? },
        OP_CONST_I    => Instruction::ConstI32 { dst, val: c.read_i32()? },
        OP_CONST_F    => Instruction::ConstF64 { dst, val: c.read_f64()? },
        OP_CONST_BOOL => Instruction::ConstBool { dst, val: c.read_u8()? != 0 },
        OP_CONST_CHAR => {
            let code_point = c.read_i32()? as u32;
            Instruction::ConstChar { dst, val: char::from_u32(code_point).unwrap_or('\0') }
        }
        OP_CONST_NULL => Instruction::ConstNull { dst },
        OP_COPY       => Instruction::Copy { dst, src: c.read_u16()? as u32 },

        OP_ADD     => { let (a,b) = read_ab(c)?; Instruction::Add { dst, a, b } }
        OP_SUB     => { let (a,b) = read_ab(c)?; Instruction::Sub { dst, a, b } }
        OP_MUL     => { let (a,b) = read_ab(c)?; Instruction::Mul { dst, a, b } }
        OP_DIV     => { let (a,b) = read_ab(c)?; Instruction::Div { dst, a, b } }
        OP_REM     => { let (a,b) = read_ab(c)?; Instruction::Rem { dst, a, b } }
        OP_AND     => { let (a,b) = read_ab(c)?; Instruction::And { dst, a, b } }
        OP_OR      => { let (a,b) = read_ab(c)?; Instruction::Or  { dst, a, b } }
        OP_BIT_AND => { let (a,b) = read_ab(c)?; Instruction::BitAnd { dst, a, b } }
        OP_BIT_OR  => { let (a,b) = read_ab(c)?; Instruction::BitOr  { dst, a, b } }
        OP_BIT_XOR => { let (a,b) = read_ab(c)?; Instruction::BitXor { dst, a, b } }
        OP_SHL     => { let (a,b) = read_ab(c)?; Instruction::Shl { dst, a, b } }
        OP_SHR     => { let (a,b) = read_ab(c)?; Instruction::Shr { dst, a, b } }
        OP_STR_CONCAT => { let (a,b) = read_ab(c)?; Instruction::StrConcat { dst, a, b } }
        OP_EQ      => { let (a,b) = read_ab(c)?; Instruction::Eq { dst, a, b } }
        OP_NE      => { let (a,b) = read_ab(c)?; Instruction::Ne { dst, a, b } }
        OP_LT      => { let (a,b) = read_ab(c)?; Instruction::Lt { dst, a, b } }
        OP_LE      => { let (a,b) = read_ab(c)?; Instruction::Le { dst, a, b } }
        OP_GT      => { let (a,b) = read_ab(c)?; Instruction::Gt { dst, a, b } }
        OP_GE      => { let (a,b) = read_ab(c)?; Instruction::Ge { dst, a, b } }

        OP_NEG     => Instruction::Neg    { dst, src: c.read_u16()? as u32 },
        OP_NOT     => Instruction::Not    { dst, src: c.read_u16()? as u32 },
        OP_BIT_NOT => Instruction::BitNot { dst, src: c.read_u16()? as u32 },
        OP_TO_STR  => Instruction::ToStr  { dst, src: c.read_u16()? as u32 },
        OP_ARRAY_LEN => Instruction::ArrayLen { dst, arr: c.read_u16()? as u32 },

        OP_CALL => {
            let func = pool_str_owned(pool, c.read_u32()?)?;
            let args = read_args(c)?;
            Instruction::Call { dst, func, args }
        }
        OP_BUILTIN => {
            let name = pool_str_owned(pool, c.read_u32()?)?;
            let args = read_args(c)?;
            Instruction::Builtin { dst, name, args }
        }
        OP_VCALL => {
            let method = pool_str_owned(pool, c.read_u32()?)?;
            let obj    = c.read_u16()? as u32;
            let args   = read_args(c)?;
            Instruction::VCall { dst, obj, method, args }
        }
        OP_FIELD_GET => {
            let obj        = c.read_u16()? as u32;
            let field_name = pool_str_owned(pool, c.read_u32()?)?;
            Instruction::FieldGet { dst, obj, field_name }
        }
        OP_FIELD_SET => {
            let obj        = c.read_u16()? as u32;
            let field_name = pool_str_owned(pool, c.read_u32()?)?;
            let val        = c.read_u16()? as u32;
            Instruction::FieldSet { obj, field_name, val }
        }
        OP_STATIC_GET => Instruction::StaticGet { dst, field: pool_str_owned(pool, c.read_u32()?)? },
        OP_STATIC_SET => {
            let field = pool_str_owned(pool, c.read_u32()?)?;
            let val   = c.read_u16()? as u32;
            Instruction::StaticSet { field, val }
        }
        OP_OBJ_NEW => {
            let class_name = pool_str_owned(pool, c.read_u32()?)?;
            let args       = read_args(c)?;
            Instruction::ObjNew { dst, class_name, args }
        }
        OP_IS_INSTANCE => {
            let obj        = c.read_u16()? as u32;
            let class_name = pool_str_owned(pool, c.read_u32()?)?;
            Instruction::IsInstance { dst, obj, class_name }
        }
        OP_AS_CAST => {
            let obj        = c.read_u16()? as u32;
            let class_name = pool_str_owned(pool, c.read_u32()?)?;
            Instruction::AsCast { dst, obj, class_name }
        }
        OP_ARRAY_NEW     => Instruction::ArrayNew { dst, size: c.read_u16()? as u32 },
        OP_ARRAY_NEW_LIT => { let elems = read_args(c)?; Instruction::ArrayNewLit { dst, elems } }
        OP_ARRAY_GET     => {
            let arr = c.read_u16()? as u32;
            let idx = c.read_u16()? as u32;
            Instruction::ArrayGet { dst, arr, idx }
        }
        OP_ARRAY_SET     => {
            let arr = c.read_u16()? as u32;
            let idx = c.read_u16()? as u32;
            let val = c.read_u16()? as u32;
            Instruction::ArraySet { arr, idx, val }
        }
        _ => bail!("unknown opcode 0x{op:02X}"),
    };
    Ok(instr)
}

fn read_ab(c: &mut Cursor) -> Result<(u32, u32)> {
    Ok((c.read_u16()? as u32, c.read_u16()? as u32))
}

fn read_args(c: &mut Cursor) -> Result<Vec<u32>> {
    let count = c.read_u8()? as usize;
    let mut args = Vec::with_capacity(count);
    for _ in 0..count { args.push(c.read_u16()? as u32); }
    Ok(args)
}

fn pool_str_owned(pool: &[String], idx: u32) -> Result<String> {
    pool.get(idx as usize)
        .map(|s| s.clone())
        .ok_or_else(|| anyhow::anyhow!("string pool index {} out of range", idx))
}

// ── String pool rebuild (ConstStr remap) ─────────────────────────────────────

/// Rebuilds the module-local string pool from the global pool + ConstStr references,
/// and remaps ConstStr.idx from global to local indices in-place.
fn rebuild_string_pool(global: &[String], funcs: &mut [Function]) -> Vec<String> {
    let mut seen: HashMap<u32, u32> = HashMap::new();
    let mut local: Vec<String> = Vec::new();

    for func in funcs.iter() {
        for block in &func.blocks {
            for instr in &block.instructions {
                if let Instruction::ConstStr { idx, .. } = instr {
                    if !seen.contains_key(idx) {
                        let s = global.get(*idx as usize).cloned().unwrap_or_default();
                        let local_idx = local.len() as u32;
                        seen.insert(*idx, local_idx);
                        local.push(s);
                    }
                }
            }
        }
    }

    // Remap in-place
    for func in funcs.iter_mut() {
        for block in &mut func.blocks {
            for instr in &mut block.instructions {
                if let Instruction::ConstStr { idx, .. } = instr {
                    if let Some(&new_idx) = seen.get(idx) {
                        *idx = new_idx;
                    }
                }
            }
        }
    }

    local
}

// ── zbc public API ────────────────────────────────────────────────────────────

/// Read a full-mode binary zbc file and reconstruct a Module.
pub fn read_zbc(data: &[u8]) -> Result<Module> {
    if data.len() < 16 { bail!("zbc file too short") }
    if &data[0..4] != ZBC_MAGIC { bail!("not a binary zbc (bad magic)") }

    let minor     = u16::from_le_bytes([data[6], data[7]]);
    let sec_count = u16::from_le_bytes([data[10], data[11]]);
    let has_is_static = minor >= 4;
    let dir = read_directory(data, sec_count)?;

    let namespace = get_section(data, &dir, b"NSPC")
        .map(|s| read_nspc(s))
        .transpose()?
        .unwrap_or_default();

    let pool_raw = get_section(data, &dir, b"STRS")
        .or_else(|| get_section(data, &dir, b"BSTR"))
        .map(|s| read_strs(s))
        .transpose()?
        .unwrap_or_default();

    let classes = get_section(data, &dir, b"TYPE")
        .map(|s| read_type(s, &pool_raw))
        .transpose()?
        .unwrap_or_default();

    let sigs = get_section(data, &dir, b"SIGS")
        .map(|s| read_sigs(s, &pool_raw, has_is_static))
        .transpose()?
        .unwrap_or_default();

    let func_bodies = get_section(data, &dir, b"FUNC")
        .map(|s| read_func(s, &pool_raw))
        .transpose()?
        .unwrap_or_default();

    // Assemble functions from SIGS + FUNC
    let mut functions: Vec<Function> = func_bodies.into_iter().enumerate().map(|(i, body)| {
        let sig = sigs.get(i);
        Function {
            name:            sig.map(|s| s.name.clone()).unwrap_or_else(|| format!("func#{i}")),
            param_count:     sig.map(|s| s.param_count).unwrap_or(0),
            ret_type:        sig.map(|s| s.ret_type.clone()).unwrap_or_else(|| "void".to_owned()),
            exec_mode:       sig.map(|s| s.exec_mode).unwrap_or(ExecMode::Interp),
            blocks:          body.blocks,
            exception_table: body.exception_table,
            is_static:       sig.map(|s| s.is_static).unwrap_or(false),
            max_reg:         0,
            line_table:      body.line_table,
        }
    }).collect();

    let name = if namespace.is_empty() { "unknown".to_owned() } else { namespace };
    let string_pool = rebuild_string_pool(&pool_raw, &mut functions);
    Ok(Module { name, string_pool, classes, functions, type_registry: std::collections::HashMap::new() })
}

// ── zpkg public API ───────────────────────────────────────────────────────────

pub struct ZpkgInfo {
    pub name:         String,
    pub version:      String,
    pub entry:        Option<String>,
    pub namespaces:   Vec<String>,
    pub dependencies: Vec<ZpkgDep>,
    pub is_packed:    bool,
    pub is_exe:       bool,
}

/// Read zpkg header metadata (fast path, no module decode).
pub fn read_zpkg_meta(data: &[u8]) -> Result<ZpkgInfo> {
    if data.len() < 16 { bail!("zpkg file too short") }
    if &data[0..4] != ZPKG_MAGIC { bail!("not a binary zpkg (bad magic)") }

    let flags     = u16::from_le_bytes([data[8], data[9]]);
    let sec_count = u16::from_le_bytes([data[10], data[11]]);
    let is_packed = flags & 0x01 != 0;
    let is_exe    = flags & 0x02 != 0;

    let dir = read_directory(data, sec_count)?;

    // META: name, version, entry (inline UTF-8, no pool dependency)
    let (name, version, entry) = get_section(data, &dir, b"META")
        .map(|s| read_meta_section(s))
        .transpose()?
        .unwrap_or_else(|| (String::new(), String::new(), None));

    // STRS pool for NSPC + DEPS
    let pool = get_section(data, &dir, b"STRS")
        .map(|s| read_strs(s))
        .transpose()?
        .unwrap_or_default();

    // NSPC: list of namespace indices → strings
    let namespaces = get_section(data, &dir, b"NSPC")
        .map(|s| read_nspc_list(s, &pool))
        .transpose()?
        .unwrap_or_default();

    // DEPS
    let dependencies = get_section(data, &dir, b"DEPS")
        .map(|s| read_deps_section(s, &pool))
        .transpose()?
        .unwrap_or_default();

    Ok(ZpkgInfo { name, version, entry, namespaces, dependencies, is_packed, is_exe })
}

/// Read all modules from a packed zpkg. Returns (Module, namespace) pairs.
pub fn read_zpkg_modules(data: &[u8]) -> Result<Vec<(Module, String)>> {
    if data.len() < 16 { bail!("zpkg file too short") }
    if &data[0..4] != ZPKG_MAGIC { bail!("not a binary zpkg (bad magic)") }

    let minor     = u16::from_le_bytes([data[6], data[7]]);
    let flags     = u16::from_le_bytes([data[8], data[9]]);
    let sec_count = u16::from_le_bytes([data[10], data[11]]);
    let is_packed = flags & 0x01 != 0;
    // zpkg v0.1+ always includes is_static in SIGS (no legacy format exists)
    let has_is_static = minor >= 1;

    let dir = read_directory(data, sec_count)?;

    let pool = get_section(data, &dir, b"STRS")
        .map(|s| read_strs(s))
        .transpose()?
        .unwrap_or_default();

    if is_packed {
        // SIGS: global function signatures
        let sigs = get_section(data, &dir, b"SIGS")
            .map(|s| read_sigs(s, &pool, has_is_static))
            .transpose()?
            .unwrap_or_default();

        // MODS: per-module FUNC+TYPE bodies
        let mods_sec = get_section(data, &dir, b"MODS")
            .ok_or_else(|| anyhow::anyhow!("packed zpkg missing MODS section"))?;
        read_mods_section(mods_sec, &pool, &sigs)
    } else {
        // Indexed mode: FILE section lists .zbc paths, not loadable directly
        bail!("indexed zpkg cannot be loaded directly by the VM; use packed mode")
    }
}

// ── zpkg section decoders ─────────────────────────────────────────────────────

fn read_meta_section(sec: &[u8]) -> Result<(String, String, Option<String>)> {
    let mut c = Cursor::new(sec);
    let name    = c.read_utf8_u16len()?;
    let version = c.read_utf8_u16len()?;
    let entry_s = c.read_utf8_u16len()?;
    let entry   = if entry_s.is_empty() { None } else { Some(entry_s) };
    Ok((name, version, entry))
}

fn read_nspc_list(sec: &[u8], pool: &[String]) -> Result<Vec<String>> {
    let mut c = Cursor::new(sec);
    let count = c.read_u32()? as usize;
    let mut ns = Vec::with_capacity(count);
    for _ in 0..count {
        let idx = c.read_u32()?;
        ns.push(pool_str_owned(pool, idx)?);
    }
    Ok(ns)
}

fn read_deps_section(sec: &[u8], pool: &[String]) -> Result<Vec<ZpkgDep>> {
    let mut c = Cursor::new(sec);
    let count = c.read_u32()? as usize;
    let mut deps = Vec::with_capacity(count);
    for _ in 0..count {
        let file_idx = c.read_u32()?;
        let ns_count = c.read_u16()? as usize;
        let mut namespaces = Vec::with_capacity(ns_count);
        for _ in 0..ns_count {
            namespaces.push(pool_str_owned(pool, c.read_u32()?)?);
        }
        deps.push(ZpkgDep {
            file: pool_str_owned(pool, file_idx)?,
            namespaces,
        });
    }
    Ok(deps)
}

fn read_mods_section(
    sec: &[u8],
    pool: &[String],
    global_sigs: &[FuncSig],
) -> Result<Vec<(Module, String)>> {
    let mut c = Cursor::new(sec);
    let mod_count = c.read_u32()? as usize;
    let mut result = Vec::with_capacity(mod_count);

    let mut sig_offset = 0usize;
    for _ in 0..mod_count {
        let ns_idx      = c.read_u32()?;
        let _src_idx    = c.read_u32()?;
        let _hash_idx   = c.read_u32()?;
        let func_count  = c.read_u16()? as usize;
        let first_sig   = c.read_u32()? as usize;
        let func_len    = c.read_u32()? as usize;
        let func_data   = c.read_bytes(func_len)?;
        let type_len    = c.read_u32()? as usize;
        let type_data   = c.read_bytes(type_len)?;

        let namespace = pool_str_owned(pool, ns_idx)?;
        let sigs_slice = &global_sigs[first_sig..first_sig + func_count.min(global_sigs.len() - first_sig.min(global_sigs.len()))];

        let func_bodies = read_func(func_data, pool)?;
        let classes = if type_len > 0 { read_type(type_data, pool)? } else { vec![] };

        let mut functions: Vec<Function> = func_bodies.into_iter().enumerate().map(|(i, body)| {
            let sig = sigs_slice.get(i);
            Function {
                name:            sig.map(|s| s.name.clone()).unwrap_or_else(|| format!("func#{i}")),
                param_count:     sig.map(|s| s.param_count).unwrap_or(0),
                ret_type:        sig.map(|s| s.ret_type.clone()).unwrap_or_else(|| "void".to_owned()),
                exec_mode:       sig.map(|s| s.exec_mode).unwrap_or(ExecMode::Interp),
                blocks:          body.blocks,
                exception_table: body.exception_table,
                is_static:       sig.map(|s| s.is_static).unwrap_or(false),
                max_reg:         0,
                line_table:      body.line_table,
            }
        }).collect();

        let name = if namespace.is_empty() { "unknown".to_owned() } else { namespace.clone() };
        let string_pool = rebuild_string_pool(pool, &mut functions);
        result.push((Module { name, string_pool, classes, functions, type_registry: std::collections::HashMap::new() }, namespace));

        sig_offset += func_count;
        let _ = sig_offset; // used for validation if needed
    }
    Ok(result)
}

// ── Zpkg namespace fast-scan ──────────────────────────────────────────────────

/// Read only the namespaces from a binary zpkg (fast path for dependency scanning).
pub fn read_zpkg_namespaces(data: &[u8]) -> Result<Vec<String>> {
    if data.len() < 16 { bail!("zpkg file too short") }
    if &data[0..4] != ZPKG_MAGIC { bail!("not a binary zpkg (bad magic)") }

    let sec_count = u16::from_le_bytes([data[10], data[11]]);
    let dir = read_directory(data, sec_count)?;

    let pool = get_section(data, &dir, b"STRS")
        .map(|s| read_strs(s))
        .transpose()?
        .unwrap_or_default();

    get_section(data, &dir, b"NSPC")
        .map(|s| read_nspc_list(s, &pool))
        .transpose()
        .map(|v| v.unwrap_or_default())
}

// ── Conversion helpers ────────────────────────────────────────────────────────

fn type_tag_to_str(tag: u8) -> &'static str {
    match tag {
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
        0x20 => "object",
        0x21 => "array",
        _    => "void",
    }
}

fn exec_mode_from_byte(b: u8) -> ExecMode {
    match b {
        1 => ExecMode::Jit,
        2 => ExecMode::Aot,
        _ => ExecMode::Interp,
    }
}
