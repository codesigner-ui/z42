use z42_macros::module;

struct Counter;

module! {
    name: "z42.bad-name",
    types: [Counter],
}

fn main() {}
