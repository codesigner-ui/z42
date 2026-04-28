//! Skeleton compile-tests: a user can hand-implement [`Z42Type`] and
//! [`Z42Traceable`] without the derive macro. Real behavior arrives in C2/C3.

use core::ffi::c_void;
use z42_rs::prelude::*;

struct Dummy {
    inner: u32,
}

impl Z42Type for Dummy {
    const MODULE: &'static str = "test";
    const NAME: &'static str = "Dummy";

    fn descriptor() -> *const Descriptor {
        // Real descriptor wiring lands in C3 (derive macro). For C1 we only
        // assert that the trait can be implemented; the test never calls into
        // the descriptor pointer.
        core::ptr::null()
    }
}

impl Z42Traceable for Dummy {
    fn trace(&self, _visitor: &mut dyn Visitor) {
        // Dummy holds only a primitive; no refs to walk.
    }
}

struct CountingVisitor {
    count: usize,
}

impl Visitor for CountingVisitor {
    fn visit_ref(&mut self, _ptr: *const c_void) {
        self.count += 1;
    }
}

#[test]
fn z42type_can_be_implemented_manually() {
    assert_eq!(Dummy::MODULE, "test");
    assert_eq!(Dummy::NAME, "Dummy");
    assert!(Dummy::descriptor().is_null(),
        "C1 skeleton returns null descriptor; C3 derive macro will provide a real one");
}

#[test]
fn z42traceable_compiles_and_walks() {
    let d = Dummy { inner: 7 };
    let mut v = CountingVisitor { count: 0 };
    d.trace(&mut v);
    assert_eq!(v.count, 0, "Dummy holds no traceable refs");
    assert_eq!(d.inner, 7);
}

#[test]
fn re_exports_are_reachable() {
    // Ensures the prelude / re-export surface stays stable.
    let _: Option<Z42Args> = None;
    let _: Option<Z42Value> = None;
    let _: Option<Z42TypeRef> = None;
    let _: Option<Z42Error> = None;
}
