# Tasks: Strict OOM Rejection (Phase 3-OOM)

> 状态：🟢 已完成 | 创建：2026-04-29 | 完成：2026-04-29 | 类型：vm（行为升级）

## 完成清单

### 阶段 1: trait 加 set_strict_oom ✅
- [x] `gc/heap.rs::trait MagrGC::set_strict_oom(&self, _enabled: bool) {}` 默认
      no-op（向后兼容）
- [x] 文档说明 strict 模式语义 + 默认关闭

### 阶段 2: RcMagrGC 实现 ✅
- [x] `RcHeapInner.strict_oom: bool` 字段
- [x] RcMagrGC override `set_strict_oom`
- [x] `would_oom_after_alloc(&self, size) -> (bool, limit)` helper
- [x] alloc_object / alloc_array：在 size 已知后、record_alloc 前检查
      would_oom，超则 fire OOM 事件并 `return Value::Null`（GcRef 在此 drop，
      Rc 释放，无副作用）

### 阶段 3: 测试 ✅（6 个新测试）
- [x] `strict_oom_off_by_default_no_rejection` —— 默认行为不变
- [x] `strict_oom_alloc_returns_null_when_over_limit` —— 启用后越限返 Null
- [x] `strict_oom_does_not_bump_stats_or_registry` —— 撤销完整：stats、registry 都不动
- [x] `strict_oom_event_fires_on_rejection` —— OOM observer 仍能感知
- [x] `strict_oom_under_limit_succeeds_normally` —— 未越限正常
- [x] `strict_oom_can_be_disabled_at_runtime` —— 运行时可关闭

### 阶段 4: 文档同步 ✅
- [x] `gc/rc_heap.rs` 模块文档：限制清单清空（GC 主功能完整）+ 加 Phase 3-OOM
      完成段
- [x] `gc/heap.rs` `set_strict_oom` 文档化
- [x] `gc/README.md` 同步 + 加 Phase 3-OOM 完成说明
- [x] `docs/design/vm-architecture.md` Phase 路线表 3-OOM ✅；限制清单清空
- [x] `gc/mod.rs` Phase 路线表
- [x] `docs/roadmap.md` MagrGC 子系统行更新（覆盖全部 Phase）

### 阶段 5: 验证 ✅

**编译状态**：
- ✅ `cargo build` 0 错误 0 警告
- ✅ `dotnet build` 0 错误（已建好）

**测试结果**：
- ✅ Rust unit tests: **148/148 通过**（+6 strict OOM 测试）
- ✅ Rust integration tests: **4/4**
- ✅ `dotnet test`: **743/743**
- ✅ `./scripts/test-vm.sh`: **interp 104 + jit 104 = 208/208**

### 结论：✅ 全绿，可以归档

## GC 子系统至此完整

所有原始限制已解决：
1. 接口完整（MMTk-shape 10 能力组）✅
2. 环回收（Bacon-Rajan trial-deletion）✅
3. Finalizer（cycle + drop 双路径）✅
4. 自动 collect（内存压力触发）✅
5. 全 root coverage（pinned + static_fields + pending_exception + interp/JIT 栈帧）✅
6. snapshot/iterate Full coverage ✅
7. 端到端验证（`Std.GC.*` 脚本 API + 3 个 golden test）✅
8. **OOM 真拒绝（strict 模式可选）**✅

GC 子系统**生产可用**，可继续转 L2 其它 backlog（错误码体系 / 测试基线 /
Workspace 完善 / L3-G 泛型剩余子项等）。
