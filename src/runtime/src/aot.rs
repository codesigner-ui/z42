use crate::metadata::{Function, Module};
use anyhow::Result;

pub fn run(_module: &Module, _func: &Function) -> Result<()> {
    anyhow::bail!("AOT backend not yet implemented")
}
