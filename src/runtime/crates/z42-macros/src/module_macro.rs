//! `z42::module! { name: "...", types: [T1, T2, ...] }` — emits a
//! `<name>_register()` function that calls `z42_register_type` per type.

use proc_macro::TokenStream;
use proc_macro2::Span;
use quote::{format_ident, quote};
use syn::parse::{Parse, ParseStream};
use syn::{bracketed, punctuated::Punctuated, Ident, LitStr, Token};

use crate::util;

/// `name: "...", types: [T1, T2]` — order-insensitive but both required.
struct ModuleSpec {
    name: LitStr,
    types: Punctuated<Ident, Token![,]>,
    name_span: Span,
}

impl Parse for ModuleSpec {
    fn parse(input: ParseStream) -> syn::Result<Self> {
        let mut name: Option<LitStr> = None;
        let mut types: Option<Punctuated<Ident, Token![,]>> = None;
        let mut name_span = Span::call_site();

        while !input.is_empty() {
            let key: Ident = input.parse()?;
            input.parse::<Token![:]>()?;
            match key.to_string().as_str() {
                "name" => {
                    let v: LitStr = input.parse()?;
                    name_span = v.span();
                    name = Some(v);
                }
                "types" => {
                    let content;
                    bracketed!(content in input);
                    let parsed: Punctuated<Ident, Token![,]> =
                        Punctuated::parse_terminated(&content)?;
                    types = Some(parsed);
                }
                other => {
                    return Err(syn::Error::new(
                        key.span(),
                        format!("z42::module!: unknown field `{other}`"),
                    ));
                }
            }
            // Optional trailing comma between fields.
            if input.peek(Token![,]) {
                input.parse::<Token![,]>()?;
            }
        }

        let name = name.ok_or_else(|| {
            syn::Error::new(Span::call_site(), "z42::module!: missing `name: \"...\"`")
        })?;
        let types = types.unwrap_or_default();
        Ok(ModuleSpec { name, types, name_span })
    }
}

pub fn expand(input: TokenStream) -> TokenStream {
    let parsed = match syn::parse::<ModuleSpec>(input) {
        Ok(p) => p,
        Err(e) => return util::err_to_tokens(e),
    };

    let module_name = parsed.name.value();
    if let Err(e) = util::validate_module_name(&module_name, parsed.name_span) {
        return util::err_to_tokens(e);
    }

    let register_ident = format_ident!("{}_register", module_name);

    let calls = parsed.types.iter().map(|ty| {
        quote! {
            unsafe {
                ::z42_abi::z42_register_type(
                    <#ty as ::z42_rs::traits::Z42Type>::descriptor()
                );
            }
        }
    });

    let expanded = quote! {
        #[no_mangle]
        pub extern "C" fn #register_ident() {
            #(#calls)*
        }
    };

    expanded.into()
}
