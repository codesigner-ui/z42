/// Module merge logic: combine multiple IR modules into one for .zmod / .zlib loading.
///
/// The only complexity is the `string_pool` offset remap: each `ConstStr { dst, idx }`
/// instruction references an index into its module's string pool. When N pools are
/// concatenated, every index from module i must be shifted by the cumulative length of
/// pools 0..i-1.
use crate::bytecode::{BasicBlock, Instruction, Module};
use anyhow::Result;

/// Merge an ordered sequence of IR modules into a single flat module.
///
/// Merge rules:
/// - `string_pool`: concatenated in order; `ConstStr.idx` remapped accordingly.
/// - `classes`: concatenated (Phase 1 trusts the compiler for no duplicates).
/// - `functions`: concatenated (qualified names keep them distinct across namespaces).
/// - `name`: taken from the first module.
pub fn merge_modules(modules: Vec<Module>) -> Result<Module> {
    if modules.is_empty() {
        anyhow::bail!("merge_modules: no modules provided");
    }
    if modules.len() == 1 {
        // Fast path: nothing to merge.
        let mut m = modules.into_iter().next().unwrap();
        // Canonicalise: ensure no stale indices (no-op remap with offset 0)
        remap_functions(&mut m.functions, 0);
        return Ok(m);
    }

    let name = modules[0].name.clone();
    let mut string_pool: Vec<String> = Vec::new();
    let mut classes = Vec::new();
    let mut functions = Vec::new();

    for mut module in modules {
        let offset = string_pool.len() as u32;
        string_pool.extend(module.string_pool);
        classes.extend(module.classes);
        remap_functions(&mut module.functions, offset);
        functions.extend(module.functions);
    }

    Ok(Module { name, string_pool, classes, functions })
}

/// Shift every `ConstStr.idx` inside `functions` by `offset`.
/// All other instructions are unchanged.
fn remap_functions(functions: &mut Vec<crate::bytecode::Function>, offset: u32) {
    if offset == 0 {
        return; // nothing to do
    }
    for func in functions.iter_mut() {
        for block in func.blocks.iter_mut() {
            remap_block(block, offset);
        }
    }
}

fn remap_block(block: &mut BasicBlock, offset: u32) {
    for instr in block.instructions.iter_mut() {
        if let Instruction::ConstStr { idx, .. } = instr {
            *idx += offset;
        }
    }
}

// ── Tests ──────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::bytecode::{BasicBlock, Function, Instruction, Module, Terminator};
    use crate::types::ExecMode;

    fn make_module(name: &str, strings: &[&str], const_str_idx: u32) -> Module {
        let instr = Instruction::ConstStr { dst: 0, idx: const_str_idx };
        let block = BasicBlock {
            label: "entry".to_string(),
            instructions: vec![instr],
            terminator: Terminator::Ret { reg: None },
        };
        let func = Function {
            name: format!("{}.Main", name),
            param_count: 0,
            ret_type: "void".to_string(),
            exec_mode: ExecMode::Interp,
            blocks: vec![block],
            exception_table: vec![],
        };
        Module {
            name: name.to_string(),
            string_pool: strings.iter().map(|s| s.to_string()).collect(),
            classes: vec![],
            functions: vec![func],
        }
    }

    #[test]
    fn merge_single_module_is_identity() {
        let m = make_module("A", &["hello"], 0);
        let merged = merge_modules(vec![m]).unwrap();
        assert_eq!(merged.string_pool, vec!["hello"]);
        // idx unchanged (offset 0)
        assert!(matches!(
            merged.functions[0].blocks[0].instructions[0],
            Instruction::ConstStr { idx: 0, .. }
        ));
    }

    #[test]
    fn merge_two_modules_concatenates_pools_and_remaps() {
        let m0 = make_module("A", &["hello"], 0); // pool=[hello], idx=0
        let m1 = make_module("B", &["world"], 0); // pool=[world], idx=0 → should become 1

        let merged = merge_modules(vec![m0, m1]).unwrap();

        assert_eq!(merged.string_pool, vec!["hello", "world"]);
        assert_eq!(merged.functions.len(), 2);

        // A.Main: idx still 0 (first module, no shift)
        assert!(matches!(
            merged.functions[0].blocks[0].instructions[0],
            Instruction::ConstStr { idx: 0, .. }
        ));
        // B.Main: idx was 0, pool offset = 1 → now 1
        assert!(matches!(
            merged.functions[1].blocks[0].instructions[0],
            Instruction::ConstStr { idx: 1, .. }
        ));
    }

    #[test]
    fn merge_three_modules_offsets_accumulate() {
        let m0 = make_module("A", &["a0", "a1"], 1); // idx=1 → stays 1
        let m1 = make_module("B", &["b0"],       0); // idx=0 → +2 → 2
        let m2 = make_module("C", &["c0", "c1"], 0); // idx=0 → +3 → 3

        let merged = merge_modules(vec![m0, m1, m2]).unwrap();

        assert_eq!(merged.string_pool, vec!["a0", "a1", "b0", "c0", "c1"]);

        let idx = |fi: usize| match merged.functions[fi].blocks[0].instructions[0] {
            Instruction::ConstStr { idx, .. } => idx,
            _ => panic!("expected ConstStr"),
        };
        assert_eq!(idx(0), 1); // A: no shift
        assert_eq!(idx(1), 2); // B: 0 + 2
        assert_eq!(idx(2), 3); // C: 0 + 3
    }

    #[test]
    fn merge_empty_returns_error() {
        assert!(merge_modules(vec![]).is_err());
    }
}
