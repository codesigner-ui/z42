/// Module merge logic: combine multiple IR modules into one for .zpkg loading.
///
/// The only complexity is the `string_pool` offset remap: each `ConstStr { dst, idx }`
/// instruction references an index into its module's string pool. When N pools are
/// concatenated, every index from module i must be shifted by the cumulative length of
/// pools 0..i-1.
use super::bytecode::{BasicBlock, Instruction, Module};
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
fn remap_functions(functions: &mut Vec<super::bytecode::Function>, offset: u32) {
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

#[cfg(test)]
#[path = "merge_tests.rs"]
mod tests;
