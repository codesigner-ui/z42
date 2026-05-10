# Proposal: Implement Tier 1 C ABI Runtime

## Why

C1 (`design-interop-interfaces`) 锁定了 Tier 1/2/3 所有公开接口但**无运行时行为**：4 个新 IR opcode 在 VM 里 trap，3 个 `z42_*` C 函数尚未实现，没有任何 native 库可以真正注册类型。

C2 把 **Tier 1 这一层**完整接入 VM 运行时：
- 实现 `z42_register_type` / `z42_resolve_type` / `z42_invoke` / `z42_invoke_method` / `z42_last_error` 五个 extern 函数
- VM 内置 `TypeRegistry`（住在 `VmContext`）持有注册表
- `CallNative` IR opcode 走 libffi 调度调用 native 函数，**取代 C1 trap**
- 错误码 Z0905 / Z0906 / Z0910 落地真实抛出点
- 端到端 PoC：一个用 C 写的 `numz42-c` 库（Counter 类型 + new/inc/get），从 VM 注册并调用，全流程跑通

C3/C4/C5 都建立在 C2 之上。**用户面向的 syntax 仍未引入**（[Native]/extern class/import 等推迟到 C5）；本 spec 只让接口骨架"通电"，测试通过手工构造 zbc + integration test 验证。

## What Changes

- **新增模块** `src/runtime/src/native/`：TypeRegistry、Z42Value/Z42Args marshal、libffi cif 缓存、dlopen 加载器、thread_local 错误槽、`z42_*` extern 函数体
- **VmContext 扩展**：加 `native_types: Rc<RefCell<HashMap<(String,String), Arc<RegisteredType>>>>`
- **Interp dispatch**：`Instruction::CallNative` 分支从 `bail!("not implemented")` 改为 marshal → libffi `call` → unmarshal 真实流程
- **依赖**：`libffi = "3.2"`、`libloading = "0.8"` 进 z42_vm 运行时依赖
- **PoC**：`tests/data/numz42-c/numz42.c` + build script，Counter 类型 + 3 方法，注册函数 `numz42_register(VmCtx*)` 由测试代码手动调
- **集成测试**：`tests/native_interop_e2e.rs` 手工构造 `CallNative` 字节码 → VM 执行 → 验证 Counter inc/get 行为
- **文档同步**：`docs/design/interop.md` §10 标 C2 ✅；`docs/design/error-codes.md` 把 Z0905/Z0906/Z0910 从"占位"改为"已启用"+ 抛出条件
- **新单元测试**：registry / marshal 各自的 `*_tests.rs`

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/Cargo.toml` | MODIFY | 加 `libffi = "3.2"`、`libloading = "0.8"` |
| `src/runtime/src/lib.rs` | MODIFY | 暴露 `pub mod native;` |
| `src/runtime/src/native/mod.rs` | NEW | 模块入口；re-export 公开 API |
| `src/runtime/src/native/registry.rs` | NEW | `RegisteredType` + `TypeRegistry` 实现 |
| `src/runtime/src/native/registry_tests.rs` | NEW | TypeRegistry CRUD 单元测试 |
| `src/runtime/src/native/marshal.rs` | NEW | `Z42Value` ↔ z42 `Value` 双向转换 |
| `src/runtime/src/native/marshal_tests.rs` | NEW | marshal 往返测试 |
| `src/runtime/src/native/dispatch.rs` | NEW | libffi `cif` 缓存 + `call_method` |
| `src/runtime/src/native/dispatch_tests.rs` | NEW | dispatch 单元测试（用本进程函数指针，不用 dlopen） |
| `src/runtime/src/native/loader.rs` | NEW | `load_library(path)`：`libloading::Library` + 缓存 |
| `src/runtime/src/native/error.rs` | NEW | thread_local `LAST_ERROR` + helpers |
| `src/runtime/src/native/exports.rs` | NEW | `z42_register_type` 等 5 个 `#[no_mangle] pub extern "C" fn` 实现 |
| `src/runtime/src/native/README.md` | NEW | 第 3 层目录 README |
| `src/runtime/src/vm_context.rs` | MODIFY | 加 `native_types` 字段 + `register_native_type()`/`resolve_native_type()` 方法 |
| `src/runtime/src/vm_context_tests.rs` | MODIFY | 新增 native_types 注册/查找 单元测试 |
| `src/runtime/src/interp/exec_instr.rs` | MODIFY | `Instruction::CallNative` 分支：trap → 真实 dispatch；保留 `CallNativeVtable`/`PinPtr`/`UnpinPtr` 的 trap |
| `src/runtime/tests/data/numz42-c/numz42.c` | NEW | PoC：Counter 类型 + new/inc/get 方法 + `numz42_register` 入口 |
| `src/runtime/tests/data/numz42-c/build.sh` | NEW | 编译脚本（cc + dylib），由 build.rs 调用 |
| `src/runtime/build.rs` | NEW | 测试期编译 numz42-c → `target/debug/libnumz42_c.{dylib,so}` |
| `src/runtime/tests/native_interop_e2e.rs` | NEW | 端到端：dlopen → register → CallNative → assert |
| `docs/design/interop.md` | MODIFY | §10 Roadmap C2 → ✅；§3.3 ABI 演进描述补"实现已锁定" |
| `docs/design/error-codes.md` | MODIFY | Z0905 / Z0906 / Z0910 从"占位"改为"已启用"+ 详细抛出条件 |
| `docs/roadmap.md` | MODIFY | Native Interop 表 C2 行 → ✅ + 日期 |
| `docs/design/vm-architecture.md` | MODIFY | "VM 状态" 段补 `native_types` 说明 |

