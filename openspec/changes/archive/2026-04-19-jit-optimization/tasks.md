# Tasks: JIT 优化 — Frame 池化 + I64 快速路径

> 状态：🟢 已完成 | 创建：2026-04-19 | 完成：2026-04-19

**变更说明：** JIT frame 复用 Vec<Value>，减少每次函数调用的堆分配；arithmetic/comparison helpers 添加 I64 快速路径

**原因：** M7 要求"JIT 基础优化"

**文档影响：** 无

---

- [x] 1.1 JitFrame: thread-local frame pool（POOL_MAX=32），take/return 复用 Vec
- [x] 1.2 helpers_object.rs: jit_call/jit_obj_new/jit_vcall 使用 frame.recycle()
- [x] 1.3 helpers_arith.rs: add/sub/mul/div/rem/eq/ne/lt/le/gt/ge 添加 I64 快速路径
- [x] 2.1 cargo build + ./scripts/test-vm.sh 128 passed
- [x] 2.2 性能对比（见下）

## 性能数据

### JIT — Fib30 + Sum1M + DeepCall500K（小模块，递归调用密集）

| | 中位数 | 变化 |
|---|--------|------|
| Before | 0.52s | — |
| After | 0.53s | ≈ 持平 |

### JIT — 200 函数 × 5000 轮（大模块，跨函数调用密集）

| | 中位数 | 变化 |
|---|--------|------|
| Before | 0.14s | — |
| After | 0.12s | **14% 快** |

### Interpreter — 10M 纯算术循环

| | 中位数 | 变化 |
|---|--------|------|
| Before | 1.70s | — |
| After | 1.64s | **3.5% 快** |

### 分析

JIT 模式下小模块改善不明显，原因是瓶颈在 extern "C" ABI 调用开销（Cranelift native → Rust helper），I64 快速路径和 frame 池化节省的时间被调用开销掩盖。大模块 14% 提升来自 frame 复用减少堆分配。

进一步的 JIT 优化需要将 I64 算术直接内联为 Cranelift IR（iadd/isub/imul），完全跳过 helper 调用——这是 L3 阶段的工作。
