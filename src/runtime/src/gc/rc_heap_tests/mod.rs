//! `RcMagrGC` 单元测试 —— 覆盖全部 11 个能力组。

use std::collections::HashMap;
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};

use crate::gc::{GcEvent, GcHandleKind, GcKind, GcObserver, MagrGC, RcMagrGC, SnapshotCoverage};
use crate::metadata::{NativeData, TypeDesc, Value};

pub(super) fn dummy_type_desc(name: &str) -> Arc<TypeDesc> {
    Arc::new(TypeDesc {
        name: name.to_string(),
        base_name: None,
        fields: Vec::new(),
        field_index: HashMap::new(),
        vtable: Vec::new(),
        vtable_index: HashMap::new(),
        type_params: vec![],
        type_args: vec![],
        type_param_constraints: vec![],
    })
}

mod allocation;
mod collection;
mod config_stats;
mod cycle_collection;
mod events;
mod finalization;
mod object_model;
mod oom;
mod roots;
mod weak_refs;
