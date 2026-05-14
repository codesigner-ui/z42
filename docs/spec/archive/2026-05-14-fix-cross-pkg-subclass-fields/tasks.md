# Tasks: Cross-zpkg subclass field inheritance fix

> 状态：🟢 已完成 | 创建：2026-05-14 | 完成：2026-05-14 | 类型：vm（完整流程）

## 进度概览

- [x] 阶段 1: TypeDesc 字段拆分（own_fields / own_methods 显式存储）
- [x] 阶段 2: `build_type_registry` 拆出 `merge_with_base` + `try_fixup_inheritance`
- [x] 阶段 3: `LazyLoader::load_zpkg_file` 调 fixup 直到 fixed-point + 主入口 seed_lazy_loader_types
- [x] 阶段 4: 访问点无须迁移（保留 `Vec`/`HashMap` 直接访问；Arc::get_mut 路径）
- [x] 阶段 5: Rust 单测（4 个 fixup 测试）+ z42 端到端（所有 z42.io 测试全绿）
- [x] 阶段 6: 文档（vm-architecture.md 两阶段类型加载段）
- [x] 阶段 7: GREEN 验证 + 归档 + commit + push

## 阶段 1: TypeDesc 字段拆分

- [ ] 1.1 MODIFY [src/runtime/src/metadata/types.rs](../../../../src/runtime/src/metadata/types.rs)
  - 加 `own_fields: Vec<FieldSlot>`（迁出当前 `fields` 的来源数据）
  - 加 `own_methods: Vec<MethodEntry>`（类型暂用 `(method_name, fq_func_name)` tuple；正式叫法看 vtable 现有结构）
  - 把 `fields` / `field_index` / `vtable` / `vtable_index` 改成 `OnceLock<...>`
  - 加 accessor 方法 `fields()` / `field_index()` / `vtable()` / `vtable_index()`，未填则 panic 带可定位 message
- [ ] 1.2 `cargo build` 通过（必然 break；先看 compile errors 数量，决定下一步拆分粒度）

## 阶段 2: `build_type_registry` 重构

- [ ] 2.1 MODIFY [src/runtime/src/metadata/loader.rs](../../../../src/runtime/src/metadata/loader.rs)
  - `build_type_registry` 改成只填 `own_fields` / `own_methods`，**不**填 merged 视图
  - 新函数 `try_fixup_inheritance(targets, global_registry) -> usize` 实现 design.md 的逻辑
  - 新私有函数 `compute_merged_layout(td, registry) -> Option<MergedLayout>` —— None = base 链未完成
  - `build_type_registry` 末尾调一次 `try_fixup_inheritance` 用 module 自身 registry 作 global —— 处理同 zpkg 子类
- [ ] 2.2 单元测试（loader_tests.rs）：单 module 子类（base + sub 同 module）fixup 后 `fields` 含 base 字段，`field_index` 正确

## 阶段 3: `LazyLoader::register_zpkg` 调 fixup

- [ ] 3.1 MODIFY [src/runtime/src/metadata/lazy_loader.rs](../../../../src/runtime/src/metadata/lazy_loader.rs)
  - 在 `load_zpkg_file` 内部 `type_registry.insert(...)` loop 之后加 fixed-point loop
  - 循环：`while try_fixup_inheritance(self.type_registry.values(), &self.type_registry) > 0 {}`
- [ ] 3.2 单元测试（lazy_loader_tests.rs 若不存在则新建）：
  - 构造两个 fake zpkg（A 含 Base，B 含 Sub : Base）；按 A → B 顺序 register；assert `Sub.field_index` 含 base 字段
  - 按 B → A 顺序 register；assert 同样结果（验证 deferred fixup 命中）

## 阶段 4: 访问点迁移

- [ ] 4.1 `cargo build` 列出所有 `td.fields` / `td.field_index` / `td.vtable` / `td.vtable_index` 直接访问点
- [ ] 4.2 逐点改为 accessor 方法调用（`td.fields()` 等）；推荐用 sed 批量改 + 人工 review
- [ ] 4.3 `cargo build` 通过；`cargo test --release --lib` 全绿（既有测试不应回归）

## 阶段 5: 端到端测试 + 回归测试

- [ ] 5.1 NEW [src/libraries/z42.io/tests/exception_subclass.z42](../../../../src/libraries/z42.io/tests/exception_subclass.z42)
  - 复现 add-std-process 触发的 case：声明一个 `MyExc : Exception` 在 z42.io 中，`new MyExc("x").Message == "x"` 必须为 true
  - 三层链测试：`Sub : Mid : Base`，跨 zpkg
- [ ] 5.2 `./scripts/test-stdlib.sh` 全绿（22 个 process 失败应只剩 0-2 个；非 cross-zpkg-field 原因留独立 issue）
- [ ] 5.3 `./scripts/test-all.sh` 全绿 baseline 不变（43 个 pre-existing VM 失败仍然是 pre-existing，不应新增也不应减少）

