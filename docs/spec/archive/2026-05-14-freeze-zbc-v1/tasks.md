# Tasks: Freeze `.zbc` v1 wire format

> 状态：🟢 已完成 | 创建：2026-05-14 | 完成：2026-05-14
> 类型：`ir`（wire format 契约 + invariant test）
> 关联：[proposal.md](proposal.md) + [design.md](design.md) + [specs/zbc-format/spec.md](specs/zbc-format/spec.md)

## 进度概览

- [ ] 阶段 1: 文档同步（zbc.md 1.3 → 1.5 + strict-pin 契约）
- [ ] 阶段 2: Reader 精确匹配（C# + Rust 单点常量 + 精确版本检查）
- [ ] 阶段 3: 字节级 Golden fixture（6 个代表性 layout + harness）
- [ ] 阶段 4: Invariant + Disasm round-trip 测试
- [ ] 阶段 5: workflow.md + roadmap.md + 归档

## 阶段 1: 文档同步

- [ ] 1.1 MODIFY [docs/design/runtime/zbc.md](../../../design/runtime/zbc.md) §"版本兼容性"：
  - 当前版本 `major=1, minor=0` → `major=1, minor=5`
  - "version_minor 变化 → 新增操作码，旧 VM 遇到未知 opcode 报 `UnsupportedOpcode`" → 替换为 strict-pin 描述（spec.md MODIFIED Requirements 落地版）
  - 加 changelog 表（1.0 → 1.5；1.1/1.4 内容 grep archive 补齐，找不到则注 "Not committed via spec; reconstructed from git log"）
- [ ] 1.2 在 zbc.md 末尾加新章节 "`.zbc` minor changelog"（独立表格供未来 bump 追加）
- [ ] 1.3 MODIFY zbc.md L249-251（文件头描述）：`version_minor 当前 1` → `当前 5`；section_count 注释同步

## 阶段 2: Reader 精确匹配（单点常量）

- [ ] 2.1 MODIFY [src/compiler/z42.IR/BinaryFormat/ZbcReader.cs](../../../../src/compiler/z42.IR/BinaryFormat/ZbcReader.cs) L28-37：精确匹配 + 引用 `ZbcWriter.VersionMinor` 常量；message 含 `regen via ./scripts/regen-golden-tests.sh`
- [ ] 2.2 MODIFY [src/runtime/src/metadata/zbc_reader.rs](../../../../src/runtime/src/metadata/zbc_reader.rs)：
  - 加 `pub const ZBC_VERSION_MAJOR: u16 = 1;`
  - 加 `pub const ZBC_VERSION_MINOR: u16 = 5;`
  - 主 `read_zbc` 入口加精确匹配 + 错误信息（sidecar 路径已有 `minor < 5` 检查，同步改精确匹配）
- [ ] 2.3 grep `minor < 5` 项目内残留引用，逐个改成 `!= ZBC_VERSION_MINOR`（或对应 const）
- [ ] 2.4 `dotnet build src/compiler/z42.slnx && cargo build --manifest-path src/runtime/Cargo.toml --release` 通过

## 阶段 3: 字节级 Golden Fixture

- [ ] 3.1 NEW [src/tests/zbc-format/README.md](../../../../src/tests/zbc-format/README.md) — 目录职责 + 维护流程（按 code-organization.md 规则）
- [ ] 3.2 NEW `src/tests/zbc-format/empty/source.z42` 等 6 个 fixture 源码：
  - `empty/`：`module Empty { }`
  - `strp-func-minimal/`：单 class 单 method 单 main
  - `with-dbug-blid/`：触发 HasDebug flag（编译命令带 `--debug` 或类似）
  - `with-tidx/`：有 `[Test]` 注解
  - `with-frcs/`：触发 FRCS section（catch / vcall metadata）
  - `cross-import-token/`：import 跨 zpkg 类型并使用
- [ ] 3.3 NEW `src/tests/zbc-format/generate-fixtures.sh` — 一键 regen 脚本
- [ ] 3.4 实施 emit mode：`dotnet z42c.dll <zbc> --emit golden-json -o expected.json`
  - 优先尝试在 z42.Driver 加 `--emit golden-json` 开关（一个 case 分支 + ZbcGoldenJsonFormatter）
  - fallback：独立工具 `src/tools/zbc-golden-dump/`
- [ ] 3.5 跑 `generate-fixtures.sh` 一遍 → check in `source.zbc` + `expected.json` × 6
- [ ] 3.6 NEW `src/compiler/z42.Tests/Zbc/FormatGoldenTests.cs` — 双轨 harness（字节 + JSON）
- [ ] 3.7 `dotnet test --filter FormatGoldenTests` 全绿

## 阶段 4: Invariant + Round-trip 测试

- [ ] 4.1 NEW `src/compiler/z42.Tests/Zbc/FormatInvariantTests.cs`：
  - `WriterReaderRoundTrip`：内嵌 IR module → ZbcWriter → ZbcReader → assert 字段相等
  - `UnknownSectionSkipped`：构造含 ZZZZ section 的 zbc bytes → 读 → 不抛错
  - `MajorMismatchRejected`：构造 major=2 zbc bytes → 读 → 抛 + message 含 "expected major 1"
  - `MinorMismatchRejected`：构造 minor=4 zbc bytes → 读 → 抛 + message 含 "regen via"
  - `WriterDeterministic`：同 IR module 两次写入 → 字节相等