**只读引用**（理解上下文，不修改）：
- `src/runtime/include/z42_abi.h` — Tier 1 ABI 头文件契约
- `src/runtime/crates/z42-abi/src/lib.rs` — Rust 侧镜像类型
- `docs/design/interop.md` §3 — C2 必须实现的 ABI 描述
- `src/runtime/src/vm_context.rs` 现有字段布局 — 学习 `Rc<RefCell>` 模式

## Out of Scope

- **用户面向 syntax**：`[Native]` / `extern class` / `import T from "lib"` —— C5 (`impl-source-generator`)
- **proc macro 实现**：`#[derive(Z42Type)]` —— C3
- **`pinned` 块运行时**：`PinPtr` / `UnpinPtr` 仍 trap —— C4
- **`CallNativeVtable`**：vtable 索引调度 —— C5
- **JIT 后端**：`CallNative` 在 JIT 层仍 `bail!`（与 C1 一致），interp 全绿后 L3.M16 接入
- **AOT**：不动
- **stdlib 迁移**：现有 L1 `[Native("__name")]` 机制保持不变
- **`numz42-rs`** Rust 版 PoC：C3 工作（依赖 derive macro）

## Open Questions

- [ ] **Q1**：dlopen 时机 — VM 启动时全量加载所有声明的 native lib，还是延迟到第一次 `CallNative` 命中？
  - 倾向：**显式 API** `vm.load_native_library(path)`，由测试 / 启动脚本主动调；不做"魔法"自动加载（避免错误隐藏）
- [ ] **Q2**：libffi 还是手写 trampoline？
  - 倾向：**libffi**。手写 trampoline 每个签名一个 thunk，组合爆炸，维护成本高
- [ ] **Q3**：`z42_*` extern 函数怎么访问 VmContext？
  - 倾向：**thread_local `Cell<*const VmContext>`**，进入 z42 解释器时 set，离开时 unset。Native 库 callback 必然是从 z42 代码里发起的，所以线程上一定有 VM 上下文
- [ ] **Q4**：Z42Value 的 `tag` 取值表（C1 留为"opaque"）
  - 倾向定义：`0=Null, 1=I64, 2=F64, 3=Bool, 4=Str, 5=Object, 6=TypeRef`；payload 64-bit 存原始位 / pointer
