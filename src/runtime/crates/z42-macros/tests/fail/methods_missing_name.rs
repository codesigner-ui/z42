use z42_macros::methods;

#[derive(Default)]
struct Counter { value: i64 }

#[methods(module = "demo")]
impl Counter {
    pub fn inc(&mut self) -> i64 { self.value += 1; self.value }
}

fn main() {}
