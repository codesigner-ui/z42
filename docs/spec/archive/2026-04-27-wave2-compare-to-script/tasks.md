# Tasks: wave2-compare-to-script

> 状态：🟢 已完成 | 类型：refactor | 创建：2026-04-27 | 完成：2026-04-27

**变更说明：** Wave 2：5 个 primitive 的 `CompareTo` 方法（int / long / double / float / char）从 `[Native]` 迁纯脚本，删除 3 个 `__*_compare_to` builtin。

**原因：** Rust `i32::cmp` / `f64::partial_cmp` 等都是 pure Rust（不是 intrinsic）。z42 已有 `<` / `>` 比较运算符，写成 `if (this < other) return -1; if (this > other) return 1; return 0;` 与 Rust 等价 —— 包括 NaN 边界（`<` / `>` 对 NaN 都返回 false → 落到第三行 return 0，匹配 `partial_cmp` 的 `unwrap_or(0)` 行为）。

**原审计表分类**：🔵 codegen 特化。但实测纯脚本足够 —— `<` / `>` 已 codegen 为 IR 比较 + 跳转指令，无需在 IrGen 加新识别逻辑。归并到 Wave 1 同款"纯脚本"。

## Tasks

- [x] 1.1 `Int.z42`：`CompareTo(int)` 改脚本（int < / > 比较）
- [x] 1.2 `Long.z42`：`CompareTo(long)` 改脚本
- [x] 1.3 `Double.z42`：`CompareTo(double)` 改脚本（NaN 自动返回 0）
- [x] 1.4 `Float.z42`：`CompareTo(float)` 改脚本
- [x] 1.5 `Char.z42`：`CompareTo(char)` 改脚本
- [x] 2.1 `corelib/mod.rs`：删 3 行 `__int_compare_to` / `__double_compare_to` / `__char_compare_to` dispatch + 注释
- [x] 2.2 `corelib/convert.rs`：删 3 个 `builtin_*_compare_to` 函数
- [x] 3.1 `src/libraries/README.md` 审计表：3 个 compare_to 行 🔵 → ✅；Wave 进度
- [x] 4.1 build-stdlib + regen golden + dotnet test + test-vm 全绿
- [x] 5.1 commit + push + 归档

## 备注

- **NaN 行为**：`if (NaN < x) return -1` (false) → `if (NaN > x) return 1` (false) → `return 0`. 与 Rust `f64::partial_cmp(NaN).map(o as i64).unwrap_or(0)` 等价
- **不动 `__str_compare_to`**：保留 `🟢 Object 协议 ABI` 分类（字符串 lex 比较语义复杂，不适合脚本）
- **char 不支持 `<`/`>`**：实施时发现 TypeChecker 拒绝 `char < char`（"operator requires numeric operand"）。Workaround：用 `GetHashCode()` 拿 codepoint 整数后再比较。Backlog：让 TypeChecker 接受 char 比较（小补丁，独立 spec）
- **builtin 总数突破 ~45 长期目标**：Wave 0/1/2 累计删 32 个，从 ~80 → ~45，达成 BCL/Rust 标杆
