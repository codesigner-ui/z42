use super::*;
use std::path::Path;

// ── extract_import_namespaces ─────────────────────────────────────────────────

fn ns(imports: &[&str]) -> Vec<String> {
    extract_import_namespaces(&imports.iter().map(|s| s.to_string()).collect::<Vec<_>>())
}

#[test]
fn empty_imports_returns_empty() {
    assert!(ns(&[]).is_empty());
}

#[test]
fn single_import_emits_all_prefixes() {
    // namespace-aware: emit every `.`-bounded prefix so 3+ segment stdlib
    // namespaces (Std.IO.Binary, …) get a chance to match a zpkg, not just
    // the first two segments.
    assert_eq!(
        ns(&["Std.IO.Console.WriteLine"]),
        vec!["Std", "Std.IO", "Std.IO.Console"]
    );
}

#[test]
fn deeper_namespace_emits_three_segment_prefix() {
    // Regression: Std.IO.Binary.BinaryWriter.WriteByte must yield
    // "Std.IO.Binary" so lazy loader pulls in z42.io.binary.zpkg.
    assert_eq!(
        ns(&["Std.IO.Binary.BinaryWriter.WriteByte"]),
        vec!["Std", "Std.IO", "Std.IO.Binary", "Std.IO.Binary.BinaryWriter"]
    );
}

#[test]
fn multiple_imports_same_namespace_deduplicated() {
    assert_eq!(
        ns(&["Std.IO.Console.WriteLine", "Std.IO.File.ReadText"]),
        vec!["Std", "Std.IO", "Std.IO.Console", "Std.IO.File"]
    );
}

#[test]
fn imports_from_different_namespaces_all_returned() {
    let result = ns(&["Std.IO.File.ReadText", "Std.Math.Math.Abs"]);
    assert!(result.contains(&"Std.IO".to_owned()));
    assert!(result.contains(&"Std.Math".to_owned()));
    assert!(result.contains(&"Std".to_owned()));
}

#[test]
fn import_with_one_dot_emits_first_segment() {
    assert_eq!(ns(&["mylib.Foo"]), vec!["mylib"]);
}

#[test]
fn import_with_no_dot_uses_full_name() {
    assert_eq!(ns(&["standalone"]), vec!["standalone"]);
}

// ── resolve_namespace ─────────────────────────────────────────────────────────

/// Build a minimal binary zpkg (indexed, lib) with a STRS + NSPC section.
/// Layout: header(16) + dir(sec_count×12) + META + STRS + NSPC sections.
fn make_fake_zpkg(dir: &Path, filename: &str, namespaces: &[&str]) {
    use crate::metadata::formats::ZPKG_MAGIC;

    // Build STRS section: one entry per namespace
    let encoded: Vec<Vec<u8>> = namespaces.iter().map(|s| s.as_bytes().to_vec()).collect();
    let mut strs_data: Vec<u8> = Vec::new();
    // count[4]
    strs_data.extend_from_slice(&(encoded.len() as u32).to_le_bytes());
    // entry table: [offset:u32][len:u32]
    let mut offset = 0u32;
    for b in &encoded {
        strs_data.extend_from_slice(&offset.to_le_bytes());
        strs_data.extend_from_slice(&(b.len() as u32).to_le_bytes());
        offset += b.len() as u32;
    }
    // raw data
    for b in &encoded { strs_data.extend_from_slice(b); }

    // Build NSPC section: count[4] + idx[4] per ns
    let mut nspc_data: Vec<u8> = Vec::new();
    nspc_data.extend_from_slice(&(namespaces.len() as u32).to_le_bytes());
    for i in 0u32..namespaces.len() as u32 {
        nspc_data.extend_from_slice(&i.to_le_bytes());
    }

    // Build META section: name, version, entry (each u16-len + bytes)
    let mut meta_data: Vec<u8> = Vec::new();
    for s in &["test", "0.1.0", ""] {
        let b = s.as_bytes();
        meta_data.extend_from_slice(&(b.len() as u16).to_le_bytes());
        meta_data.extend_from_slice(b);
    }

    // Assemble: 3 sections (META, STRS, NSPC)
    let sections: &[(&[u8; 4], &[u8])] = &[
        (b"META", &meta_data),
        (b"STRS", &strs_data),
        (b"NSPC", &nspc_data),
    ];
    let sec_count = sections.len() as u16;
    let header_size: usize = 16;
    let dir_size: usize = sec_count as usize * 12;
    let mut next_offset = (header_size + dir_size) as u32;

    let mut data: Vec<u8> = Vec::new();
    // Header: magic[4] + major[2] + minor[2] + flags[2] + sec_count[2] + reserved[4]
    data.extend_from_slice(&ZPKG_MAGIC);
    data.extend_from_slice(&0u16.to_le_bytes()); // major
    data.extend_from_slice(&1u16.to_le_bytes()); // minor
    data.extend_from_slice(&0u16.to_le_bytes()); // flags: indexed, lib
    data.extend_from_slice(&sec_count.to_le_bytes());
    data.extend_from_slice(&0u32.to_le_bytes()); // reserved

    // Directory
    for (tag, sec) in sections {
        data.extend_from_slice(*tag);
        data.extend_from_slice(&next_offset.to_le_bytes());
        data.extend_from_slice(&(sec.len() as u32).to_le_bytes());
        next_offset += sec.len() as u32;
    }

    // Section data
    for (_, sec) in sections { data.extend_from_slice(sec); }

    std::fs::write(dir.join(filename), &data).expect("write test zpkg");
}

