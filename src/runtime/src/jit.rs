use crate::bytecode::{Function, Module};
use anyhow::Result;

pub fn run(_module: &Module, _func: &Function) -> Result<()> {
    anyhow::bail!("JIT backend not yet implemented")
}
