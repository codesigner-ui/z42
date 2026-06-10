# Tasks: GetFields() 含静态字段 + FieldInfo.IsStatic（zbc 1.13）

> 状态：🟢 已完成 | 创建：2026-06-10 | 完成：2026-06-10
> 子系统锁：`compiler` + `runtime` + `stdlib`
> 协调：zbc 1.13 / zpkg 0.15 → port-z42c-zbc-writer 对齐 1.13

## 进度概览
- [x] 阶段 1: 编译器 emit
- [x] 阶段 2: 运行时 load + builtin
- [x] 阶段 3: stdlib API
- [x] 阶段 4: 版本 bump + fixture
- [x] 阶段 5: 测试与文档
- [x] 阶段 6: GREEN

## 阶段 1: 编译器 emit（compiler）
- [x] 1.1 `IrModule.cs`：`IrClassDesc` 加 `List<IrFieldDesc>? StaticFields = null`
- [x] 1.2 `IrGen.Classes.cs`：`EmitClassDesc` 填 `StaticFields = cls.Fields.Where(IsStatic)`
- [x] 1.3 `ZbcWriter.cs`：flags 字节后写 `static_field_count: u16` + 块（同实例字段形）；`VersionMinor 12→13`
- [x] 1.4 `ZbcReader.cs`：`ReadTypeSection` 读静态字段块 → IrClassDesc

## 阶段 2: 运行时 load + builtin（runtime）
- [x] 2.1 `bytecode.rs`：`ClassDesc.static_fields: Box<[FieldDesc]>`（`#[serde(default)]`）
- [x] 2.2 `zbc_reader.rs`：`read_type` flags 后读静态字段块
- [x] 2.3 `types.rs`：`TypeDescCold.static_fields: Box<[FieldSlot]>`
- [x] 2.4 `loader.rs`：`build_type_registry` 透传 static_fields + cold 判定加 `!static_fields.is_empty()`
- [x] 2.5 `reflection.rs`：`builtin_type_fields` 实例后追加静态字段，每 FieldInfo 写 `IsStatic` 槽
- [x] 2.6 `zbc_reader.rs`：`ZBC_VERSION_MINOR 12→13` + `ZPKG_VERSION_MINOR 14→15`

## 阶段 3: stdlib（stdlib）
- [x] 3.1 `FieldInfo.z42`：加 `public bool IsStatic;`

## 阶段 4: 版本 bump + fixture
- [x] 4.1 `ZpkgWriter.cs` `VersionMinor 14→15`
- [x] 4.2 `zbc_reader_tests.rs` version-pin → 13 / 15
- [x] 4.3 `zbc.md` + `zpkg.md` minor changelog 各加一行
- [x] 4.4 `generate-fixtures.sh`（zbc + zpkg）regen
- [x] 4.5 stdlib regen（driver-direct build → dist/release）

## 阶段 5: 测试与文档
- [x] 5.1 Golden `src/tests/types/static_fields_reflect.z42`
- [x] 5.2 Dogfood `reflection.z42` [Test]
- [x] 5.3 Rust 单测 `reflection_tests.rs`
- [x] 5.4 `reflection.md`：API（FieldInfo.IsStatic + GetFields 含静态）+ 实现原理 + Deferred 标落地（继承静态残留）

## 阶段 6: GREEN
- [x] 6.1 dotnet build + cargo build (debug+release)
- [x] 6.2 dotnet test（Zbc/Zpkg invariant + 新 golden + IncrementalBuild + sidecar）
- [x] 6.3 cargo test --lib
- [x] 6.4 spec scenarios 逐条覆盖
- [x] 6.5 fixture diff 确认

## 备注
- 静态字段独立 list，**不**并入实例 `TypeDesc.fields`（热路径布局纯净）。
- 新增 z42.core 源文件无 → IncrementalBuild count 不变（FieldInfo.z42 只改）。
- 继承静态字段反射 = Deferred（MVP 仅声明类自身）。
- z42c 自举 writer 同步 = port re-port 工作（z42c 锁不在本变更）。

## 验证报告（2026-06-10）
- ✅ dotnet build + cargo build (debug+release) — 无错
- ✅ cargo test --lib: **797/0**（含 static_fields_accessor + lenient 单测；版本-pin → 13/15）
- ✅ dotnet test: **1553/1553**（新 golden `static_fields_reflect.z42` 端到端；Zbc/Zpkg FormatGolden+Invariant 对 regen'd 1.13/0.15 fixtures；IncrementalBuild + StdlibSidecarPairing）
- ✅ zbc 1.13 / zpkg 0.15 fixtures regen；stdlib driver-direct 重建 dist/release（22 libs）；embedding_hello.zbc → 1.13（build.rs 重生）
- 🐞 实施期 bug：`InternPoolStrings` 未 intern 静态字段名/类型 → `StringPool.Idx` KeyNotFound；已补 `cls.StaticFields` 的 intern（ZbcWriter.cs 249-253）
- ⚠️ xtask gate 僵尸 jam 未跑；以 C# GoldenTests 为权威门。z42c 自举 writer 同步 = port re-port 工作（对齐 1.13），ACTIVE.md 已记
