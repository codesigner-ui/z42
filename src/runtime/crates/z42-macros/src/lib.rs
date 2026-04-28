//! Proc macro entry points for z42 native interop.
//!
//! C1 scaffold: macros are registered (so `cargo check` of the crate succeeds
//! and IDEs see them), but each invocation expands to a `compile_error!`
//! pointing at the spec that will provide a real implementation.
//!
//! Real expansion logic lands in spec C3.

use proc_macro::TokenStream;
use quote::quote;

const C3_DERIVE: &str =
    "#[derive(Z42Type)] is declared in z42-macros but not yet implemented (spec C3)";
const C3_METHODS: &str =
    "#[z42::methods] is declared in z42-macros but not yet implemented (spec C3)";
const C3_TRAIT_IMPL: &str =
    "#[z42::trait_impl] is declared in z42-macros but not yet implemented (spec C3)";
const C3_MODULE: &str =
    "z42::module! is declared in z42-macros but not yet implemented (spec C3)";

/// `#[derive(Z42Type)]` — generates a `Z42Type` impl + static descriptor.
#[proc_macro_derive(Z42Type, attributes(z42))]
pub fn derive_z42_type(_input: TokenStream) -> TokenStream {
    quote! { ::core::compile_error!(#C3_DERIVE); }.into()
}

/// `#[z42::methods]` — wraps each `impl` method with an `extern "C"` shim.
#[proc_macro_attribute]
pub fn methods(_attr: TokenStream, _item: TokenStream) -> TokenStream {
    quote! { ::core::compile_error!(#C3_METHODS); }.into()
}

/// `#[z42::trait_impl("trait_name")]` — registers a trait impl into the
/// descriptor's `trait_impls` table.
#[proc_macro_attribute]
pub fn trait_impl(_attr: TokenStream, _item: TokenStream) -> TokenStream {
    quote! { ::core::compile_error!(#C3_TRAIT_IMPL); }.into()
}

/// `z42::module! { name: "...", types: [..] }` — emits `#[ctor]` registration
/// + writes the `<module>.z42abi` manifest at build time.
#[proc_macro]
pub fn module(_input: TokenStream) -> TokenStream {
    quote! { ::core::compile_error!(#C3_MODULE); }.into()
}
