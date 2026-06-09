# Tasks: fix-classdesc-attributes-windows

**变更说明：** 6 个 `#[cfg(test)]` 的 `ClassDesc {…}` 初始化器缺 `attributes` 字段 → Windows build-and-test 的 rust 单测编译 `error[E0063]: missing field 'attributes' in initializer of 'ClassDesc'`（×6），阻塞 publish-nightly。

**原因：** C3b（add-attribute-reflection-methods）给 `ClassDesc` 加了 `attributes: Box<[AttributeRef]>`（bytecode.rs:129），更新了生产初始化器（zbc_reader.rs:327）但漏了 3 个测试文件里的 6 个初始化器。macOS/Linux build-and-test 腿不编译 rust 单测模块（跑 release VM + z42 xtask golden），只有 Windows "Smoke test" 腿编 rust 单测 → Windows-only 失败。

**文档影响：** 无（test-only fix，不改 wire format / 行为；非格式 bump，不动 version-bumping checklist）。

**子系统：** `runtime`（空闲；C3b 已归档释放）。fix 型，minimal mode。

- [x] 1.1 `src/runtime/src/metadata/constraint_tests.rs`：simple_class 的 ClassDesc 加 `attributes: Box::new([])`
- [x] 1.2 `src/runtime/src/metadata/merge_tests.rs`：2 个 ClassDesc 加 `attributes`
- [x] 1.3 `src/runtime/src/metadata/loader_tests.rs`：3 个 ClassDesc 加 `attributes`
- [x] 1.4 `cargo test --no-run` ✅ Finished，无 E0063（macOS 上即可复现/验证，与平台无关）
- [x] 1.5 commit + push + 归档

## 备注
- 这是 publish-nightly 的**确定性**阻塞项（Windows rust 单测编译）。修后 build-and-test (windows) 可绿。
- ubuntu-arm 腿的 `Parser_NeverCrashes_Random` 是**另一个、flaky** 的失败（unseeded FsCheck parser fuzz，shrunk input `a(){8F` → parser 抛非 ParseException 的崩溃）——pre-existing parser robustness gap，**compiler 子系统（被 fix-fqn-class-resolution 占）**，本 fix 不碰；靠 re-run 过 + 另立 compiler change 修 parser/seed test。
