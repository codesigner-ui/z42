# Tasks: IsAssignableFrom / GetInterface + FQ 接口身份

> 状态：🟢 已完成 | 创建：2026-06-16 | 完成：2026-06-16 | 类型：ir（接口名 bare→FQ wire 语义 + VM 判定扩展 + zbc bump，完整流程）

## 阶段 1: compiler
- [x] 1.1 `IrGen.Classes.cs`：`InterfaceTypeName` → `QualifyClassName`（接口块存 FQ 名）
- [x] 1.2 `ZbcWriter.cs`：`VersionMinor` 19→20 + 注释
- [x] 1.3 `ZpkgWriter.cs`：`VersionMinor` 21→22 + 注释
- [x] 1.4 dotnet build 编译器：0 error

## 阶段 2: runtime
- [x] 2.1 `zbc_reader.rs`：`ZBC_VERSION_MINOR` 20 / `ZPKG_VERSION_MINOR` 22
- [x] 2.2 `zbc_reader_tests.rs`：version-pin 20/22
- [x] 2.3 `dispatch.rs`：`is_subclass_or_eq_td` base 链每层查 `interfaces()` FQ 名
- [x] 2.4 `reflection.rs`：`builtin_type_is_assignable_from`（复用 is_subclass_or_eq_td）
- [x] 2.5 `corelib/mod.rs`：注册 `__type_is_assignable_from`
- [x] 2.6 cargo build（debug+release）：0 error

## 阶段 3: stdlib
- [x] 3.1 `Type.z42`：`IsAssignableFrom(Type)` extern + `GetInterface(string)` z42

## 阶段 4: 测试与验证
- [x] 4.1 golden `src/tests/types/assignable_from.z42`（interp+jit；IsAssignableFrom 6 例 + GetInterface 命中/未命中 + 接口 is 生效 + GetInterfaces 真句柄）
- [x] 4.2 zbc/zpkg fixtures regen
- [x] 4.3 dotnet test 全绿（GoldenTests 回归：接口 is 行为变化不破坏既有 + Zbc/Zpkg invariant + round-trip）
- [x] 4.4 cargo build + cargo test --lib 全绿
- [x] 4.5 xtask vm/cross-zpkg/stdlib 全绿（regen 后）

## 阶段 5: docs + 归档
- [x] 5.1 `reflection.md`：IsAssignableFrom/GetInterface 主体 + 接口 FQ/真句柄 + 接口 is 生效 + API 表 + Deferred
- [x] 5.2 `zbc.md` 1.20 + `zpkg.md` 0.22 changelog
- [x] 5.3 ACTIVE.md 释放锁 + 归档
- [x] 5.4 z42c writer 同步 follow-up（接口块 FQ 名；byte-identical gate 暂红）

## 阶段补充：JIT `is` 镜像（Scope 内）
- 接口 `is`/`as` 有两条 VM 路径：interp `is_subclass_or_eq_td`（dispatch.rs）+ JIT `is_subclass_or_eq`（jit/helpers/object.rs）。两者都只走 base 链不查接口，均扩展（golden jit 模式触发暴露 JIT 路径）。

## 备注
- 真实类型身份比较（复用 is_subclass_or_eq_td），不比合成 Type 字符串。
- 接口块 bare→FQ：顺带 GetInterfaces 返真句柄 + 接口 `is`/`as` 生效。
- 传递接口（接口继承接口）/ 泛型变体 / handle-less 装箱语义 / z42c 同步 延后。
