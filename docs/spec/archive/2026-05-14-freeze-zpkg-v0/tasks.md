# Tasks: Freeze `.zpkg` v0 wire format

> 状态：🟢 已完成 | 创建：2026-05-14 | 完成：2026-05-14
> 类型：`ir`（wire format 契约 + invariant test）
> 关联：[proposal.md](proposal.md) + [design.md](design.md) + [specs/zpkg-format/spec.md](specs/zpkg-format/spec.md)
> 模板：参考 [freeze-zbc-v1](../../archive/2026-05-14-freeze-zbc-v1/)

## 进度概览

- [ ] 阶段 1: NEW docs/design/runtime/zpkg.md（专用文档 + strict-pin + changelog 0.1 → 0.6）
- [ ] 阶段 2: ZpkgWriter 0.5 → 0.6 catch-up bump + reader 精确匹配（C# + Rust）
- [ ] 阶段 3: 字节级 Golden fixture（5 个代表性 layout + harness）+ golden-json 扩展支持 zpkg
- [ ] 阶段 4: C# invariant 测试
- [ ] 阶段 5: workflow.md + roadmap.md + 归档

## 阶段 1: zpkg.md 专用文档

- [ ] 1.1 NEW [docs/design/runtime/zpkg.md](../../../design/runtime/zpkg.md)（按 design.md "zpkg.md 结构" 落地骨架）
  - 设计目标 / 核心决策 / 文件格式（含 13 个 section 类型详解）/ Packed vs Indexed mode / Sym-only sidecar / 版本兼容性 / Minor changelog
- [ ] 1.2 在 zpkg.md 末尾加 "如何 bump minor"（引用 workflow.md）
- [ ] 1.3 MODIFY [docs/design/runtime/zbc.md](../../../design/runtime/zbc.md)：在 "如何 bump minor" 段加 zpkg 联动规则交叉引用

## 阶段 2: ZpkgWriter 0.5 → 0.6 + Reader strict-pin

- [ ] 2.1 MODIFY [src/compiler/z42.Project/ZpkgWriter.cs](../../../../src/compiler/z42.Project/ZpkgWriter.cs) line 37-38：`VersionMinor = 5` → `6`，注释更新为 catch-up 缘由（inner zbc 1.5）
- [ ] 2.2 MODIFY [src/compiler/z42.Project/ZpkgReader.cs](../../../../src/compiler/z42.Project/ZpkgReader.cs) line 279：宽容 `<` → 精确 `!= ZpkgWriter.VersionMajor / VersionMinor`，错误 message 含 `regen via ./scripts/build-stdlib.sh`
- [ ] 2.3 MODIFY [src/runtime/src/metadata/zbc_reader.rs](../../../../src/runtime/src/metadata/zbc_reader.rs)：
  - 加 `pub const ZPKG_VERSION_MAJOR: u16 = 0;`
  - 加 `pub const ZPKG_VERSION_MINOR: u16 = 6;`
  - line ~647 (parse_zpkg_sidecar) + line ~1191 (read_zpkg) 改精确匹配
- [ ] 2.4 `dotnet build src/compiler/z42.slnx && cargo build --manifest-path src/runtime/Cargo.toml --release` 通过
- [ ] 2.5 `./scripts/build-stdlib.sh` —— 验证 stdlib 重新产 0.6 zpkg 成功（验证 bump 不破坏既有 build flow）

## 阶段 3: 字节级 Golden Fixture + golden-json zpkg 扩展

- [ ] 3.1 NEW [src/tests/zpkg-format/README.md](../../../../src/tests/zpkg-format/README.md) — 目录职责 + 维护流程（类比 zbc-format/README.md）
- [ ] 3.2 NEW [src/compiler/z42.IR/BinaryFormat/ZpkgGoldenJsonFormatter.cs](../../../../src/compiler/z42.IR/BinaryFormat/ZpkgGoldenJsonFormatter.cs) — 字段集见 design.md decision 6
- [ ] 3.3 MODIFY [src/compiler/z42.Driver/Program.cs](../../../../src/compiler/z42.Driver/Program.cs) `golden-json` 子命令：按扩展名分发 .zbc / .zpkg
- [ ] 3.4 实施 5 个 fixture（spike fixture 生成路径：workspace mode vs ZpkgWriter direct construction，根据复杂度决定）：
  - [ ] 3.4a `packed-minimal/`
  - [ ] 3.4b `packed-multi-module/`
  - [ ] 3.4c `packed-with-tsig/`
  - [ ] 3.4d `indexed-minimal/`
  - [ ] 3.4e `sym-only-sidecar/`
- [ ] 3.5 NEW `src/tests/zpkg-format/generate-fixtures.sh` —— 一键 regen 脚本
- [ ] 3.6 跑 `generate-fixtures.sh` → check in 5 个 source.zpkg + 5 个 expected.json
- [ ] 3.7 NEW [src/compiler/z42.Tests/Zpkg/FormatGoldenTests.cs](../../../../src/compiler/z42.Tests/Zpkg/FormatGoldenTests.cs)：
  - `ByteEqual(fixture)` × 5
  - `JsonEqual(fixture)` × 5
  - `WriterDeterministic(fixture)` × 5
