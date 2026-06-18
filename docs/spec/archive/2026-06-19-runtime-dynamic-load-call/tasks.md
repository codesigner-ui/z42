# Tasks: runtime 动态加载 + 静态调用

> 状态：🟢 已归档 2026-06-19（接口已落地；VM 实现明确延后——前置：反射 MVP + 编译器自举） | 创建：2026-06-18
> 类型：vm/runtime（完整流程）。子系统锁：runtime + stdlib。

## 进度概览
- [x] 阶段 0: 可行性验证(builtin 重入 VM 调 z42 函数)
- [ ] 阶段 1: `__load_zpkg`（**延后**，见下）
- [ ] 阶段 2: `__call_static`（**延后**，见下）
- [x] 阶段 3: stdlib 接口（`Std.Runtime` extern 声明已落地）
- [ ] 阶段 4: 测试与验证（延后）

## 阶段 0: 可行性验证(✅ GO,2026-06-18)
- [x] 0.1 确认 builtin 可重入 VM 调 z42 函数取返回。**GO**:
  - 统一入口 `interp::exec_function(ctx,&module,&func,&args)->ExecOutcome`(`pub(crate)`、`&VmContext` 不可变 → 可嵌套);`run_returning`(mod.rs:68)取返回。
  - builtin 签名 `fn(&VmContext,&[Value])->Result<Value>`(corelib/mod.rs:62)拿 `&VmContext`,无借用冲突。
  - 先例:ObjNew 调 ctor(exec_object.rs:145)、call_indirect 调闭包(exec_call.rs:161)、JIT vcall(jit/helpers/vcall.rs:116)、嵌入 invoke_impl(host/ops.rs:245)全走 exec_function 重入 + 传播异常。
  - 实现细节:跨 zpkg 目标函数的 owning module 取法照搬 call_indirect 跨包路径。

## 阶段 3: stdlib 接口（✅ 已完成）

- [x] 3.1 `z42.core/src/Runtime.z42`:`Std.Runtime.LoadZpkg(string)` + `CallStatic(string, string[])->int` extern（`[Native]`）

## 阶段 1: `__load_zpkg`（⏸ 延后——前置：反射 MVP + 编译器自举完成）

- [ ] 1.1 `z42_host::ZpkgResolver` trait + `VmBuilder::set_zpkg_resolver()`：apphost 注入平台特定 resolver（filesystem / asset / bundle）
- [ ] 1.2 `corelib/runtime_dyn.rs`:`__load_zpkg(name)` builtin —— 按优先级解析（缓存 → resolver → filesystem 搜索）→ bytes → `load_zpkg_bytes`；幂等；错误抛 RuntimeException
- [ ] 1.3 `corelib/mod.rs`:注册 builtin

## 阶段 2: `__call_static`（⏸ 延后——前置：反射 MVP + 编译器自举完成）

- [ ] 2.1 `corelib/runtime_dyn.rs`:`__call_static(fqn, string[])->int` —— `try_lookup_function` 解析 + 签名严格校验 + 重入执行 + 取 int
- [ ] 2.2 `corelib/mod.rs`:注册 builtin

## 阶段 4: 测试与验证（⏸ 延后——前置：阶段 1+2 完成）

- [ ] 4.1 Rust 单测 `runtime_dyn_tests.rs`:ZpkgResolver trait / filesystem 搜索 / call_static 正确+错误签名 / 幂等
- [ ] 4.2 VM e2e `src/tests/dynamic-load-call/load_call/`:load 测试 zpkg → call_static 传参取返回；expected_output
- [ ] 4.3 `cargo build --release` + `cargo test`(runtime 单测,memory:改 runtime 必跑)
- [ ] 4.4 `z42 xtask.zpkg test`(dotnet + vm interp+jit + cross-zpkg + lib 全绿)
- [ ] 4.5 spec 场景逐条覆盖确认
- [ ] 4.6 `docs/design/runtime/` 实现原理同步(动态加载 + builtin 重入调用)

## 备注
- 无 zbc/zpkg 格式 bump(纯 builtin + extern)。
- 阶段 0 是 go/no-go 关口：已通过（GO）。
- **2026-06-19**：用户决策——先落接口 + 文档提交，VM builtin 实现（阶段 1+2）和测试（阶段 4）延后到反射 MVP + 编译器自举完成后接入。stdlib 锁释放；runtime 锁保留（阶段 1+2 需要）。
