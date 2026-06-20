# Tasks: fix-jit-cross-zpkg-transitive-eager

> 状态：🟢 已完成 | 创建：2026-06-20 | 完成：2026-06-20 | 类型：fix（runtime）

**变更说明：** `--mode jit` 默认化 + eager transitive merge 后，`compile_module` 要翻译
整个传递闭包的每个函数；含 `LoadLocalAddr`（out/ref/in 参数）或 native interop 等
JIT 未支持 opcode 的函数让**整模块编译 `bail!` → 程序 abort**（CI Bootstrap
`xtask build stdlib` 报 `JIT cannot translate LoadLocalAddr yet`，exit 1）。

**原因：** translate.rs 里这些 opcode 的注释声称"Function falls back to interp"，但实际
`?` 把 per-function bail 冒泡成 whole-module 失败 —— 之前没暴露是因为 jit 非默认 +
只 merge 直接依赖，闭包里碰巧没有 out/ref 函数。

**文档影响：** `docs/design/runtime/vm-architecture.md`（新增"单函数 interp 降级"子节）。

## 任务
- [x] 1.1 `jit/translate.rs`：新增 `jit_unsupported_reason(func)` 预扫描（与所有
      `bail!` opcode 分支严格同步：CallNative/CallNativeVtable/PinPtr/UnpinPtr/
      LoadLocalAddr/LoadElemAddr/LoadFieldAddr）
- [x] 1.2 `jit/mod.rs` `compile_module`：declare/translate 前跳过不可翻译函数
      （留在 func_ids/fn_entries 之外；fn_entries_by_id 逐槽 None 保持对齐）
- [x] 1.3 `jit/helpers/vcall.rs`：对象 vtable 路径 + 原始类型接收者路径补 merged-module
      interp 兜底（`module.func_index` 命中即 `exec_function`）
- [x] 1.4 `jit/mod.rs` `run_fn`：被跳过的 entry/static-init 用 `interp::exec_function`
      执行，不再硬报 entry not found；`run` 的 static-init 名单改从 `module.functions` 枚举
- [x] 1.5 回归测试：`src/tests/refs/{out_var,in_param,ref_local,ref_nested}` 删 `interp_only`
      标记（这些正是因 LoadLocalAddr abort 才被标记 jit-skip）
- [x] 1.6 `docs/design/runtime/vm-architecture.md` 同步实现原理

## 验证
- [x] cargo build --release（jit feature，default）无错
- [x] cargo test --lib：775/0（+ compression 21/0）
- [x] 端到端：`xtask build stdlib` 默认 jit 模式 exit 0（修前 abort）
- [x] vm goldens jit 184/0（修前 180；+4 = refs 摘标记后 jit 运行通过）+ cross-zpkg jit 2/0；refs 4 例 interp+jit 双模式皆 exit 0

## 备注
- ACTIVE.md 未改：runtime 行本就标"空闲"（无锁可释放），且该文件正被并行 agent
  （colocated-zpkg-deps 变更）占用编辑中——按 shared-worktree race 规则避免触碰，
  只 surgically 提交本 fix 自己的文件。
