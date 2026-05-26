# Tasks: Align zbc reader-writer asymmetry (Option A)

> 状态：🟢 已完成 | 创建：2026-05-27 | 归档：2026-05-27 | 类型：ir（wire format + double version bump）

## 阶段 1: Writer 端（C#）

- [x] 1.1 MODIFY `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs` — SIGS 加 `w.Write((uint)pool.Idx(fn.RetType))`；TYPE 加 `w.Write((uint)pool.Idx(fld.Type))`；`VersionMinor` 6 → 7
- [x] 1.2 MODIFY `src/compiler/z42.Project/ZpkgWriter.cs` — `VersionMinor` 7 → 8
- [x] 1.3 MODIFY `src/compiler/z42.IR/BinaryFormat/ZbcReader.cs` — SIGS 读 ret_type str_idx；TYPE 读 field_type str_idx；用 string 作为 ret_type / FieldDesc.Type
- [x] 1.4 VERIFY: `dotnet build src/compiler/z42.slnx` 无 error

## 阶段 2: Reader 端（Rust）

- [x] 2.1 MODIFY `src/runtime/src/metadata/zbc_reader.rs` — `ZBC_VERSION_MINOR` 6 → 7；`ZPKG_VERSION_MINOR` 7 → 8；`read_sigs` 读 ret_type str_idx；`read_type` 读 field_type str_idx
- [x] 2.2 VERIFY: `cargo build --manifest-path src/runtime/Cargo.toml --release` 无 error

## 阶段 3: Fixture regen + stdlib regen

- [x] 3.1 EXEC `./src/tests/zbc-format/generate-fixtures.sh` → 6 fixture source.zbc 全刷
- [x] 3.2 EXEC `./src/tests/zpkg-format/generate-fixtures.sh` → 4 fixture source.zpkg 全刷
- [x] 3.3 EXEC `./scripts/regen-golden-tests.sh` → 所有 stdlib zpkg 重生

## 阶段 4: Round-trip CI test

- [x] 4.1 NEW `src/compiler/z42.Tests/Zbc/ReadWriteRoundTripTests.cs` — 加载每个 zbc fixture bytes → C# ZbcReader → C# ZbcWriter → 比较字节相等

## 阶段 5: 文档同步

- [x] 5.1 MODIFY `docs/design/runtime/zbc.md` — changelog 加 1.7 行；Deferred 删 reader-writer-asymmetry；header "当前 6" → "当前 7"
- [x] 5.2 MODIFY `docs/design/runtime/zpkg.md` — changelog 加 0.8 行；Deferred 删 reader-writer-asymmetry
- [x] 5.3 MODIFY `docs/roadmap.md` — Deferred Backlog Index 删 reader-writer-asymmetry 行

## 阶段 6: GREEN + 归档

- [x] 6.1 `./scripts/test-all.sh --scope=full` 全绿
- [x] 6.2 mv `docs/spec/changes/align-zbc-reader-writer-asymmetry/` → `docs/spec/archive/2026-05-27-align-zbc-reader-writer-asymmetry/`
- [x] 6.3 commit + push

## 备注

- **User override**：zbc.md Deferred §reader-writer-asymmetry 当前决定是 Option C（永不修）。User 2026-05-27 显式选 Option A。design.md "Decision provenance" 段记录这次 override
- **双 minor bump**：zbc 6→7 + zpkg 7→8 必须同 commit（联动规则见 version-bumping.md）
- **TypeTag u8 不删除**：保留作 hint；权威 source 改 string
- **ParamTypes 不动**：已经是字符串（1.3+），无 lossy