/// Build a minimal binary zbc with just a NSPC section (v0.3 format with directory).
fn make_fake_zbc(dir: &Path, filename: &str, namespace: &str) {
    use crate::metadata::formats::ZBC_MAGIC;
    let ns_bytes = namespace.as_bytes();
    // NSPC section payload: u16(len) + bytes
    let nspc_payload: Vec<u8> = {
        let mut v = Vec::new();
        v.extend_from_slice(&(ns_bytes.len() as u16).to_le_bytes());
        v.extend_from_slice(ns_bytes);
        v
    };

    let sec_count: u16 = 1;
    let header_size: usize = 16;
    let dir_size: usize = sec_count as usize * 12;
    let sec_offset = (header_size + dir_size) as u32;

    let mut data: Vec<u8> = Vec::new();
    // Header: magic[4] + major[2] + minor[2] + flags[2] + sec_count[2] + reserved[4]
    data.extend_from_slice(&ZBC_MAGIC);
    data.extend_from_slice(&0u16.to_le_bytes()); // major
    data.extend_from_slice(&3u16.to_le_bytes()); // minor (v0.3)
    data.extend_from_slice(&0u16.to_le_bytes()); // flags = 0 (full)
    data.extend_from_slice(&sec_count.to_le_bytes());
    data.extend_from_slice(&0u32.to_le_bytes()); // reserved

    // Directory: NSPC entry
    data.extend_from_slice(b"NSPC");
    data.extend_from_slice(&sec_offset.to_le_bytes());
    data.extend_from_slice(&(nspc_payload.len() as u32).to_le_bytes());

    // NSPC section data
    data.extend_from_slice(&nspc_payload);

    std::fs::write(dir.join(filename), &data).expect("write test zbc");
}

/// resolve_namespace with empty paths returns an empty vec
#[test]
fn test_resolve_namespace_empty_paths() {
    let result = resolve_namespace("Std.IO", &[], &[]);
    assert!(result.is_ok());
    assert!(result.unwrap().is_empty());
}

/// Two zpkg files in the same libs tier providing the same namespace
/// → both are returned (legit under C# assembly model; disambiguation
/// happens at the lazy-load layer by zpkg file name).
#[test]
fn test_resolve_namespace_ambiguous_returns_both() {
    let tmp = std::env::temp_dir().join(format!("z42_test_{}", std::process::id()));
    std::fs::create_dir_all(&tmp).unwrap();

    make_fake_zpkg(&tmp, "libA.zpkg", &["z42.conflict"]);
    make_fake_zpkg(&tmp, "libB.zpkg", &["z42.conflict"]);

    let result = resolve_namespace("z42.conflict", &[], &[tmp.clone()]);
    std::fs::remove_dir_all(&tmp).ok();

    assert!(result.is_ok(), "unexpected error: {:?}", result.err());
    let paths = result.unwrap();
    assert_eq!(paths.len(), 2, "both zpkgs should be reported; got {paths:?}");
    let names: std::collections::HashSet<String> = paths
        .iter()
        .filter_map(|p| p.file_name().and_then(|n| n.to_str()).map(str::to_owned))
        .collect();
    assert!(names.contains("libA.zpkg"));
    assert!(names.contains("libB.zpkg"));
}

