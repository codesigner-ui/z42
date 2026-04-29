# Tasks: External Root Scanner for Cycle Collector (Phase 3d.1)

> 状态：🟢 已完成 | 创建：2026-04-29 | 完成：2026-04-29 | 类型：vm（**bugfix**）

## Bug 修复说明

Phase 3c 的 trial-deletion `mark_reachable_set()` 只扫 `RcMagrGC.pinned_roots`，
**不扫** `VmContext.static_fields` 和 `pending_exception`。

具体场景（典型 z42 用法）：`class Foo { static List<X> Cache; }` 中 Cache 含
cyclic 对象时，collect_cycles 把它们标 unreachable → 内部引用相互抵消使
`tentative <= 0` → 算法清空它们的 slots → **可达对象数据丢失**。

## 修复实施

### 阶段 1: VmContext 字段改 Rc<RefCell<...>> ✅
- [x] `static_fields`: `RefCell<HashMap>` → `Rc<RefCell<HashMap>>`
- [x] `pending_exception`: 同改
- [x] `lazy_loader`: 同改（一致性）
- [x] accessors（`static_get` / `static_set` / `take_exception` / `set_exception` /
      `install_lazy_loader_*` / `try_lookup_*`）通过 Rc deref 自动适配
- [x] 现有 vm_context_tests 全绿（行为零变化）

### 阶段 2: RcMagrGC 加 scanner ✅
- [x] `external_root_scanner: RefCell<Option<ExternalRootScanner>>` 字段
- [x] `type ExternalRootScanner = Box<dyn Fn(&mut dyn FnMut(&Value))>` 类型别名
- [x] `pub fn set_external_root_scanner(&self, scanner: ExternalRootScanner)` 方法
- [x] Debug impl 加 `external_scanner: bool`
- [x] `mark_reachable_set` 在扫完 pinned roots 后调 scanner 喂 visit 进 queue

### 阶段 3: VmContext::new 注入 scanner ✅
- [x] 先构造 concrete `RcMagrGC::new()`（boxed 前）
- [x] clone Rc 给闭包（sf, pe），调 `heap.set_external_root_scanner(Box::new(...))`
- [x] 然后 `Box::new(heap)` 装到 `Box<dyn MagrGC>`

### 阶段 4: 测试 ✅（3 个新测试）
- [x] `external_root_scanner_called_during_collect` —— scanner 被调用 ≥ 1 次
- [x] `cycle_reachable_via_external_scanner_is_preserved` —— 关键测试：模拟
      static_fields 持 cycle，collect 后对象 slots intact
- [x] `cycle_unreachable_from_external_scanner_still_collected` —— 没在
      external 里的 cycle 仍被正确收集（不会"过度保护"）

### 阶段 5: 文档同步 ✅
- [x] `gc/rc_heap.rs` 模块文档：限制 #3 缩到"interp/JIT 栈帧"，static_fields 已对接
- [x] `gc/README.md` 同步 + 加 Phase 3d.1 完成说明
- [x] `docs/design/vm-architecture.md` 同步限制 + Phase 路线表加 3d.1
- [x] `gc/mod.rs` Phase 路线表加 3d.1

### 阶段 6: 验证 ✅

**编译状态**：
- ✅ `cargo build` 0 错误 0 警告
- ✅ `dotnet build` 0 错误（已建好）

**测试结果**：
- ✅ Rust unit tests: **138/138 通过**（+3 Phase 3d.1 测试）
- ✅ Rust integration tests (`zbc_compat`): **4/4**
- ✅ `dotnet test`: **740/740**
- ✅ `./scripts/test-vm.sh`: **interp 101 + jit 101 = 202/202**

### 结论：✅ 全绿，可以归档
