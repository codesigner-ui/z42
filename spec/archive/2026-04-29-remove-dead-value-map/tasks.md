# Tasks: Remove Dead Value::Map

> 状态：🟢 已完成 | 创建：2026-04-29 | 完成：2026-04-29

## 进度概览
- [x] 阶段 1: 删除 Value variant + 接口
- [x] 阶段 2: 清理消费侧
- [x] 阶段 3: 文档同步
- [x] 阶段 4: 验证

## 阶段 1: 删除 Value variant + 接口
- [x] 1.1 `metadata/types.rs` —— 删除 `Map(...)` variant + `PartialEq` Map arm + 加注 2026-04-29 重构说明
- [x] 1.2 `gc/heap.rs` —— 删除 `alloc_map` trait method
- [x] 1.3 `gc/rc_heap.rs` —— 删除 `alloc_map` impl + 移除 `HashMap` import
- [x] 1.4 `gc/rc_heap_tests.rs` —— 删除 `alloc_map_returns_empty_value_map`，更新 stats 测试改用 alloc_object
- [x] 1.5 `vm_context_tests.rs` —— `two_contexts_heap_isolated` 改用第二次 alloc_array

## 阶段 2: 清理消费侧
- [x] 2.1 `interp/exec_instr.rs` —— ArrayGet / ArraySet / FieldGet 移除 Map 分支
- [x] 2.2 `jit/helpers_object.rs` —— jit_array_get / jit_array_set / jit_field_get 移除 Map 分支
- [x] 2.3 `corelib/io.rs` —— `__len` 移除 Map 分支
- [x] 2.4 `corelib/convert.rs` —— `value_to_str` 改为 exhaustive match（移除 `other =>` 兜底）
- [x] 2.5 移除 `jit/helpers_object.rs` 中已无引用的 `value_to_str` import

## 阶段 3: 文档同步
- [x] 3.1 `gc/README.md` —— 更新 Phase 1 known limits（删除 alloc_map 占位说明）
- [x] 3.2 `docs/design/vm-architecture.md` —— "GC 子系统" 段：trait 形状代码块移除 alloc_map 行；已知限制段加 2026-04-29 重构注释

## 阶段 4: 验证

### 编译状态
- ✅ `cargo build` 0 错误 0 警告
- ✅ `dotnet build` 0 错误（已建好，无需 rebuild）

### 测试结果
- ✅ Rust unit tests: **80/80 通过**（删除 1 个 alloc_map 测试 + 1 个 stats 测试改 alloc_object，净 -2 + 0 = 80）
- ✅ Rust integration tests (`zbc_compat`): **4/4 通过**
- ✅ `dotnet test`: **735/735 通过**
- ✅ `./scripts/test-vm.sh`: **interp 101/101 + jit 101/101 = 202/202**

### Spec 覆盖
| Scenario | 实现位置 | 验证方式 | 状态 |
|----------|---------|---------|------|
| Value::Map variant 不再存在 | `metadata/types.rs` | grep `Value::Map` 全代码库 0 命中 | ✅ |
| alloc_map trait 方法不再存在 | `gc/heap.rs` | `cargo build` 通过（任何旧调用会编译失败）| ✅ |
| 5 处 match arm 全部删除 | interp 3 + jit 3 + corelib/io 1 | grep `Value::Map(rc)` 0 命中 | ✅ |
| value_to_str exhaustive | `corelib/convert.rs` | 删除 `other =>` 后编译期强制覆盖 | ✅ |
| 行为零变化 | golden tests | 202/202 通过 | ✅ |

### Tasks 完成度：4 阶段全部 ✅

### 结论：✅ 全绿，可以归档