/// A zbc in module_paths and a zpkg in libs_paths both provide the same namespace
/// → module_paths wins (zpkg tier is skipped when zbc tier has matches).
#[test]
fn test_resolve_namespace_cross_tier_override() {
    let tmp = std::env::temp_dir().join(format!("z42_test_ct_{}", std::process::id()));
    let zbc_dir  = tmp.join("modules");
    let zpkg_dir = tmp.join("libs");
    std::fs::create_dir_all(&zbc_dir).unwrap();
    std::fs::create_dir_all(&zpkg_dir).unwrap();

    make_fake_zbc(&zbc_dir, "mymod.zbc", "z42.shared");
    make_fake_zpkg(&zpkg_dir, "mylib.zpkg", &["z42.shared"]);

    let result = resolve_namespace("z42.shared", &[zbc_dir.clone()], &[zpkg_dir.clone()]);
    std::fs::remove_dir_all(&tmp).ok();

    assert!(result.is_ok(), "unexpected error: {:?}", result.err());
    let paths = result.unwrap();
    assert_eq!(paths.len(), 1, "only zbc should match (zpkg tier skipped)");
    let path = &paths[0];
    assert_eq!(
        path.parent().unwrap(),
        zbc_dir.as_path(),
        "expected zbc from module_paths to win over zpkg in libs_paths"
    );
    assert_eq!(path.extension().and_then(|e| e.to_str()), Some("zbc"));
}

// ── Phase 3 S1: type_registry_vec + Module API ───────────────────────────────

/// Build a minimal Module with two classes and verify build_type_registry
/// populates both `type_registry` (HashMap by name) and `type_registry_vec`
/// (Vec by TypeId), and that the Vec[id] index agrees with the HashMap.
#[test]
fn type_registry_vec_invariant_after_build() {
    use crate::metadata::bytecode::{ClassDesc, Module};

    let mut module = Module {
        name: "Demo".to_owned(),
        string_pool: vec![],
        classes: vec![
            ClassDesc {
                name: "Demo.Aaa".to_owned(),
                base_class: None,
                fields: Box::new([]),
                type_params: Box::new([]),
                type_param_constraints: Box::new([]),
            },
            ClassDesc {
                name: "Demo.Bbb".to_owned(),
                base_class: Some("Demo.Aaa".to_owned()),
                fields: Box::new([]),
                type_params: Box::new([]),
                type_param_constraints: Box::new([]),
            },
        ],
        functions: vec![],
        type_registry: std::collections::HashMap::new(),
        type_registry_vec: Vec::new(),
        func_index: std::collections::HashMap::new(),
        func_ref_cache_slots: 0,
    };

    crate::metadata::loader::build_type_registry(&mut module);

    // Both views populated and consistent.
    assert_eq!(module.type_registry.len(), 2, "by-name HashMap has 2 types");
    assert_eq!(module.type_registry_vec.len(), 2, "by-id Vec has 2 types");

    // Topo order: Aaa (no base) → Bbb (extends Aaa). TypeId.0 == Vec index.
    let aaa = module.type_registry.get("Demo.Aaa").expect("Aaa registered");
    let bbb = module.type_registry.get("Demo.Bbb").expect("Bbb registered");
    assert_eq!(aaa.id.0, 0, "Aaa got TypeId 0 (topo first)");
    assert_eq!(bbb.id.0, 1, "Bbb got TypeId 1 (topo second)");

    // Vec[id.0] yields the same Arc as the HashMap entry.
    assert!(std::sync::Arc::ptr_eq(&module.type_registry_vec[0], aaa));
    assert!(std::sync::Arc::ptr_eq(&module.type_registry_vec[1], bbb));

    // Module::type_by_id lookup returns the same Arc.
    assert!(std::sync::Arc::ptr_eq(
        module.type_by_id(crate::metadata::tokens::TypeId(0)).unwrap(),
        aaa
    ));
    assert!(std::sync::Arc::ptr_eq(
        module.type_by_id(crate::metadata::tokens::TypeId(1)).unwrap(),
        bbb
    ));
}

