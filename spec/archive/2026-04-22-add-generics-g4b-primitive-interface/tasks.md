# Tasks: L3-G4b Primitive 类型实现 interface

> 状态：🟢 已完成 | 创建：2026-04-22 | 完成：2026-04-22 | 类型：feat（TypeChecker + VM 双边，无 zbc 改动）

**变更说明**：让 primitive 类型（int/double/bool/char/string）编译期被视为实现了 `IComparable<T>` / `IEquatable<T>`，并在 VM 层把 VCall on primitive 分派到 corelib builtin。让 `Max<int>(3, 5)` 真正可用。

**Scope**：
- Rust corelib：新增 `__int_compare_to` / `__int_equals` / `__int_hash_code` / `__int_to_string` 等 16 个 primitive builtins
- Rust VM：`interp::exec_instr::primitive_method_builtin` 映射表；interp + JIT VCall 路径共用
- C# TypeChecker：`PrimitiveImplementsInterface(primName, ifaceName)` 硬编码表，接入 `TypeSatisfiesInterface`
- 无 IR / zbc 格式改动

## 完成项

### Rust 侧
- [x] `corelib/convert.rs`：`builtin_int_compare_to` / `builtin_int_equals` / ... / `builtin_str_compare_to`（16 个函数 + 4 个 require_* helper）
- [x] `corelib/mod.rs`：DISPATCH 注册 16 个新 builtin
- [x] `interp/exec_instr.rs`：`primitive_method_builtin` helper + VCall 分派优先走 primitive
- [x] `jit/helpers_object.rs`：VCall helper 同步 primitive 分派分支（复用 interp::exec_instr::primitive_method_builtin）
- [x] `interp/mod.rs`：exec_instr 模块 `pub(crate)` 以便 jit 复用

### C# 侧
- [x] `TypeChecker.cs`：`PrimitiveImplementsInterface` — int/long/short/... + float/double + string/char 满足 IComparable + IEquatable；bool 仅 IEquatable
- [x] `TypeSatisfiesInterface` 分派 `Z42PrimType` + `Z42InstantiatedType`

### 测试
- [x] `TypeCheckerTests.cs`：4 新用例（int IComparable / string IComparable / bool IComparable 报错 / bool IEquatable 通过）
- [x] Golden `run/75_generic_primitive_interface`：Max<int> / Max<string> / Max<double> + Eq<int/string/bool>
- [x] `dotnet test` 519/519 ✅; `test-vm.sh` 144/144 ✅ (interp 72 + jit 72)

### 文档
- [x] `docs/design/generics.md`: L3-G4b 小节
- [x] `docs/roadmap.md`: L3-G4b ✅；L3-G4c（List/Dict 源码化）保留 📋

## 备注

- 无 zbc 版本 bump（纯 VCall 运行时分派 + TypeChecker 规则扩展）
- L3-G4c（将 pseudo-class List/Dict 改写为 z42 源码泛型类）是独立后续迭代，不阻塞本次
- Scope 外发现：无
