use super::*;

#[test]
fn snapshot_freezes_line_and_column() {
    let f = FrameInfo::new("Foo".into(), "f.z42".into(), std::ptr::null(), std::ptr::null());
    f.line.set(7);
    f.column.set(13);
    let snap = f.snapshot();
    f.line.set(99);     // mutates after snapshot
    f.column.set(40);
    assert_eq!(snap.line,   7);
    assert_eq!(snap.column, 13);
    assert_eq!(snap.func_name, "Foo");
    assert_eq!(snap.file, "f.z42");
}

#[test]
fn format_orders_caller_to_throw_last() {
    // call_stack pushed in chrono order: Main → A → B (B is the throwing frame)
    let frames = vec![
        FrameSnapshot { func_name: "Main".into(), file: "f.z42".into(), line: 3,  column: 9 },
        FrameSnapshot { func_name: "A".into(),    file: "f.z42".into(), line: 7,  column: 5 },
        FrameSnapshot { func_name: "B".into(),    file: "f.z42".into(), line: 12, column: 1 },
    ];
    let out = format_stack_trace(&frames);
    let lines: Vec<&str> = out.lines().collect();
    // First line (most recent / throwing frame) is B
    assert_eq!(lines[0], "  at B (f.z42:12:1)");
    assert_eq!(lines[1], "  at A (f.z42:7:5)");
    assert_eq!(lines[2], "  at Main (f.z42:3:9)");
}

#[test]
fn format_drops_column_when_zero() {
    // zbc < 1.1 (or hand-rolled IR) → column = 0 → degrade to (file:line).
    let frames = vec![
        FrameSnapshot { func_name: "Foo".into(), file: "f.z42".into(), line: 5, column: 0 },
    ];
    assert_eq!(format_stack_trace(&frames), "  at Foo (f.z42:5)");
}

#[test]
fn format_omits_file_when_empty() {
    let frames = vec![
        FrameSnapshot { func_name: "Anon".into(), file: String::new(), line: 5, column: 8 },
    ];
    assert_eq!(format_stack_trace(&frames), "  at Anon (line 5, col 8)");
}

#[test]
fn format_omits_line_when_zero() {
    let frames = vec![
        FrameSnapshot { func_name: "Init".into(), file: "f.z42".into(), line: 0, column: 0 },
    ];
    assert_eq!(format_stack_trace(&frames), "  at Init (f.z42)");
}

#[test]
fn format_handles_no_position_info() {
    let frames = vec![
        FrameSnapshot { func_name: "Bare".into(), file: String::new(), line: 0, column: 0 },
    ];
    assert_eq!(format_stack_trace(&frames), "  at Bare");
}
