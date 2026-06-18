# Tasks: runtime 动态加载 + 静态调用

> 状态：🟡 进行中 | 创建：2026-06-18
> 类型：vm/runtime（完整流程）。子系统锁：runtime + stdlib。

## 进度概览
- [ ] 阶段 0: 可行性验证(builtin 重入 VM 调 z42 函数)
- [ ] 阶段 1: `__load_zpkg`
- [ ] 阶段 2: `__call_static`
- [ ] 阶段 3: stdlib 封装
- [ ] 阶段 4: 测试与验证

## 阶段 0: 可行性验证(✅ GO,2026-06-18)
- [x] 0.1 确认 builtin 可重入 VM 调 z42 函数取返回。**GO**:
  - 统一入口 `interp::exec_function(ctx,&module,&func,&args)->ExecOutcome`(`pub(crate)`、`&VmContext` 不可变 → 可嵌套);`run_returning`(mod.rs:68)取返回。
  - builtin 签名 `fn(&VmContext,&[Value])->Result<Value>`(corelib/mod.rs:62)拿 `&VmContext`,无借用冲突。
  - 先例:ObjNew 调 ctor(exec_object.rs:145)、call_indirect 调闭包(exec_call.rs:161)、JIT vcall(jit/helpers/vcall.rs:116)、嵌入 invoke_impl(host/ops.rs:245)全走 exec_function 重入 + 传播异常。
  - 实现细节:跨 zpkg 目标函数的 owning module 取法照搬 call_indirect 跨包路径。

## 阶段 1: `__load_zpkg`
- [ ] 1.1 `lazy_loader.rs`:加 `declare_from_path(path)` —— 读 zbc 元数据建 `ZpkgCandidate` 插 `declared_zpkgs`
- [ ] 1.2 `corelib/runtime_dyn.rs`:`__load_zpkg(path)` builtin —— 调 declare_from_path + `load_zpkg_file`;幂等;错误抛 RuntimeException
- [ ] 1.3 `corelib/mod.rs`:注册 builtin

## 阶段 2: `__call_static`
- [ ] 2.1 `corelib/runtime_dyn.rs`:`__call_static(fqn, string[])->int` —— `try_lookup_function` 解析 + 签名严格校验 + 重入执行 + 取 int
- [ ] 2.2 `corelib/mod.rs`:注册 builtin

## 阶段 3: stdlib 封装
- [ ] 3.1 `z42.core/src/Runtime.z42`:`Std.Runtime.LoadZpkg(string)` + `CallStatic(string, string[])->int` extern（`[Native]`）

## 阶段 4: 测试与验证
- [ ] 4.1 Rust 单测 `runtime_dyn_tests.rs`:declare_from_path / call_static 正确+错误签名 / 幂等
- [ ] 4.2 VM e2e `src/tests/dynamic-load-call/load_call/`:load 测试 zpkg → call_static 传参取返回；expected_output
- [ ] 4.3 `cargo build --release` + `cargo test`(runtime 单测,memory:改 runtime 必跑)
- [ ] 4.4 `z42 xtask.zpkg test`(dotnet + vm interp+jit + cross-zpkg + lib 全绿)
- [ ] 4.5 spec 场景逐条覆盖确认
- [ ] 4.6 `docs/design/runtime/` 实现原理同步(动态加载 + builtin 重入调用)

## 备注
- 无 zbc/zpkg 格式 bump(纯 builtin + extern)。
- 阶段 0 是 go/no-go 关口:若 builtin 重入不可行,整方案需重设计(退回子进程 spawn)。
