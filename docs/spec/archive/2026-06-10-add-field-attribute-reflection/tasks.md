# Tasks: 字段级用户 attribute 反射（zbc 1.14）

> 状态：🟢 已完成 | 创建：2026-06-10 | 完成：2026-06-10
> 子系统锁：`compiler` + `runtime` + `stdlib`
> 协调：zbc 1.14 / zpkg 0.16 → port-z42c-zbc-writer 对齐 1.14

## 进度概览
- [x] 阶段 1: parser + 合成
- [x] 阶段 2: IR + wire emit
- [x] 阶段 3: runtime load + builtin
- [x] 阶段 4: stdlib
- [x] 阶段 5: 版本 bump + fixture
- [x] 阶段 6: 测试与文档
- [x] 阶段 7: GREEN

## 阶段 1: parser + 合成（compiler）
- [x] 1.1 `Ast.cs`：`FieldDecl` 加 `List<AttributeApp>? Attributes = null`
- [x] 1.2 `TopLevelParser.Types.cs`：字段分支把 `pendingUserAttrs` 附到 FieldDecl（现丢弃）
- [x] 1.3 `AttributeFactorySynthesizer.cs`：`ProcessClass` 遍历字段，`ProcessAttributes(fld.Attributes, "fld$"+cls.Name+"$"+fld.Name, factories)`

## 阶段 2: IR + wire emit（compiler）
- [x] 2.1 `IrModule.cs`：`IrFieldDesc` 加 `List<IrAttributeRef>? Attributes`
- [x] 2.2 `IrGen.Classes.cs`：`EmitClassDesc` 填字段 attr refs（实例 + 静态）
- [x] 2.3 `ZbcWriter.cs`：字段记录（实例块 + 静态块）写 `attr_count: u16` + refs；`InternPoolStrings` 字段循环 intern attr refs；`VersionMinor 13→14`
- [x] 2.4 `ZbcReader.cs`：字段记录读 attr refs（两块）
- [x] 2.5 `ZpkgWriter.cs`：`VersionMinor 15→16`

## 阶段 3: runtime load + builtin（runtime）
- [x] 3.1 `bytecode.rs`：`FieldDesc.attributes: Box<[AttributeRef]>`（`#[serde(default)]`）
- [x] 3.2 `zbc_reader.rs`：`read_type` 字段读 attr refs（两块）+ 版本常量 14/16
- [x] 3.3 `types.rs`：`TypeDescCold.field_attributes: Box<[(Box<str>, Box<[AttributeRef]>)]>` + accessor
- [x] 3.4 `loader.rs`：`build_type_registry` 从 fields + static_fields 收集 attr → field_attributes；cold 判定加
- [x] 3.5 `reflection.rs`：`builtin_field_custom_attributes(qualified)` + `build_field_info` 加 `__qualified`
- [x] 3.6 `mod.rs`：注册 `__field_custom_attributes`

## 阶段 4: stdlib（stdlib）
- [x] 4.1 `FieldInfo.z42`：`__qualified` + `__attrCache` + `[Native] __customAttributes(qualified)` + `GetCustomAttributes()` / `GetAttribute(Type)`（镜像 MethodInfo）

## 阶段 5: 版本 bump + fixture
- [x] 5.1 `zbc_reader_tests.rs` version-pin → 14 / 16
- [x] 5.2 `zbc.md` + `zpkg.md` changelog
- [x] 5.3 `generate-fixtures.sh`（zbc + zpkg）regen
- [x] 5.4 stdlib regen（driver-direct）+ embedding_hello 重生（touch build.rs）

## 阶段 6: 测试与文档
- [x] 6.1 Golden `src/tests/attributes/field_attrs.z42`
- [x] 6.2 Dogfood `reflection.z42` [Test]
- [x] 6.3 Rust 单测
- [x] 6.4 `attributes.md`（field target 落地 + Deferred 更新）+ `reflection.md`（FieldInfo attr API）

## 阶段 7: GREEN
- [x] 7.1 dotnet build + cargo build
- [x] 7.2 dotnet test（含 golden + Format invariant + IncrementalBuild + sidecar）
- [x] 7.3 cargo test --lib
- [x] 7.4 spec scenarios 覆盖 + fixture diff

## 备注
- 参数 attribute = 独立 follow-up（add-param-attribute-reflection）。
- IncrementalBuild count：FieldInfo.z42 只改，无新 z42.core 源文件。
- z42c 自举 writer 同步 = port re-port 工作（对齐 1.14）。

## 验证报告（2026-06-10）
- ✅ dotnet build + cargo build (debug+release) — 无错
- ✅ cargo test --lib: **799/0**（含 field_attributes_accessor + lenient 单测；版本-pin → 14/16）
- ✅ dotnet test: **1554/1554**（新 golden `field_attrs.z42` 端到端：字段 attr 活实例 + 缓存 + 空；Zbc/Zpkg FormatGolden+Invariant 对 regen'd 1.14/0.16 fixtures；IncrementalBuild + sidecar）
- ✅ zbc 1.14 / zpkg 0.16 fixtures regen（注意：fixture 须用刚重建的 Driver dll regen，否则缺 attr_count 字节）；stdlib driver-direct 重建 + 显式 restage dist/release（driver 不自动 re-link flat 视图）；embedding_hello → 1.14
- ⚠️ xtask gate 僵尸 jam 未跑；C# GoldenTests 权威门。z42c 自举 writer 同步 = port re-port（对齐 1.14），ACTIVE.md 已记。参数 attribute = follow-up（add-param-attribute-reflection）
