use super::*;

#[test]
fn namespace_prefix_of_qualified_call() {
    assert_eq!(namespace_prefix("Std.IO.Console.WriteLine"), Some("Std.IO".to_string()));
    assert_eq!(namespace_prefix("Std.Text.StringBuilder.Append$1"),
               Some("Std.Text".to_string()));
}

#[test]
fn namespace_prefix_of_shallow_name() {
    assert_eq!(namespace_prefix("Assert.Equal"), Some("Assert".to_string()));
    assert_eq!(namespace_prefix("main"), None);
}

#[test]
fn install_then_uninstall_is_clean() {
    install(None);
    assert!(try_lookup_function("Std.IO.Console.WriteLine").is_none());
    uninstall();
}
