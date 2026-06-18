# Proposal: runtime 动态加载 + 静态调用(on-demand workload 地基)

## Why
on-demand workload 机制(平台命令逻辑随 `z42 workload install <plat>` 按需安装、launcher 运行时载入并调用)需要两件 VM 能力:① 运行时把任意路径的 zpkg 登记进懒加载器;② 按 FQN 调用其中一个**约定签名的静态函数**。VM 已有 `LazyLoader`(运行时按名加载 zpkg)+ `try_lookup_function`(cross-zpkg 按名解析),但**缺**:从任意路径声明 zpkg 的入口、以及从 builtin 重入 VM 调用一个 z42 函数并取返回。补这两个 builtin 后,launcher 才能 `__load_zpkg(workloadZpkg)` + `__call_static("Z42.Workload.Ios.Export.Run", args)`,无需反射 `Invoke`、无需建对象、无需接口。

不做此事:on-demand 无法落地,平台逻辑只能编译期 baked 进 launcher(当前 option ① 状态),平台命令无法随 workload 按需装。

## What Changes
- 新 builtin `__load_zpkg(path)`:读 zpkg 元数据 → 登记进 `LazyLoader.declared_zpkgs` 并加载(幂等)。
- 新 builtin `__call_static(fqn, string[]) -> int`:按 FQN 解析静态函数(触发懒加载)→ 以单个 `string[]` 参数重入 VM 执行 → 取 `int` 返回。**约定固定签名 `(string[])->int`**,避开通用参数 marshaling(即避开完整 `Invoke`)。
- stdlib 薄封装:`Std.Runtime.LoadZpkg(path)` + `Std.Runtime.CallStatic(fqn, args)`(或并入现有 reflection/runtime 命名空间)。
- 动态载入类型的 vtable / 函数表注册沿用 `lazy_loader` 现有路径(`load_zpkg_file` + `try_fixup_inheritance`);本变更**不引入对象创建/接口分发**(留给静态函数约定)。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/corelib/runtime_dyn.rs` | NEW | `__load_zpkg` + `__call_static` builtin 实现 |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 注册两个 builtin |
| `src/runtime/src/metadata/lazy_loader.rs` | MODIFY | 暴露"从任意路径声明 zpkg"(`declare_from_path`)给 builtin |
| `src/runtime/src/vm_context.rs` | MODIFY | builtin 重入 VM 调用 z42 函数取返回的入口(复用 host-call 路径) |
| `src/libraries/z42.core/src/Runtime.z42` | NEW | `Std.Runtime.LoadZpkg` / `CallStatic` extern 封装 |
| `src/tests/dynamic-load-call/load_call/` | NEW | e2e:载入测试 zpkg、调静态 `Run(string[])->int`、interp+JIT 一致 |
| `src/runtime/src/corelib/runtime_dyn_tests.rs` | NEW | Rust 单测:declare-from-path / call-static 签名校验 |
| `docs/design/runtime/embedding.md` 或 `vm-architecture.md` | MODIFY | 记录动态加载 + 静态重入调用的实现原理 |

**只读引用**:
- `src/runtime/src/metadata/loader.rs`(`load_zpkg_file` / `try_fixup_inheritance` 复用)
- `src/runtime/src/interp/exec_call.rs`、`exec_object.rs`(调用/重入路径参考)
- `src/toolchain/launcher/host-api`→ 已移 `src/runtime/crates/z42-host`(native→z42 调用路径参考)

## Out of Scope
- 反射 `MethodInfo.Invoke` / 通用参数 marshaling(约定固定签名规避;完整 Invoke 仍 0.5.x)。
- 对象创建 `Activator.CreateInstance` / 接口分发(本方案用静态函数,不需要)。
- launcher 的 catalog / dispatch / provision(= 阶段 2 `impl-ondemand-dispatch`)。
- workload 打包/安装布局(= 阶段 3 `workload-packaging`)。
- 卸载已载入 zpkg(常驻即可,命令进程生命周期足够)。

## Open Questions
- [ ] `__load_zpkg` 仅支持 **packed 自包含** zpkg,还是允许其 deps 经懒加载 transitive 链接?(建议先 packed 自包含,最稳)
- [ ] builtin 重入 VM 调用:复用 host-api 的 native→z42 路径,还是新增 interp 重入入口?(勘察确认可行性后定)
- [ ] `__call_static` 签名不匹配(非 `(string[])->int`)时:报错 vs 宽松?(建议严格报错)
