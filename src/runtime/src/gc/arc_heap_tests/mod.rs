//! `ArcMagrGC` 单元测试 —— 覆盖全部 11 个能力组。

use std::collections::HashMap;
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};

use crate::gc::{GcEvent, GcHandleKind, GcKind, GcObserver, MagrGC, ArcMagrGC, SnapshotCoverage};
use crate::metadata::{NativeData, TypeDesc, Value};

pub(super) fn dummy_type_desc(name: &str) -> Arc<TypeDesc> {
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
        id: crate::metadata::tokens::TypeId::UNRESOLVED,
    })
}

mod allocation;
mod collection;
mod concurrent_mark;
mod config_stats;
mod cycle_collection;
mod events;
mod finalization;
mod generational;
mod invariants;
mod mark_phase;
mod mode_selection;
mod multi_vm;
mod object_model;
mod oom;
mod pause_histogram;
mod roots;
mod send_sync;
mod stress;
mod weak_refs;
mod write_barriers;
