# Tasks: wave1-bool-script

> 状态：🟢 已完成 | 类型：refactor | 创建：2026-04-27 | 完成：2026-04-27

**变更说明：** Wave 1.2：`bool` primitive 三件套 `Equals` / `GetHashCode` / `ToString` 从 `[Native]` 迁纯脚本，删除 3 个 `__bool_*` builtin。

**原因：** BCL `bool.GetHashCode` 是 `value ? 1 : 0`，`ToString` 是字面量。Rust `bool::Display` 直接 写 `"true"`/`"false"`。z42 已用 Rust 风格小写 ("true" / "false")，保持现有行为。primitive 上的脚本方法可用 `this` 引用值（与 String.z42 已有先例一致）。

## Tasks

- [x] 1.1 重写 `src/libraries/z42.core/src/Bool.z42`：3 方法去 `[Native]` + 脚本实现
- [x] 2.1 `src/runtime/src/corelib/mod.rs`：删 3 行 `__bool_*` dispatch + 注释
- [x] 2.2 `src/runtime/src/corelib/convert.rs`：删 3 个 `builtin_bool_*` 函数（保留 require_bool）
- [x] 3.1 `src/libraries/README.md` 审计表：bool 行 🟡 → ✅；Wave 进度更新
- [x] 4.1 `build-stdlib.sh` + `cp dist/*.zpkg → artifacts/z42/libs/`
- [x] 4.2 `regen-golden-tests.sh`、`dotnet test`、`test-vm.sh` 全绿
- [x] 5.1 commit + push + 归档

## 备注

- `bool.ToString()` 输出 **"true" / "false"**（小写，匹配现有 Rust 行为，与 BCL `"True"/"False"` 不同）
- `this` 在 primitive struct 方法体内引用值本身（参考 String.z42 用法）