#[test]
fn type_by_id_unresolved_returns_none() {
    let module = crate::metadata::bytecode::Module {
        name: String::new(),
        string_pool: vec![],
        classes: vec![],
        functions: vec![],
        type_registry: std::collections::HashMap::new(),
        type_registry_vec: Vec::new(),
        func_index: std::collections::HashMap::new(),
        func_ref_cache_slots: 0,
    };

    assert!(module.type_by_id(crate::metadata::tokens::TypeId::UNRESOLVED).is_none());
    assert!(module.type_by_id(crate::metadata::tokens::TypeId(99)).is_none());
}

#[test]
fn register_lazy_type_appends_with_next_id() {
    use crate::metadata::types::TypeDesc;
    let mut module = crate::metadata::bytecode::Module {
        name: "Demo".to_owned(),
        string_pool: vec![],
        classes: vec![],
        functions: vec![],
        type_registry: std::collections::HashMap::new(),
        type_registry_vec: Vec::new(),
        func_index: std::collections::HashMap::new(),
        func_ref_cache_slots: 0,
    };

    // Lazy type carrying a foreign id (simulating cross-zpkg arrival).
    let foreign = std::sync::Arc::new(TypeDesc {
        name: "Lazy.Foreign".to_owned(),
        id: crate::metadata::tokens::TypeId(42),
        base_name: None,
        fields: vec![],
        field_index: std::collections::HashMap::new(),
        vtable: vec![],
        vtable_index: std::collections::HashMap::new(),
        cold: None,
    });

    let assigned = module.register_lazy_type(foreign);

    // Module-local id is the next available slot (= 0 for first registration),
    // not the foreign incoming id of 42.
    assert_eq!(assigned.0, 0, "first lazy gets id 0");
    assert_eq!(module.type_registry_vec.len(), 1);
    assert_eq!(module.type_registry_vec[0].id, assigned, "stored TypeDesc rebuilt with module-local id");
    assert!(module.type_registry.contains_key("Lazy.Foreign"));

    // Re-registering the same name returns the existing id (idempotent).
    let dup = std::sync::Arc::new(TypeDesc {
        name: "Lazy.Foreign".to_owned(),
        id: crate::metadata::tokens::TypeId(99),
        base_name: None, fields: vec![], field_index: std::collections::HashMap::new(),
        vtable: vec![], vtable_index: std::collections::HashMap::new(),
        cold: None,
    });
    let dup_id = module.register_lazy_type(dup);
    assert_eq!(dup_id, assigned, "re-register returns existing id");
    assert_eq!(module.type_registry_vec.len(), 1, "no duplicate Vec slot");
}

/// resolve_dependency locates a zpkg by file name in the libs_paths.
#[test]
fn test_resolve_dependency_by_file_name() {
    let tmp = std::env::temp_dir().join(format!("z42_test_dep_{}", std::process::id()));
    std::fs::create_dir_all(&tmp).unwrap();
    make_fake_zpkg(&tmp, "z42.fake.zpkg", &["z42.fake"]);

    let hit = crate::metadata::loader::resolve_dependency("z42.fake.zpkg", &[tmp.clone()]).unwrap();
    let miss = crate::metadata::loader::resolve_dependency("does.not.exist.zpkg", &[tmp.clone()]).unwrap();
    std::fs::remove_dir_all(&tmp).ok();

    assert!(hit.is_some());
    assert_eq!(hit.unwrap().file_name().and_then(|n| n.to_str()), Some("z42.fake.zpkg"));
    assert!(miss.is_none());
}

// ── fix-cross-pkg-subclass-fields (2026-05-14) ────────────────────────────────

