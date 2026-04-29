//! Per-VM native-type registry.
//!
//! `z42_register_type` builds a [`RegisteredType`] from the caller's
//! [`Z42TypeDescriptor_v1`], then hands it to [`crate::vm_context::VmContext`]
//! for storage. Each method's signature is parsed into [`SigType`]s and a
//! libffi `Cif` is prebuilt so per-call dispatch is just `cif.call`.

use std::collections::HashMap;
use std::ffi::CStr;
use std::os::raw::c_void;

use anyhow::{anyhow, Result};
use libffi::middle::Cif;
use z42_abi::{
    Z42MethodDesc, Z42TypeDescriptor_v1, Z42_ABI_VERSION,
};

use super::dispatch::{self, SigType};

/// One registered method on a native type.
pub struct MethodEntry {
    pub name: String,
    pub signature: String,
    pub fn_ptr: *mut c_void,
    pub params: Vec<SigType>,
    pub return_type: SigType,
    /// Prebuilt libffi cif. Cif is `!Send` / `!Sync` because it owns
    /// libffi-internal pointers; that matches `RegisteredType` (kept on the
    /// single-threaded VmContext).
    pub cif: Cif,
}

impl std::fmt::Debug for MethodEntry {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("MethodEntry")
            .field("name", &self.name)
            .field("signature", &self.signature)
            .field("fn_ptr", &self.fn_ptr)
            .field("params", &self.params)
            .field("return_type", &self.return_type)
            // skip Cif (it has Debug but verbose libffi internals)
            .finish_non_exhaustive()
    }
}

/// One registered native type.
#[derive(Debug)]
pub struct RegisteredType {
    module: String,
    type_name: String,
    instance_size: usize,
    instance_align: usize,
    /// Raw pointer to caller's static descriptor; kept for diagnostic /
    /// future use only (we never re-read fields after registration).
    descriptor_ptr: *const Z42TypeDescriptor_v1,
    methods: HashMap<String, MethodEntry>,
}

impl RegisteredType {
    pub fn module(&self) -> &str { &self.module }
    pub fn type_name(&self) -> &str { &self.type_name }
    pub fn instance_size(&self) -> usize { self.instance_size }
    pub fn instance_align(&self) -> usize { self.instance_align }
    pub fn method(&self, name: &str) -> Option<&MethodEntry> { self.methods.get(name) }
    pub fn descriptor_ptr(&self) -> *const Z42TypeDescriptor_v1 { self.descriptor_ptr }
    pub fn method_count(&self) -> usize { self.methods.len() }

    /// Build a [`RegisteredType`] from a caller-supplied descriptor.
    ///
    /// # Safety
    /// Caller guarantees that `desc` is a valid pointer to a
    /// `Z42TypeDescriptor_v1` whose strings live for the duration of the
    /// VM (typically `static` storage in the native library).
    pub unsafe fn from_descriptor(desc: *const Z42TypeDescriptor_v1) -> Result<Self> {
        if desc.is_null() {
            return Err(anyhow!("Z0905: null descriptor"));
        }
        let d = unsafe { &*desc };
        if d.abi_version != Z42_ABI_VERSION {
            return Err(anyhow!(
                "Z0906: ABI version mismatch: expected {}, got {}",
                Z42_ABI_VERSION,
                d.abi_version
            ));
        }
        if d.module_name.is_null() {
            return Err(anyhow!("Z0905: descriptor.module_name is null"));
        }
        if d.type_name.is_null() {
            return Err(anyhow!("Z0905: descriptor.type_name is null"));
        }
        if d.method_count > 0 && d.methods.is_null() {
            return Err(anyhow!(
                "Z0905: method_count={} but methods array is null",
                d.method_count
            ));
        }

        let module = unsafe { CStr::from_ptr(d.module_name) }
            .to_str()
            .map_err(|e| anyhow!("Z0905: descriptor.module_name not UTF-8: {e}"))?
            .to_string();
        let type_name = unsafe { CStr::from_ptr(d.type_name) }
            .to_str()
            .map_err(|e| anyhow!("Z0905: descriptor.type_name not UTF-8: {e}"))?
            .to_string();

        let mut methods = HashMap::new();
        if d.method_count > 0 {
            let slice: &[Z42MethodDesc] =
                unsafe { std::slice::from_raw_parts(d.methods, d.method_count) };
            for m in slice {
                let entry = unsafe { build_method(m) }?;
                if methods.insert(entry.name.clone(), entry).is_some() {
                    return Err(anyhow!(
                        "Z0905: duplicate method name in descriptor for {module}::{type_name}"
                    ));
                }
            }
        }

        Ok(RegisteredType {
            module,
            type_name,
            instance_size: d.instance_size,
            instance_align: d.instance_align,
            descriptor_ptr: desc,
            methods,
        })
    }
}

/// Convert one C method descriptor into a populated [`MethodEntry`].
unsafe fn build_method(m: &Z42MethodDesc) -> Result<MethodEntry> {
    if m.name.is_null() {
        return Err(anyhow!("Z0905: method.name is null"));
    }
    if m.signature.is_null() {
        return Err(anyhow!("Z0905: method.signature is null"));
    }
    if m.fn_ptr.is_null() {
        return Err(anyhow!("Z0905: method.fn_ptr is null"));
    }
    let name = unsafe { CStr::from_ptr(m.name) }
        .to_str()
        .map_err(|e| anyhow!("Z0905: method.name not UTF-8: {e}"))?
        .to_string();
    let signature = unsafe { CStr::from_ptr(m.signature) }
        .to_str()
        .map_err(|e| anyhow!("Z0905: method.signature not UTF-8: {e}"))?
        .to_string();
    let (params, return_type) = dispatch::parse_signature(&signature)?;
    let cif = dispatch::build_cif(&params, &return_type);
    Ok(MethodEntry {
        name,
        signature,
        fn_ptr: m.fn_ptr,
        params,
        return_type,
        cif,
    })
}
