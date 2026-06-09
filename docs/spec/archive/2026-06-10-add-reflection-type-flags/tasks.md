# Tasks: Type.IsAbstract / IsSealed（zbc 1.12）

> 状态：🟢 已完成 | 创建：2026-06-10 | 完成：2026-06-10
> 子系统锁：`compiler` + `runtime` + `stdlib`（见 ACTIVE.md）
> 协调：zbc 1.12 / zpkg 0.14 → port-z42c-zbc-writer 对齐 1.12

## 进度概览
- [x] 阶段 1: 编译器 emit
- [x] 阶段 2: 运行时 load + builtin
- [x] 阶段 3: stdlib API
- [x] 阶段 4: 版本 bump + fixture
- [x] 阶段 5: 测试与文档
- [x] 阶段 6: GREEN

## 阶段 1: 编译器 emit（compiler）
- [x] 1.1 `IrModule.cs`：`IrClassDesc` 加 `IsAbstract`/`IsSealed`/`IsStruct`/`IsRecord`（默认 false）
- [x] 1.2 `IrGen.Classes.cs`：`EmitClassDesc` 从 `ClassDecl` 填 4 个 flag
- [x] 1.3 `ZbcWriter.cs`：`BuildTypeSection` attr 块后 `w.Write((byte)flags)`；位常量 1/2/4/8
- [x] 1.4 `ZbcReader.cs`：`ReadTypeSection` 读 flags 字节 → IrClassDesc（往返一致）

## 阶段 2: 运行时 load + builtin（runtime）
- [x] 2.1 `bytecode.rs`：`ClassDesc.class_flags: u8` + 位常量
- [x] 2.2 `zbc_reader.rs`：`read_type` attr 后 `read_u8()` → class_flags
- [x] 2.3 `types.rs`：`TypeDesc.class_flags: u8`
- [x] 2.4 `loader.rs`：`build_type_registry` 透传 class_flags
- [x] 2.5 `reflection.rs`：`builtin_type_is_abstract` / `builtin_type_is_sealed`（lenient 无句柄→false）
- [x] 2.6 `mod.rs`：注册 `__type_is_abstract` / `__type_is_sealed`

## 阶段 3: stdlib（stdlib）
- [x] 3.1 `Type.z42`：`[Native("__type_is_abstract")] extern bool IsAbstract { get; }` + IsSealed

## 阶段 4: 版本 bump + fixture（version-bumping.md checklist）
- [x] 4.1 `ZbcWriter.cs` `VersionMinor 11→12`（注释本次变更）
- [x] 4.2 `zbc_reader.rs` `ZBC_VERSION_MINOR 11→12`
- [x] 4.3 `ZpkgWriter.cs` `VersionMinor 13→14` + `zbc_reader.rs` `ZPKG_VERSION_MINOR 13→14`
- [x] 4.4 `docs/design/runtime/zbc.md` + `zpkg.md` minor changelog 各加一行
- [x] 4.5 `src/tests/zbc-format/generate-fixtures.sh` + `src/tests/zpkg-format/generate-fixtures.sh` regen
- [x] 4.6 stdlib regen（driver-direct build z42.core + 全 libs → dist/release）

## 阶段 5: 测试与文档
- [x] 5.1 Golden `src/tests/types/type_flags.z42`（abstract/sealed/plain/子类）
- [x] 5.2 Dogfood `reflection.z42` [Test]
- [x] 5.3 Rust 单测 `reflection_tests.rs`（lenient + flags 解码）
- [x] 5.4 `reflection.md`：API 表加 IsAbstract/IsSealed + 实现原理 + Deferred 标落地（struct/record/IsStatic 残留说明）

## 阶段 6: GREEN
- [x] 6.1 dotnet build + cargo build (debug+release)
- [x] 6.2 dotnet test（含 Zbc/Zpkg format invariant + 新 golden + IncrementalBuild）
- [x] 6.3 cargo test --lib（含新单测）
- [x] 6.4 spec scenarios 逐条覆盖
- [x] 6.5 fixture diff 确认（flags 字节 + 版本号）

## 备注
- 格式 bump：port-z42c-zbc-writer 需对齐 1.12（同 re-port 周期，不多一轮）。
- 无新 z42.core 源文件 → IncrementalBuild count 不变（Type.z42 只改方法）。

## 验证报告（2026-06-10）
- ✅ dotnet build + cargo build (debug+release) — 无错
- ✅ cargo test --lib: **795/0**（含 type_flags_decode + handle-less 单测；版本-pin 测试更到 12/14）
- ✅ dotnet test: **1552/1552**（新 golden `type_flags.z42` 端到端；Zbc/Zpkg FormatGolden + FormatInvariant 对 regen'd 1.12/0.14 fixtures；IncrementalBuild count 0/69；StdlibSidecarPairing BLID）
- ✅ zbc 1.12 / zpkg 0.14 fixtures regen（`src/tests/zbc-format/*` + `zpkg-format/*`）；stdlib 经 driver-direct 重建 dist/release（22 libs）
- ⚠️ xtask gate（test vm/lib + z42c zbc 单测）因僵尸 jam 未跑；以 C# GoldenTests 为权威门（本会话约定）。**z42c 自举 writer 同步（version-bumping 步骤 5）= port-z42c-zbc-writer 的 re-port 工作**（z42c 锁不在本变更），ACTIVE.md 已记协调（直接对齐 1.12）