/// Build a minimal Module containing a single class declaration. Used by
/// fixup tests to simulate per-zpkg load + merge-into-global-registry.
fn module_with_one_class(
    name: &str,
    base: Option<&str>,
    fields: Vec<(&str, &str)>,
) -> crate::metadata::bytecode::Module {
    use crate::metadata::bytecode::{ClassDesc, FieldDesc, Module};
    Module {
        name: name.to_owned(),
        string_pool: vec![],
        classes: vec![ClassDesc {
            name: name.to_owned(),
            base_class: base.map(str::to_owned),
            fields: fields.into_iter().map(|(n, t)| FieldDesc {
                name: n.to_owned(), type_tag: t.to_owned(),
            }).collect(),
            type_params: Box::new([]),
            type_param_constraints: Box::new([]),
        }],
        functions: vec![],
        type_registry: std::collections::HashMap::new(),
        type_registry_vec: Vec::new(),
        func_index: std::collections::HashMap::new(),
        func_ref_cache_slots: 0,
    }
}

/// Cross-zpkg subclass: base in module A, subclass in module B. After
/// `try_fixup_inheritance` runs against the merged registry, the subclass
/// must inherit base's fields at low slot indices.
#[test]
fn fixup_inherits_base_fields_from_separate_module() {
    let mut mod_a = module_with_one_class("Base", None,
        vec![("name", "str"), ("age", "i64")]);
    let mut mod_b = module_with_one_class("Sub", Some("Base"),
        vec![("flag", "bool")]);

    crate::metadata::loader::build_type_registry(&mut mod_a);
    crate::metadata::loader::build_type_registry(&mut mod_b);

    // Before merge, B's Sub has only its own field — base is unresolvable
    // within mod_b's local registry.
    let sub_before = mod_b.type_registry.get("Sub").expect("Sub registered");
    assert_eq!(sub_before.own_fields().len(), 1, "Sub has 1 own field");
    assert_eq!(sub_before.fields.len(), 1, "Sub.fields lacks inherited slots pre-fixup");

    // Simulate lazy_loader merge: copy both modules' TypeDescs into a
    // global registry and run fixup.
    let mut global: std::collections::HashMap<String, std::sync::Arc<TypeDesc>> =
        std::collections::HashMap::new();
    for (n, td) in std::mem::take(&mut mod_a.type_registry) { global.insert(n, td); }
    for (n, td) in std::mem::take(&mut mod_b.type_registry) { global.insert(n, td); }
    mod_a.type_registry_vec.clear();
    mod_b.type_registry_vec.clear();

    let fixed = crate::metadata::loader::try_fixup_inheritance(&mut global);
    assert_eq!(fixed, 1, "Sub should be the single newly-fixed type");

    let sub_after = global.get("Sub").expect("Sub still present");
    assert_eq!(sub_after.fields.len(), 3, "fields = base (2) + own (1)");
    assert_eq!(sub_after.field_index.get("name"), Some(&0), "base.name at slot 0");
    assert_eq!(sub_after.field_index.get("age"),  Some(&1), "base.age at slot 1");
    assert_eq!(sub_after.field_index.get("flag"), Some(&2), "own.flag at slot 2");
}