## 阶段 6: 文档同步

- [ ] 6.1 MODIFY [docs/design/runtime/vm-architecture.md](../../../design/runtime/vm-architecture.md)
  - 加 "类型加载二阶段" 段：skeleton at module load + fixup at lazy-load merge
  - 描述 own_fields vs fields/field_index 的区分；OnceLock 的并发语义（单线程，幂等）
  - 解释 deferred fixup（base 未到位时延迟）
- [ ] 6.2 MODIFY [src/runtime/src/metadata/loader.rs] 顶部 doc 注释
  - 说明 `build_type_registry` 现只填 own_fields；merged 视图由 fixup 负责

## 阶段 7: GREEN + 归档

- [ ] 7.1 `./scripts/test-all.sh` 6 stage 全绿（除 pre-existing baseline）
- [ ] 7.2 spec scenarios 逐条覆盖确认（spec.md "ADDED Requirements"）
- [ ] 7.3 tasks.md 状态 → 🟢 已完成 + 完成日期
- [ ] 7.4 mv `docs/spec/changes/fix-cross-pkg-subclass-fields/` → `docs/spec/archive/2026-05-14-fix-cross-pkg-subclass-fields/`
- [ ] 7.5 commit: `fix(vm): cross-zpkg subclass field inheritance — two-phase TypeDesc fixup`
- [ ] 7.6 push origin main
- [ ] 7.7 恢复 add-std-process spec（解除 blocker），完成阶段 5-7

## 实施期记录

- **决策 1 调整（own_fields 而非 own_start）**：design.md Decision 1 最初想用 single usize 偏移；实施时改为显式 `own_fields: Vec<FieldSlot>` + `own_methods: Vec<(String, String)>`，因为多级链 fixup 需要清晰可查的"我自己声明的"集合，省去推断成本。增加 ~48 字节 per TypeDesc，可接受。
- **决策 2 调整（直接 Vec/HashMap 而非 OnceLock）**：design.md Decision 2 选 OnceLock 以避免互斥锁。实施时改为保留 `Vec`/`HashMap` 直接字段，用 `Arc::get_mut` 在 fixup 阶段（types 还未被任何对象实例化时）独占 mutate。优势：避免**全代码库**把 `td.fields` → `td.fields()` accessor 迁移（估计 60+ 处），改动半径从 "wide" 缩到 "loader + lazy_loader 局部"。
- **`module.type_registry_vec.clear()` 必需**：lazy_loader 把 zpkg 的 type_registry 移入 self 后，原 module 的 type_registry_vec 仍持有 Arc 副本（strong_count=2），导致 `Arc::get_mut` 失败。在 fixup 前必须 clear。否则 fixup 静默跳过（只 warn），子类字段仍丢失。
- **`needs_fixup` 必须按 method simple_name 去重**：`own_methods` 含 overload（`Foo$1` / `Foo$2` 都 simple_name = `Foo` → 同 vtable 槽）。原始按总条数计 expected_vtable_count 会让 overload 类型永远 needs_fixup=true → 固定点循环不收敛。修正：按 simple_name `HashSet` 去重计数。
- **`seed_lazy_loader_types` 入口**：lazy_loader 初始 type_registry 为空。eager-loaded module（z42.core + 用户测试合并）的 TypeDesc 通过新方法克隆进来，让 fixup 时跨 zpkg base 可见。两个入口同时调（main.rs CLI + test-runner bootstrap）。
- **`merge_modules + build_type_registry` 路径不影响**：test-runner 把 z42.core + user 合并为单 module 后调 `build_type_registry`，此路径下 Std.Exception 在本 module registry 内，subclass 已正确继承 —— fixup 走 needs_fixup=false 跳过路径，零开销。

## 备注

### 与 add-std-process 的关系

- 本 spec 是 add-std-process 的 blocking 前置（add-std-process 阶段 5-7 触发了本 bug）
- 本 spec 落地后，add-std-process 的 22 个 process 测试失败应消除大多数；剩余可能涉及具体 stdio / timeout 行为，与本 fix 无关
- add-std-process 当前 WIP 文件（process.rs / Process.z42 / 等）保持磁盘上不动，本 spec 仅修 loader / lazy_loader，不影响 process 文件

### 风险监控

- **JIT 缓存的 field slot index**：若 JIT 在 fixup 前编译并缓存了 field slot index，fixup 改了 TypeDesc 后 JIT 缓存可能失效。L2 stdlib 路径目前全 interp，风险低；JIT enable 后需独立验证（独立 spec）
- **回归测试覆盖**：必须确保现有同 zpkg 继承（如 InvalidOperationException : Exception 在 z42.core 内）不变。靠 5.2/5.3 兜底
- **OnceLock 改动半径**：types.rs 的 TypeDesc 字段类型变化会触发跨 crate 的 compile error。先评估改动范围（阶段 4.1）；若超出 60 个改动点，拆 prep refactor 单独 commit
