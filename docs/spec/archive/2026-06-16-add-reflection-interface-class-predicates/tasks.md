# Tasks: Type.IsClass / Type.IsInterface

> 状态：🟢 已完成 | 创建：2026-06-16 | 完成：2026-06-16 | 类型：ir（flags 扩位 + 接口产 TYPE 条目 + zbc/zpkg bump，完整流程）

## 进度概览
- [x] 阶段 1: compiler（IrClassDesc + EmitInterfaceDesc + flags + bump）
- [x] 阶段 2: runtime（flag 常量 + builtins + 版本）
- [x] 阶段 3: stdlib
- [x] 阶段 4: 测试与验证
- [x] 阶段 5: docs + 归档

## 阶段 1: compiler
- [x] 1.1 `IrModule.cs`：`IrClassDesc` 加 `bool IsInterface = false`
- [x] 1.2 `IrGen.Classes.cs`：`EmitInterfaceDesc(InterfaceDecl)`（IsInterface+IsAbstract，无 base/字段，带 TypeParams）
- [x] 1.3 `IrGen.Generate.cs`：`classes` 追加 `cu.Interfaces.Select(EmitInterfaceDesc)`
- [x] 1.4 `ZbcWriter.cs`：flags byte `| (IsInterface ? 16 : 0)`；`VersionMinor` 18→19 + 注释
- [x] 1.5 `ZbcReader.cs`：flags bit4 → `IrClassDesc.IsInterface`（round-trip）
- [x] 1.6 `ZpkgWriter.cs`：`VersionMinor` 20→21 + 注释
- [x] 1.7 dotnet build 编译器：0 error

## 阶段 2: runtime
- [x] 2.1 `bytecode.rs`：`CLASS_FLAG_INTERFACE: u8 = 1 << 4`
- [x] 2.2 `zbc_reader.rs`：`ZBC_VERSION_MINOR` 19 / `ZPKG_VERSION_MINOR` 21
- [x] 2.3 `zbc_reader_tests.rs`：version-pin 19/21
- [x] 2.4 `reflection.rs`：`builtin_type_is_interface` + `builtin_type_is_class`
- [x] 2.5 `corelib/mod.rs`：注册 2 新 builtin
- [x] 2.6 cargo build：0 error

## 阶段 3: stdlib
- [x] 3.1 `Type.z42`：`IsClass` / `IsInterface` extern bool getter

## 阶段 4: 测试与验证
- [x] 4.1 golden `src/tests/types/interface_class_predicates.z42`（interp+jit）
- [x] 4.2 zbc/zpkg fixtures regen
- [x] 4.3 dotnet test 全绿（含 GoldenTests 回归 + Zbc/Zpkg invariant + round-trip）
- [x] 4.4 cargo build（debug+release）+ cargo test --lib 全绿
- [x] 4.5 xtask vm 362/0（interp 185+jit 177）/ cross-zpkg 2/0 / stdlib 272·22

## 阶段 5: docs + 归档
- [x] 5.1 `reflection.md`：IsClass/IsInterface 主体节 + 接口产条目说明 + Deferred（IsEnum / 接口成员 / 数组 IsClass）
- [x] 5.2 `zbc.md` 1.19 + `zpkg.md` 0.21 changelog
- [x] 5.3 `roadmap.md` Deferred Index 更新（IsEnum）
- [x] 5.4 ACTIVE.md 释放锁 + 归档
- [x] 5.5 z42c writer 同步 follow-up（flags bit4 + 接口产条目；byte-identical gate 暂红）

## 阶段 4 备注：根因修（Scope 扩展，compiler 锁内）
- 接口产 TYPE 条目后 `typeof(IFoo)` 仍 IsInterface=false：codegen `Z42TypeName` 不处理 `Z42InterfaceType`→落 `ToString()` 产**未限定**名，与接口条目 FQ 名不匹配 → make_type_from_name 漏句柄。加 `Z42InterfaceType => QualifyClassName(Name)` 修复。
- dotnet `StdlibSidecarPairingTests` 一度假红 = 孤儿 test zpkg（无 zsym），`rm -rf artifacts/build/libraries/*/release/tests` 后 2/2。

## 备注
- IsEnum 延后（enum 类型实体设计，独立 change + design doc）。
- 接口成员枚举（typeof(IFoo).GetMethods）+ 接口继承接口（transitive）延后。
- 数组 IsClass=true（C# 语义）延后（z42 数组 name-only）。
- z42c writer 同步延后（z42c 锁被 port-z42c-self-compile 占）。
