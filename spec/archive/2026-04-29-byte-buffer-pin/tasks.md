# Tasks: Array<u8> pin (C10)

> 状态：🟢 已完成 | 完成：2026-04-29 | 创建：2026-04-29

## 阶段 1: VmContext 副表

- [x] 1.1 `vm_context.rs`：加 `pinned_owned_buffers: Rc<RefCell<HashMap<u64, Box<[u8]>>>>` 字段
- [x] 1.2 加 `pin_owned_buffer(Box<[u8]>) -> u64` / `release_owned_buffer(ptr)` 方法

## 阶段 2: PinPtr / UnpinPtr 扩展

- [x] 2.1 `interp/exec_instr.rs::PinPtr`：加 Array 分支扫元素 + 拷贝 + 注册副表
- [x] 2.2 `interp/exec_instr.rs::UnpinPtr`：ArrayU8 case 释放副表条目

## 阶段 3: PoC + e2e

- [x] 3.1 numz42-c：加 `counter_buflen(*const u8, u64) -> u64`，加 method 表条目
- [x] 3.2 native_pin_e2e.rs：加 array pin 单测
- [x] 3.3 native_interop_e2e.rs：加 Array pin → CallNative buflen 测试

## 阶段 4: 文档 + GREEN + 归档

- [x] 4.1 error-codes.md Z0908 加 (e)
- [x] 4.2 interop.md / roadmap.md 加 C10 行
- [x] 4.3 GREEN
- [x] 4.4 归档 + commit
