use super::libs_env_to_publish;
use std::path::Path;

// VM resolved a dir and Z42_LIBS is unset → publish the resolved dir so the
// in-process z42c sees the same libs dir (SDK layout works with no env set).
#[test]
fn unset_env_publishes_resolved_dir() {
    let got = libs_env_to_publish(None, Some(Path::new("/sdk/libs")));
    assert_eq!(got.as_deref(), Some("/sdk/libs"));
}

// Empty string counts as unset (mirrors RuntimeConfig env handling).
#[test]
fn empty_env_is_treated_as_unset() {
    let got = libs_env_to_publish(Some("  "), Some(Path::new("/sdk/libs")));
    assert_eq!(got.as_deref(), Some("/sdk/libs"));
}

// Explicit Z42_LIBS is the caller's deliberate choice → never overridden.
#[test]
fn explicit_env_is_left_untouched() {
    assert_eq!(libs_env_to_publish(Some("/my/libs"), Some(Path::new("/sdk/libs"))), None);
}

// Nothing resolved anywhere → nothing to publish (z42c keeps its no-deps
// degraded path, unchanged from before).
#[test]
fn no_resolution_publishes_nothing() {
    assert_eq!(libs_env_to_publish(None, None), None);
}
