# Tasks: wave1-math-script

> 状态：🟢 已完成 | 类型：refactor | 创建：2026-04-27 | 完成：2026-04-27

**变更说明：** Wave 1.3：`Math.Abs` / `Max` / `Min` 从 `[Native]` 迁纯脚本，删除 3 个 `__math_*` builtin。

**原因：** BCL `Math.Abs(int)` / `Math.Max(int, int)` 都是 C# 一句 `if`/三元，不需要 native；Rust `i32::abs` / `cmp::max` 同样无 intrinsic。z42 已具备所有依赖语法（if / 三元 / `< 0` / unary minus）。

## Tasks

- [x] 1.1 重写 `src/libraries/z42.math/src/Math.z42`：Abs/Max/Min 各加 int + double 两个 overload，去 [Native]，纯脚本实现
- [x] 2.1 `src/runtime/src/corelib/mod.rs`：删 3 行 `__math_abs/_max/_min` dispatch + 注释
- [x] 2.2 `src/runtime/src/corelib/math.rs`：删 3 个 `builtin_math_abs/_max/_min` 函数
- [x] 3.1 `src/libraries/README.md` 审计表：math abs/max/min 行 🟡 → ✅；Wave 进度 + 总数
- [x] 4.1 `build-stdlib.sh` + `cp dist/*.zpkg → artifacts/z42/libs/`
- [x] 4.2 `regen-golden-tests.sh`、`dotnet test`、`test-vm.sh` 全绿
- [x] 5.1 commit + push + 归档

## 备注

- **保留双类型 dispatch**：原 `__math_abs` 在 Rust 侧通过 `Value::I64`/`F64` 模式匹配同时支持 int 和 double（[Native] declaration 只标 double，但 runtime 接受 int）。tests like `15_math` 用 `Math.Abs(-5)` (int) 期望返回 int 5。脚本必须给 int + double 两套 overload，否则 implicit i32→f64 会让 `Assert.Equal(5, 5.0)` 失败。
- z42 ints (i32 source) 在 VM 用 i64 表示。脚本 `Abs(int x): if (x < 0) return -x;` 不会溢出（`-i32::MIN` 可表示为 i64 正数）。
- 静态方法 overload 已被 codebase 验证（Console.WriteLine 多个签名 + 96_ctor_overload 测试构造函数）。
