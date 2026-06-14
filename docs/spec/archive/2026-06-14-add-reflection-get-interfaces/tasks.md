# Tasks: Type.GetInterfaces()

> 状态：🟢 已完成 | 创建：2026-06-14 | 完成：2026-06-14 | 类型：ir（格式 bump，完整流程）

## 阶段 0
- [x] 0.1 6.5 gate 审批（方案：unified Type + GetInterfaces；含继承接口；传递实现延后）

## 阶段 1: 二进制格式（TYPE section 接口块）
- [x] 1.1 C# `ZbcWriter.cs`：`VersionMinor`=17；BuildTypeSection 静态字段块后写 `interface_count: u16` + `interface_name_idx[]: u32`；InternPoolStrings per-class 段 intern 接口名
- [x] 1.2 C# `ZbcReader.cs`：ReadTypeSection 读接口块 → `IrClassDesc.Interfaces`（round-trip parity）
- [x] 1.3 C# `ZpkgWriter.cs`：`VersionMinor`=19
- [x] 1.4 Rust `zbc_reader.rs`：`ZBC_VERSION_MINOR`=17 / `ZPKG_VERSION_MINOR`=19；read_type 读接口块 → ClassDesc.interfaces
- [x] 1.5 Rust `zbc_reader_tests.rs`：version-pin 断言 17/19
- [x] 1.6 `docs/design/runtime/{zbc,zpkg}.md` changelog + TYPE section 接口块布局

## 阶段 2: 运行期类型元数据
- [x] 2.1 `types.rs`：`ClassDesc.interfaces: Box<[String]>` + `TypeDescCold.interfaces: Box<[Box<str>]>` + `interfaces()` accessor + ClassDesc→TypeDesc load 透传

## 阶段 3: 反射 builtin + stdlib
- [x] 3.1 `reflection.rs`：`builtin_type_interfaces`（base-walk td.base_name 链 + 按名 dedup most-derived-first + make_type_from_name → alloc_array_typed("Std.Type")）
- [x] 3.2 `corelib/mod.rs`：注册 `__type_interfaces`
- [x] 3.3 `Type.z42`：`public extern Type[] GetInterfaces();`

## 阶段 4: 验证
- [x] 4.1 `cargo build` + `cargo test --lib`（含 version-pin）全绿
- [x] 4.2 embedded hello.zbc regen（1.17）+ stdlib clean-rebuild（0.19）+ fixtures regen ×2
- [x] 4.3 golden `src/tests/types/get_interfaces.z42`（单/多/继承/无接口/obj.GetType 一致；interp+jit）
- [x] 4.4 dotnet GoldenTests 全绿（含 ReadWriteRoundTrip TYPE parity + 反射回归）
- [x] 4.5 cargo 集成（隔离 target，绕僵尸锁）+ xtask vm/cross-zpkg/stdlib 全绿
- [x] 4.6 docs reflection.md 同步（用法 + 实现原理 + Deferred 落地标记）+ spec scenarios 覆盖
- [x] 4.7 ACTIVE.md 释放锁 + 归档

## 阶段 5: z42c 同步（延后）
- [ ] 5.1 ⏸ z42c `ZbcWriter.z42` _buildType 镜像接口块 + ZbcFormat 版本 17/19（z42c 锁/WIP 协调后 follow-up；记 memory）

## 备注
- 模式镜像 add-reflection-static-fields（TYPE section 块）+ add-reflection-inherited-static-fields（运行期 base-walk 聚合继承）。
- 数据已在 IrClassDesc.Interfaces（codegen 填充），本变更只接通"持久化 → 载入 → 反射"链路。
- 传递接口实现（interface-extends-interface）延后——需接口 base-interface 图（z42 接口当前不产 TYPE 条目）。
