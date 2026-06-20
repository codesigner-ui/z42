use super::*;

#[test]
fn ascii_char_len_and_char_at() {
    let s: Arc<str> = Arc::from("hello");
    assert_eq!(char_len(&s), 5);
    assert_eq!(char_at(&s, 0), Some('h'));
    assert_eq!(char_at(&s, 4), Some('o'));
    assert_eq!(char_at(&s, 5), None);
}

#[test]
fn non_ascii_char_len_and_char_at() {
    // "a你b好c" — mixed ASCII + multibyte (你/好 are 3 bytes each in UTF-8).
    let s: Arc<str> = Arc::from("a你b好c");
    assert_eq!(char_len(&s), 5);
    assert_eq!(char_at(&s, 0), Some('a'));
    assert_eq!(char_at(&s, 1), Some('你'));
    assert_eq!(char_at(&s, 2), Some('b'));
    assert_eq!(char_at(&s, 3), Some('好'));
    assert_eq!(char_at(&s, 4), Some('c'));
    assert_eq!(char_at(&s, 5), None);
}

#[test]
fn empty_string() {
    let s: Arc<str> = Arc::from("");
    assert_eq!(char_len(&s), 0);
    assert_eq!(char_at(&s, 0), None);
}

#[test]
fn cache_hit_same_arc_is_consistent() {
    let s: Arc<str> = Arc::from("café");   // é = 2 bytes
    assert_eq!(char_len(&s), 4);
    // Repeated queries (cache hits) return identical results.
    for _ in 0..3 {
        assert_eq!(char_at(&s, 3), Some('é'));
        assert_eq!(char_len(&s), 4);
    }
}

#[test]
fn distinct_strings_independent() {
    let a: Arc<str> = Arc::from("abc");
    let b: Arc<str> = Arc::from("日本語");
    assert_eq!(char_len(&a), 3);
    assert_eq!(char_len(&b), 3);
    assert_eq!(char_at(&a, 1), Some('b'));
    assert_eq!(char_at(&b, 1), Some('本'));
}

#[test]
fn eviction_recomputes_correctly() {
    // Fill past CACHE_CAP, then re-query an early string — must still be right.
    let strings: Vec<Arc<str>> = (0..(CACHE_CAP + 3))
        .map(|i| Arc::from(format!("s{i}-ünïcode")))
        .collect();
    for s in &strings {
        assert_eq!(char_len(s), char_len(s)); // populate
    }
    // The first string was likely evicted; querying recomputes the same value.
    let expected = strings[0].chars().count();
    assert_eq!(char_len(&strings[0]), expected);
    assert_eq!(char_at(&strings[0], 2), strings[0].chars().nth(2));
}
