# Tasks: wave1-assert-script

> 状态：🟢 已完成 | 类型：refactor | 创建：2026-04-27 | 完成：2026-04-27

**变更说明：** Wave 1 第 1 个切片：`Std.Assert` 6 个 `[Native]` 方法迁纯脚本（`if (!cond) throw new Exception(...)`），删除 6 个 `__assert_*` builtin。Assert API 签名 / 抛出行为完全保持不变，仅实现层切换。

**原因：** BCL `Debug.Assert` / Rust `assert!` 都是脚本（C# / 宏展开）。z42 已具备所有依赖原语（`Object.Equals` / `string.Contains` / `throw new Exception` / `$"..."` 字符串插值），无任何阻塞。落地审计表 Wave 1 的最小切片以验证模式。

**文档影响：**
- `src/libraries/README.md` 审计表更新：6 个 `__assert_*` 标记为已迁出 + Wave 进度表

## Tasks

- [x] 1.1 重写 `src/libraries/z42.core/src/Assert.z42`：6 个方法去 `[Native]` + 给出脚本实现
- [x] 2.1 删除 `src/runtime/src/corelib/mod.rs` 中 6 个 `__assert_*` dispatch 注册 + 添加迁移注释
- [x] 2.2 删除 `src/runtime/src/corelib/object.rs` 中 6 个 `builtin_assert_*` 函数及 "── Assert ──" 区块（含未用 import 清理）
- [x] 3.1 更新 `src/libraries/README.md` 审计表：Assert 行从 🟡 → ✅ 已迁出；汇总数字 (Wave 1: 19→13, 已迁: 6) + Wave 进度行
- [x] 4.1 `./scripts/build-stdlib.sh` + `cp dist/*.zpkg → artifacts/z42/libs/` —— stdlib 同步到 VM 加载路径
- [x] 4.2 `./scripts/regen-golden-tests.sh` —— 96 ok（清缓存后）
- [x] 4.3 `cargo build` 与 `dotnet build` —— 全绿
- [x] 4.4 `dotnet test` —— 717 passed, 0 failed
- [x] 4.5 `./scripts/test-vm.sh` —— 188 passed (94 interp + 94 jit), 0 failed
- [ ] 5.1 commit + push
- [ ] 5.2 归档到 `spec/archive/2026-04-27-wave1-assert-script/`

## 备注

- 等价性策略：脚本实现的错误消息保持与原 builtin 一致（`"AssertionError: expected ... but got ..."` / `"AssertionError: expected ... to contain ..."`），避免 golden test diff
- `Equal(object expected, object actual)` 的实现：用 `expected.Equals(actual)`（依赖 `Object.Equals` virtual 方法走 `__obj_equals`）
- `Null` / `NotNull` 参数为 `object?`，用 `value == null` 直接判定
- **dev 流程发现**：`build-stdlib.sh` 只写 `artifacts/libraries/<lib>/dist/`，VM 加载 `artifacts/z42/libs/`。需要 `cp` 同步或跑 `package.sh`。已记入 backlog（建议后续把同步加进 build-stdlib.sh 或调整 VM search path 把 `artifacts/libraries/<lib>/dist/` 也纳入）
- **增量缓存陷阱**：首次 build 后 regen 出现 5 个 IEquatable/IComparable/INumber 约束错误。清 `artifacts/libraries/*/cache` 后重建即恢复。可能与 C5 source_hash 增量编译的 cache invalidation 有关，但出错场景是"删 [Native] 改 script"这种较少见的修改类型，留给 incremental-build-cache 后续观察
