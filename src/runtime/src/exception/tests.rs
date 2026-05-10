use super::*;

#[test]
fn snapshot_freezes_line() {
    let f = FrameInfo::new("Foo".into(), "f.z42".into());
    f.line.set(7);
    let snap = f.snapshot();
    f.line.set(99);   // mutates after snapshot
    assert_eq!(snap.line, 7);
    assert_eq!(snap.func_name, "Foo");
    assert_eq!(snap.file, "f.z42");
}

#[test]
fn format_orders_caller_to_throw_last() {
    // call_stack pushed in chrono order: Main → A → B (B is the throwing frame)
    let frames = vec![
        FrameSnapshot { func_name: "Main".into(), file: "f.z42".into(), line: 3 },
        FrameSnapshot { func_name: "A".into(),    file: "f.z42".into(), line: 7 },
        FrameSnapshot { func_name: "B".into(),    file: "f.z42".into(), line: 12 },
    ];
    let out = format_stack_trace(&frames);
    let lines: Vec<&str> = out.lines().collect();
    // First line (most recent / throwing frame) is B
    assert_eq!(lines[0], "  at B (f.z42:12)");
    assert_eq!(lines[1], "  at A (f.z42:7)");
    assert_eq!(lines[2], "  at Main (f.z42:3)");
}

#[test]
fn format_omits_file_when_empty() {
    let frames = vec![
        FrameSnapshot { func_name: "Anon".into(), file: String::new(), line: 5 },
    ];
    let out = format_stack_trace(&frames);
    assert_eq!(out, "  at Anon (line 5)");
}

#[test]
fn format_omits_line_when_zero() {
    let frames = vec![
        FrameSnapshot { func_name: "Init".into(), file: "f.z42".into(), line: 0 },
    ];
    assert_eq!(format_stack_trace(&frames), "  at Init (f.z42)");
}

#[test]
fn format_handles_no_position_info() {
    let frames = vec![
        FrameSnapshot { func_name: "Bare".into(), file: String::new(), line: 0 },
    ];
    assert_eq!(format_stack_trace(&frames), "  at Bare");
}
