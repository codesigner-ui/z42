# Tasks: `--format junit` JUnit XML output

> 状态：🟢 已完成 | 完成：2026-05-31 | 创建：2026-05-31 | 类型：fix (test-runner formatter)

**变更说明：** Runner 现有 pretty / tap / json 三种 formatter。CI 系统
(Jenkins / GitLab CI / CircleCI / GitHub Actions test-reporter) 普遍原生
ingest **JUnit XML** —— 它是事实标准的测试结果交换格式。加 `--format junit`
让 z42 测试结果直接喂进这些 CI 的 test-report UI (失败高亮 / 历史趋势 /
flaky 检测) 无需中间转换脚本。

输出 schema (de-facto JUnit):
```xml
<?xml version="1.0" encoding="UTF-8"?>
<testsuites tests="N" failures="F" skipped="S" time="T.TTT">
  <testsuite name="<module>" tests="N" failures="F" skipped="S" time="T.TTT">
    <testcase name="<method>" classname="<module>" time="0.012"/>
    <testcase name="<method>" classname="<module>" time="0.000">
      <failure message="<reason first line>"><full reason + stack, escaped></failure>
    </testcase>
    <testcase name="<method>" classname="<module>" time="0.000">
      <skipped message="<reason>"/>
    </testcase>
  </testsuite>
</testsuites>
```

**原因：** rounds out the runner output story; JSON (#9) is z42-specific,
JUnit XML is the universal CI lingua franca. Small self-contained
formatter, no new deps (hand-rolled XML + escaping, matching the crate's
no-regex/no-extra-deps style).

**文档影响：** `docs/design/testing/testing.md` Runner 输出格式段 + CLI
flags 段加 junit；`--format` 选项枚举更新。

## 任务

- [ ] 1.1 `src/toolchain/test-runner/src/format/mod.rs`:
  - `Format` enum 加 `Junit` 变体 (clap `rename_all=lower` → `--format junit`)
  - module 加 `pub mod junit;`
- [ ] 1.2 NEW `src/toolchain/test-runner/src/format/junit.rs`:
  - `pub fn print(module_name: &str, results: &[TestResult])`
  - `<testsuites>` + single `<testsuite>` (one module per run)
  - per-test `<testcase name classname time>`:
    - Passed → self-closing
    - Failed → nested `<failure message="<reason 1st line>">` + text body
      含 full reason + (若有) stack_trace, XML-escaped
    - Skipped → nested `<skipped message="<reason>"/>`
  - `time` 属性 = duration_ms / 1000.0 (秒, 3 decimal)
  - `xml_escape_attr` (& < > ") + `xml_escape_text` (& < >) helpers
  - benchmark entries: 普通 testcase (JUnit 无 benchmark 概念); name 保持
    method name (is_benchmark 在 JUnit 不体现; JSON 仍有)
- [ ] 1.3 `src/toolchain/test-runner/src/main.rs::emit`:
  - 加 `Format::Junit => format::junit::print(...)` 分支
- [ ] 1.4 单元测试 (junit.rs 内 `mod tests`):
  - `junit_skeleton_passed_test` — self-closing testcase shape
  - `junit_failed_test_has_failure_element` — message=1st line, body 含 reason
  - `junit_skipped_test_has_skipped_element`
  - `junit_escapes_xml_special_chars` — reason 含 `<`/`&`/`"` 正确转义
  - `junit_testsuites_counts_match` — tests/failures/skipped 属性数对
- [ ] 1.5 文档:
  - `docs/design/testing/testing.md` "Runner 输出格式" 段加 JUnit 小节 +
    示例 XML + CI 集成 hint (Jenkins junit step / GitLab artifacts:reports:junit)
  - CLI flags reference 表 `--format` 行加 `junit`
- [ ] 1.6 `cargo build` + `cargo test` GREEN
- [ ] 1.7 spot-check via `cargo run -- <bench_demo.zbc> --format junit`
- [ ] 1.8 `./scripts/test-all.sh --parallel --jobs=4` 全绿
- [ ] 1.9 commit + push + archive

## 备注

- 单 module/run → 单 `<testsuite>` 包在 `<testsuites>` 里 (JUnit 允许多
  suite; 我们一次跑一个 .zbc = 一个 module = 一个 suite)
- `classname` = module name (JUnit 用 classname 做 grouping; z42 module 名
  是自然 group key)
- time 秒数 float 3 位小数; skipped 的 duration_ms=0 → time="0.000"
- 不引入 XML 库 (quick-xml 等); hand-roll escaping 足够 (输出端可控, 只需
  escape 5 chars). 与现有 tap.rs yaml_escape 同风格