- [ ] 4.2 ~~NEW Rust `src/runtime/src/metadata/zbc_format_invariant_tests.rs`~~ — **延后到独立 spec `align-zbc-reader-rust-tests`**，依赖 add-std-process / fix-cross-pkg-subclass-fields 窗口先归档（test crate 当前编不通）
- [x] 4.3 ~~disasm + assemble 字节级 round-trip~~ — **drop**：实施期发现 ZasmReader / ZasmAssembler 不存在（zbc.md L422 "互转无损"过期了）。替代：在 FormatGoldenTests 内加 `WriterDeterministic` × 6 fixture 保证"同 IR module 二次 write 字节等"。`ReadWriteRoundTrip` 进一步暴露 reader-writer 字节不对称（独立 spec 跟踪，见备注）
- [ ] 4.4 `dotnet test` 全套通过 + `cargo test --lib metadata::zbc_format_invariant_tests` 全套通过

## 阶段 5: workflow + roadmap + 归档

- [ ] 5.1 MODIFY [.claude/rules/workflow.md](../../../../.claude/rules/workflow.md)：加 "Bumping `.zbc` minor version" 子节（design.md Decision 6 落地版）；与既有 "不为旧版本提供兼容" 章节交叉引用
- [ ] 5.2 MODIFY [docs/roadmap.md](../../../roadmap.md)：0.2.x 表中 0.2.0 加 archive 链接；状态标完成
- [ ] 5.3 spec scenarios 逐条覆盖确认（spec.md ADDED 7 + MODIFIED 2 = 9 个 requirement）
- [ ] 5.4 `./scripts/test-all.sh` 全绿（含新增 invariant / golden / round-trip test）
- [ ] 5.5 tasks.md 状态 → 🟢 已完成 + 完成日期
- [ ] 5.6 移动 `docs/spec/changes/freeze-zbc-v1/` → `docs/spec/archive/2026-05-14-freeze-zbc-v1/`
- [ ] 5.7 commit `feat(zbc): freeze v1 wire format — strict-pin + golden invariants (0.2.0)`：
  - 涵盖：zbc.md / ZbcReader.cs / zbc_reader.rs / fixture × 6 / invariant tests × 9 / round-trip × 3 / workflow.md / roadmap.md / spec archive
- [ ] 5.8 push origin main

## 备注

### Reader-Writer 字节不对称（实施期发现，需独立 spec）

阶段 4.3 写 `ReadWriteRoundTrip` 测试时发现：3/6 fixture（`strp-func-minimal` / `multi-method` / `with-frcs`）在 `bytes → ZbcReader → ZbcWriter` 后字节不等于原 bytes，差量 ~11 字节或更多。

`empty` / `with-tidx` / `cross-import-token` 三个 fixture round-trip 字节相等。差异似乎与本地 class 定义 + 方法体有关（SIGS / EXPT 重新编码）。

本 spec 不修这个问题（不在 strict-pin scenarios 覆盖范围）；已在 `FormatGoldenTests.cs` 写注释跟踪。后续独立 spec：**`align-zbc-reader-writer-asymmetry`**（探索阶段先用 diff tool 定位差异 section，再决定 reader 还是 writer 改）。

### Pre-existing Rust test crate compile failure（与本 spec 无关）

实施期发现 `cargo test --lib` 无法编译，因 `metadata::types::TypeDesc` 加了 `own_fields` / `own_methods` 字段（另一窗口 `fix-cross-pkg-subclass-fields` 在做的 WIP），但 `loader_tests.rs` / `corelib/tests.rs` / `gc/rc_heap_tests/mod.rs` / `exception/tests.rs` / `metadata/build_id_tests.rs` 的 struct 初始化还未补齐 `own_fields: vec![], own_methods: vec![]`。

本 spec 仅触 `src/runtime/src/metadata/zbc_reader.rs` 一个 Rust 文件，`cargo build --release` 通过；strict-pin 行为靠 C# 端 invariant tests（阶段 4）覆盖；Rust 端 invariant 在另一窗口归档其 spec 后再补（独立小 spec：`align-zbc-reader-rust-tests`）。

### 实施期可能浮现的子任务

- 1.1 changelog 表中 1.1 / 1.4 minor 内容如果在 git log 也找不到对应 commit，**只能写 "Not committed via spec"** —— 不要伪造 spec 引用；找到了用真实 spec ID
- 3.4 emit mode 实施：z42.Driver `Program.cs` 已有 `--emit` 开关，加 `golden-json` 分支即可；如果发现 ZbcGoldenJsonFormatter 体量超 500 行（code-organization 硬限），拆 sub-types
- 4.1 `UnknownSectionSkipped` 测试：reader 是从 dict 取 section，自然 skip 未识别 tag；测试构造 fixture 时需手工 / 用 BitConverter 拼字节，**不通过 IR 路径**（IR pipeline 不会产 ZZZZ section）

### 风险监控

- **emit golden-json 影响 z42c CLI 表面**：driver 加新 emit mode 算 CLI 扩展，按 code-organization 文件行数限制评估；如果发现 ZbcGoldenJsonFormatter 应独立放 `src/compiler/z42.IR/Tools/` 而非 driver
- **fixture regen 一致性**：generate-fixtures.sh 必须确定性产出（同源码 → 同字节）；阶段 4.1 的 `WriterDeterministic` 测试覆盖 writer 的确定性，generate-fixtures.sh 间接依赖
- **disasm round-trip 兼容现状**：z42c 是否已具备 disasm + assemble 字节级一致的能力？阶段 4.3 实施前先 spike 一个；若发现 disasm 输出 lossy（如格式化丢字段），按 spec.md "Open Questions" 重启讨论 —— 但 design.md 中已假设 round-trip 是现有能力（zbc.md L422 文本说"互转无损"）