/// Three-level cross-zpkg chain: A.Base → B.Mid → C.Leaf. Leaf must
/// inherit fields from both Mid and Base after fixup converges.
#[test]
fn fixup_handles_three_level_chain() {
    let mut mod_a = module_with_one_class("Base", None, vec![("a", "str")]);
    let mut mod_b = module_with_one_class("Mid",  Some("Base"), vec![("b", "str")]);
    let mut mod_c = module_with_one_class("Leaf", Some("Mid"),  vec![("c", "str")]);

    for m in [&mut mod_a, &mut mod_b, &mut mod_c] {
        crate::metadata::loader::build_type_registry(m);
    }

    let mut global: std::collections::HashMap<String, std::sync::Arc<TypeDesc>> =
        std::collections::HashMap::new();
    for m in [&mut mod_a, &mut mod_b, &mut mod_c] {
        m.type_registry_vec.clear();  // drop second Arc refs so fixup can mutate
    }
    for (n, td) in std::mem::take(&mut mod_a.type_registry) { global.insert(n, td); }
    for (n, td) in std::mem::take(&mut mod_b.type_registry) { global.insert(n, td); }
    for (n, td) in std::mem::take(&mut mod_c.type_registry) { global.insert(n, td); }

    // Fixed-point loop: Mid may resolve in pass 1 (base = Base, already
    // present), then Leaf in pass 2 (base = Mid, freshly fixed up).
    let mut total = 0;
    loop {
        let n = crate::metadata::loader::try_fixup_inheritance(&mut global);
        if n == 0 { break; }
        total += n;
    }
    assert!(total >= 2, "Both Mid and Leaf should fix up (got {total})");

    let leaf = global.get("Leaf").expect("Leaf present");
    assert_eq!(leaf.fields.len(), 3, "Leaf.fields = a + b + c");
    assert_eq!(leaf.field_index.get("a"), Some(&0));
    assert_eq!(leaf.field_index.get("b"), Some(&1));
    assert_eq!(leaf.field_index.get("c"), Some(&2));
}

/// Deferred fixup: load B (subclass) before A (base). On B-only fixup,
/// nothing changes. After A loads and fixup re-runs, Sub gets inherited
/// fields.
#[test]
fn fixup_deferred_until_base_loads() {
    let mut mod_b = module_with_one_class("Sub", Some("Base"), vec![("x", "str")]);
    crate::metadata::loader::build_type_registry(&mut mod_b);

    let mut global: std::collections::HashMap<String, std::sync::Arc<TypeDesc>> =
        std::collections::HashMap::new();
    mod_b.type_registry_vec.clear();
    for (n, td) in std::mem::take(&mut mod_b.type_registry) { global.insert(n, td); }

    // First fixup pass: base "Base" unresolvable — Sub stays own-only.
    let n1 = crate::metadata::loader::try_fixup_inheritance(&mut global);
    assert_eq!(n1, 0, "no fixup possible without Base");
    assert_eq!(global.get("Sub").unwrap().fields.len(), 1, "Sub still has only its own field");

    // Now A loads, bringing Base into the global registry.
    let mut mod_a = module_with_one_class("Base", None, vec![("b", "str")]);
    crate::metadata::loader::build_type_registry(&mut mod_a);
    mod_a.type_registry_vec.clear();
    for (n, td) in std::mem::take(&mut mod_a.type_registry) { global.insert(n, td); }

    // Second fixup pass: Sub now resolvable, gets inherited slot.
    let n2 = crate::metadata::loader::try_fixup_inheritance(&mut global);
    assert_eq!(n2, 1, "Sub should fix up now that Base is present");
    let sub = global.get("Sub").unwrap();
    assert_eq!(sub.fields.len(), 2);
    assert_eq!(sub.field_index.get("b"), Some(&0), "base.b at slot 0");
    assert_eq!(sub.field_index.get("x"), Some(&1), "own.x  at slot 1");
}

/// Fixup is idempotent: a second pass with no new types changes nothing.
#[test]
fn fixup_idempotent_when_no_new_resolutions() {
    let mut mod_a = module_with_one_class("Base", None, vec![("x", "str")]);
    let mut mod_b = module_with_one_class("Sub", Some("Base"), vec![]);
    for m in [&mut mod_a, &mut mod_b] {
        crate::metadata::loader::build_type_registry(m);
        m.type_registry_vec.clear();
    }
    let mut global: std::collections::HashMap<String, std::sync::Arc<TypeDesc>> =
        std::collections::HashMap::new();
    for (n, td) in std::mem::take(&mut mod_a.type_registry) { global.insert(n, td); }
    for (n, td) in std::mem::take(&mut mod_b.type_registry) { global.insert(n, td); }

    let pass1 = crate::metadata::loader::try_fixup_inheritance(&mut global);
    assert!(pass1 >= 1);
    let pass2 = crate::metadata::loader::try_fixup_inheritance(&mut global);
    assert_eq!(pass2, 0, "second pass is no-op");
}
