//! Shared helpers used by every macro in this crate.

use proc_macro2::{Span, TokenStream};
use quote::quote;
use syn::Ident;

/// Produce a Rust `c"..."` literal token stream for the given UTF-8 string.
/// Panics if `s` contains an interior NUL — callers should pre-validate.
pub(crate) fn c_string_literal(s: &str) -> TokenStream {
    if s.bytes().any(|b| b == 0) {
        panic!("z42-macros: c_string_literal called with interior NUL: {s:?}");
    }
    let lit = syn::LitCStr::new(
        std::ffi::CString::new(s).expect("validated above").as_ref(),
        Span::call_site(),
    );
    quote! { #lit.as_ptr() }
}

/// `__<TypeName>_DESC`-style hidden ident — collision-resistant private
/// names for macro-emitted statics.
pub(crate) fn private_ident(prefix: &str, ty: &Ident, suffix: &str) -> Ident {
    Ident::new(&format!("__{prefix}{ty}{suffix}"), ty.span())
}

/// Validate that a module name is a legal C identifier.
pub(crate) fn validate_module_name(name: &str, span: Span) -> Result<(), syn::Error> {
    if name.is_empty() {
        return Err(syn::Error::new(span, "module name must be non-empty"));
    }
    let mut chars = name.chars();
    let first = chars.next().unwrap();
    if !(first.is_ascii_alphabetic() || first == '_') {
        return Err(syn::Error::new(
            span,
            format!("module name must start with [A-Za-z_]: got {name:?}"),
        ));
    }
    for c in chars {
        if !(c.is_ascii_alphanumeric() || c == '_') {
            return Err(syn::Error::new(
                span,
                format!(
                    "module name {name:?} contains illegal character {c:?}; only [A-Za-z0-9_] allowed"
                ),
            ));
        }
    }
    Ok(())
}

/// Convenience: turn a `syn::Error` into a `compile_error!` token stream.
pub(crate) fn err_to_tokens(err: syn::Error) -> proc_macro::TokenStream {
    err.to_compile_error().into()
}
