//! GC cycle-collection benchmarks — add-mark-sweep-collector P4 (2026-05-21).
//!
//! Three workloads exercise distinct shapes of the cycle collector:
//!
//! 1. **cycle_heavy_100** — 100 disjoint 2-node cycles, no roots. Stresses
//!    "many small cycles to reclaim" — the case where trial-deletion's
//!    O(N²) tentative-count bookkeeping was the worst against mark-sweep.
//!
//! 2. **shallow_tree_1k** — single pinned root → 1000-node tree (10-wide,
//!    3-deep object graph), nothing to reclaim. Stresses pure mark-phase
//!    BFS throughput.
//!
//! 3. **large_array_10k** — 1 pinned array with 10k object elements, all
//!    survivors. Stresses single-large-payload mark + Vec iteration.
//!
//! Bench shape: `iter_batched` separates the heap setup (cycle / tree
//! build) from the timed phase (`force_collect`). Reported numbers are
//! collect-cycle wall time only.
//!
//! Run via: `cargo bench --bench gc_cycle_bench`.

use std::collections::HashMap;
use std::sync::Arc;

use criterion::{
    black_box, criterion_group, criterion_main, BatchSize, Criterion,
};

use z42::gc::{ArcMagrGC, MagrGC};
use z42::metadata::{tokens::TypeId, NativeData, TypeDesc, Value};

fn make_type_desc(name: &str) -> Arc<TypeDesc> {
    Arc::new(TypeDesc {
        name: name.to_string(),
        base_name: None,
        fields: Vec::new(),
        field_index: HashMap::new(),
        vtable: Vec::new(),
        vtable_index: HashMap::new(),
        own_fields: Vec::new(),
        own_methods: Vec::new(),
        type_params: vec![],
        type_args: vec![],
        type_param_constraints: vec![],
        id: TypeId::UNRESOLVED,
    })
}

fn build_cycle_heavy(heap: &ArcMagrGC, num_cycles: usize) {
    let td = make_type_desc("Cycle");
    for _ in 0..num_cycles {
        let a = heap.alloc_object(td.clone(), vec![Value::Null], NativeData::None);
        let b = heap.alloc_object(td.clone(), vec![Value::Null], NativeData::None);
        {
            let Value::Object(a_gc) = &a else { unreachable!() };
            let Value::Object(b_gc) = &b else { unreachable!() };
            a_gc.borrow_mut().slots[0] = b.clone();
            b_gc.borrow_mut().slots[0] = a.clone();
        }
        // a / b drop at scope end → cycle becomes unrooted.
    }
}

fn build_shallow_tree(heap: &ArcMagrGC) -> Value {
    // 1 root → 10 children → each 10 grandchildren → each 10 great-grandchildren
    // Total = 1 + 10 + 100 + 1000 = 1111 nodes.
    let td = make_type_desc("Node");
    let leaves: Vec<Value> = (0..1000)
        .map(|_| heap.alloc_object(td.clone(), vec![Value::Null], NativeData::None))
        .collect();
    let mid: Vec<Value> = (0..100)
        .map(|i| {
            let kids: Vec<Value> = leaves[i * 10..(i + 1) * 10].to_vec();
            let kids_arr = heap.alloc_array(kids);
            heap.alloc_object(td.clone(), vec![kids_arr], NativeData::None)
        })
        .collect();
    let top: Vec<Value> = (0..10)
        .map(|i| {
            let kids: Vec<Value> = mid[i * 10..(i + 1) * 10].to_vec();
            let kids_arr = heap.alloc_array(kids);
            heap.alloc_object(td.clone(), vec![kids_arr], NativeData::None)
        })
        .collect();
    let top_arr = heap.alloc_array(top);
    heap.alloc_object(td, vec![top_arr], NativeData::None)
}

fn build_large_array(heap: &ArcMagrGC, n: usize) -> Value {
    let td = make_type_desc("Elem");
    let elems: Vec<Value> = (0..n)
        .map(|_| heap.alloc_object(td.clone(), vec![Value::Null], NativeData::None))
        .collect();
    heap.alloc_array(elems)
}

fn bench_cycle_heavy_100(c: &mut Criterion) {
    c.bench_function("gc_cycle/cycle_heavy_100", |b| {
        b.iter_batched(
            || {
                let heap = ArcMagrGC::new();
                heap.pause(); // disable auto-collect during setup
                build_cycle_heavy(&heap, 100);
                heap.resume();
                heap
            },
            |heap| {
                let stats = heap.force_collect();
                black_box(stats);
            },
            BatchSize::SmallInput,
        );
    });
}

fn bench_shallow_tree_1k(c: &mut Criterion) {
    c.bench_function("gc_cycle/shallow_tree_1k", |b| {
        b.iter_batched(
            || {
                let heap = ArcMagrGC::new();
                heap.pause();
                let root = build_shallow_tree(&heap);
                let _pin = heap.pin_root(root);
                heap.resume();
                heap
            },
            |heap| {
                let stats = heap.force_collect();
                black_box(stats);
            },
            BatchSize::SmallInput,
        );
    });
}

fn bench_large_array_10k(c: &mut Criterion) {
    c.bench_function("gc_cycle/large_array_10k", |b| {
        b.iter_batched(
            || {
                let heap = ArcMagrGC::new();
                heap.pause();
                let arr = build_large_array(&heap, 10_000);
                let _pin = heap.pin_root(arr);
                heap.resume();
                heap
            },
            |heap| {
                let stats = heap.force_collect();
                black_box(stats);
            },
            BatchSize::SmallInput,
        );
    });
}

criterion_group!(
    benches,
    bench_cycle_heavy_100,
    bench_shallow_tree_1k,
    bench_large_array_10k,
);
criterion_main!(benches);
