# Tasks: fix-z42c-load-fixup-loop

> 状态：🟢 已完成 | 创建：2026-06-15 | 完成：2026-06-15

**变更说明：** 修复 runtime `LazyLoader::load_zpkg_file` 加载含**重复字段名**的类时
`try_fixup_inheritance` 定点循环永不收敛 → 100% CPU 死循环（阻塞全部 z42c 自举：
driver / 测试 / e2e 全 hang）。

**原因：** `needs_fixup` 按**带重数**计 own field（base + 全部 own-not-in-base），而
`merge_with_base` 按 **distinct 名**去重（一个对象不能有两个同名槽）。类一旦带重复字段名
（如 z42c.semantics `CompiledModuleZ` 的复制粘贴 `DiagMsgs`/`DiagCount`，12 own/10 distinct），
两者永久 `10≠12` → 每轮 `newly_fixed>0` → `loop{}` 不退出。`sample` 火焰图实证：
`load_zpkg_file → loop{try_fixup_inheritance} → hashbrown reserve_rehash`（NameIndex 无限重建）。
触发源（`CompiledModuleZ` 重复字段声明）单独由 [[fix-z42c-irdump-gate-bugs]] 清理。

**根因层级**：① VM 不得因畸形元数据死循环（本变更：loader 健壮性）；② z42c 源不该有重复字段
（irdump 变更）；③ C# bootstrap 编译器静默接受重复字段声明 → backlog（compiler-architecture.md
Deferred `reject-duplicate-field-decl`）。

**文档影响：** vm-architecture.md 加载策略段补「fixup 收敛按 distinct 字段名 + 防御性 cap」。

## 任务
- [x] 1.1 `loader.rs::needs_fixup`：own field count 改 distinct 名（HashSet `seen_f`），与 merge 一致 → 收敛
- [x] 1.2 `lazy_loader.rs`：fixed-point loop 加防御性 cap（`registry.len()+8` 轮）+ `tracing::error` 非静默兜底（任何未来不收敛 → 报错而非 hang）
- [x] 1.3 `loader.rs::unconverged_type_names` 辅助（cap 错误信息可执行）
- [x] 1.4 回归测试 `loader_tests.rs::fixup_converges_with_duplicate_field_names`
- [x] 2.1 cargo test fixup 5/5 + cargo lib 765/0（无回归）
- [x] 2.2 z42c driver 不再 hang；`xtask test compiler-z42` 全绿；vm 183/0 interp + jit；cross-zpkg 2/2
- [x] 2.3 仅 stage runtime 三文件（loader.rs/lazy_loader.rs/loader_tests.rs）

## 备注
- runtime 锁名义属并行 `add-reflection-generic-predicates`；User 2026-06-15 明确授权修此死循环；本变更仅触 fixup 收敛逻辑，与并行的 reflection builtin 增量零文件重叠。
- ② 源重复字段 + ③ 编译器拒绝 = 见关联变更/backlog。