- [ ] 3.8 `dotnet test --filter FullyQualifiedName~Z42.Tests.Zpkg.FormatGoldenTests` 全绿

## 阶段 4: C# Invariant 测试

- [ ] 4.1 NEW [src/compiler/z42.Tests/Zpkg/FormatInvariantTests.cs](../../../../src/compiler/z42.Tests/Zpkg/FormatInvariantTests.cs)：
  - `WriterVersionConstantsExposed` —— ZpkgWriter.VersionMajor / VersionMinor 可访问
  - `MajorMismatchRejected` —— 构造 major=1 字节 → reader 抛
  - `MinorBelowWriterRejected` —— minor=5 → reader 抛
  - `MinorAboveWriterRejected` —— minor=7 → reader 抛
  - `UnknownSectionSkipped` —— `XXXX` section → reader 不抛
- [ ] 4.2 `dotnet test --filter FullyQualifiedName~Z42.Tests.Zpkg.FormatInvariantTests` 全绿

## 阶段 5: workflow.md + roadmap.md + 归档

- [ ] 5.1 MODIFY [.claude/rules/workflow.md](../../../../.claude/rules/workflow.md)：
  - "Bumping `.zbc` minor version" 子节 step 5 加 zpkg 同步（ZpkgWriter / Rust 常量 / zpkg.md / fixture regen）
  - 加新子节 "Bumping `.zpkg` minor version (independent)" —— 仅 zpkg outer 变（不动 zbc）
- [ ] 5.2 MODIFY [docs/roadmap.md](../../../roadmap.md) 0.2.1 行：加 archive 链接 + 标完成（注：disasm 完整化作为另一半，视实施后决定本 spec 顺手做还是独立 spec）
- [ ] 5.3 spec scenarios 逐条覆盖确认（spec.md ADDED 8 + MODIFIED 2 = 10 个 requirement）
- [ ] 5.4 `./scripts/test-all.sh` 全绿（含新增 5 fixture × 3 测试 + 5 invariant）
- [ ] 5.5 tasks.md 状态 → 🟢 已完成 + 完成日期
- [ ] 5.6 移动 `docs/spec/changes/freeze-zpkg-v0/` → `docs/spec/archive/2026-05-14-freeze-zpkg-v0/`
- [ ] 5.7 commit `feat(zpkg): freeze v0 wire format — strict-pin + golden invariants + catch-up bump 0.5→0.6 (0.2.1)`
- [ ] 5.8 push origin main

## 备注

### 实施期可能浮现的子任务

- 3.2 ZpkgGoldenJsonFormatter 体量预估 80-120 行（zpkg header + 13 section 类型字段聚合）；若 > 500 行硬限按 code-organization 拆 sub-types
- 3.4 fixture 生成路径 spike：
  - 优先 workspace + `z42c build` 模式（贴近实际产物）
  - 太重则 fallback ZpkgWriter direct construction（C# 内嵌 fixture builder helper）
- 3.4e `sym-only-sidecar/` fixture 需要 stripped mode；可能要复用现有 stdlib build pipeline 的 `.zsym` 产物，或在 fixture harness 内 invoke `ZpkgWriter.WriteSidecar`（如有）
- 5.2 `z42c disasm` zpkg 完整化：阶段 4 走完后用 1-2 小时 spike；可行则本 spec 顺手做（追加 task 5.x），否则独立 spec
- 1.1 zpkg.md 体量预估 200-250 行；超 500 行硬限按 code-organization 拆主文档 + sub-pages

### Pre-existing Rust test crate compile failure（与本 spec 无关）

同 freeze-zbc-v1 spec 备注：Rust 端 `loader_tests.rs` 等 5 文件因 `TypeDesc` 新增 `own_fields/own_methods` 字段未同步初始化而编不通；属 fix-cross-pkg-subclass-fields 窗口工作。本 spec **不写 Rust invariant test**（延后到独立 spec `align-zpkg-reader-rust-tests`，依赖另一窗口归档）。

### 风险监控

- **build-stdlib.sh 在 0.5 → 0.6 后是否能产正确的 0.6 zpkg**：阶段 2.5 必须验证；如果 stdlib 加载链路缓存了旧 zpkg，需要清 `artifacts/build/libs/` 重 build
- **golden-json 子命令 zpkg 路径**：原 C# `disasm` 子命令已有 `.zpkg` 分支但用 `ZpkgReader.ReadModules`；golden-json 走专属 ZpkgGoldenJsonFormatter（输出更结构化）；不混用
- **fixture sym-only-sidecar 复杂度**：sidecar 产出依赖 stripped mode + BLID hash；如果 z42c CLI 暴露不到该路径，可能需要在 fixture harness 内调底层 API；超出预期则 drop 该 fixture（保留 4 个）+ tasks 记录
- **strict-pin 突然把所有现存 stdlib zpkg 锁掉**：本 spec 第一次 bump 0.5 → 0.6 触发该效应；执行 `build-stdlib.sh` regen 即可；CI 上同样
