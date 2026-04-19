# Tasks: Interpreter 优化 — 函数索引 + Frame.get 快速路径

> 状态：🟢 已完成 | 创建：2026-04-19 | 完成：2026-04-19

**变更说明：** 预建函数名→索引映射消除 O(n) 线性扫描；Frame.get 改用直接索引

**原因：** M7 要求"Interpreter 基础优化：指令 dispatch 效率"

**文档影响：** 无

---

- [x] 1.1 Module 新增 `func_index: HashMap<String, usize>`，loader 加载后预建
- [x] 1.2 exec_instr.rs Call 指令改用 func_index 查找
- [x] 1.3 exec_instr.rs VCall + dispatch.rs 函数查找改用 func_index
- [x] 1.4 Frame.get #[inline(always)] + 直接索引
- [x] 1.5 main.rs merge 后重建 func_index
- [x] 2.1 cargo build + ./scripts/test-vm.sh 128 passed

## 性能数据

200 函数模块 × 5000 轮 = 1M 跨函数调用：
- Before: 0.33s (iter().find() O(n))
- After:  0.17s (HashMap O(1))
- **加速 1.9x**
