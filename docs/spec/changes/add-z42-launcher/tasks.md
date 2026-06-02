# Tasks: z42 launcher (P1)

> 状态：🟡 待确认门 | 创建：2026-06-02 | 类型：vm + cli + 新工具（完整流程）
> 硬约束：能用 z42 实现的都用 z42（原生面仅限 trampoline）

## 进度概览
- [ ] 阶段 0: 前置使能(z42vm argv 透传 + z42c Exe-zpkg)
- [ ] 阶段 1: 原生 trampoline `z42`
- [ ] 阶段 2: z42 launcher 核心(子命令)
- [ ] 阶段 3: 验证 + cutover + 文档

## 阶段 0: 前置使能（durable，在 Rust 运行时 + 编译器）
- [ ] 0.1 `src/runtime/src/main.rs`：`Cli` 加收尾 `args: Vec<String>`(trailing_var_arg);`-- ` 后入 args
- [ ] 0.2 `src/runtime/src/vm_context.rs`：存 program argv
- [ ] 0.3 定位并改 env corelib：`GetCommandLineArgs` 返回透传 argv
- [ ] 0.4 z42vm e2e：`-- a b c` → 程序得 `[a,b,c]`；无 `--` 空(回归)
- [ ] 0.5 `z42.Driver/BuildCommand.cs`：`build` script 模式 emit Exe-zpkg(autodetect Main → META.entry)
- [ ] 0.6 单测：script build 产物 mode=Exe + META.entry 正确；无 Main 报错

## 阶段 1: 原生 trampoline（最小 Rust）
- [ ] 1.1 `src/toolchain/launcher/Cargo.toml` + `src/main.rs`：定位 `Z42_HOME`/`~/.z42`，找 `launcher/{z42vm,launcher.zpkg}`
- [ ] 1.2 exec `z42vm launcher.zpkg -- <argv 原样>`，回传退出码
- [ ] 1.3 launcher 运行时缺失 → 明确报错 + 重装指引
- [ ] 1.4 `README.md`(目录职责)

## 阶段 2: z42 launcher 核心（全 z42）
- [ ] 2.1 工程文件 + `launcher.z42` 骨架 + argv 经 GetCommandLineArgs 读入(依赖阶段 0)
- [ ] 2.2 `Std.Cli.ArgParser` 接子命令:run / link / list / default / which / info
- [ ] 2.3 `~/.z42` 布局读写(`IO.Directory/Path/File`):runtimes 枚举、config.toml 读写默认版本
- [ ] 2.4 resolve 顺序(--runtime > app 声明[空] > default > 唯一已装 > 报错)
- [ ] 2.5 `run`:resolve → `Std.IO.Process.Spawn` 起 `runtimes/<ver>/z42vm app.zpkg -- <args>` → 回传码
- [ ] 2.6 `link`/`list`/`default`/`which`/`info` 实现
- [ ] 2.7 按 LOC 限拆分核心为多 .z42 文件;三层目录配 README
- [ ] 2.8 核心 [Test](`tests/*.z42`):argv 解析 / resolve 各分支 / 临时 Z42_HOME 布局 / 缺版本报错

## 阶段 3: 验证 + cutover + 文档
- [ ] 3.1 端到端:trampoline → 核心 → 跑 hello Exe-zpkg 带 args，输出含 args
- [ ] 3.2 `./scripts/test-all.sh --scope=full` 全绿
- [ ] 3.3 cutover 1 个已 ported 脚本(test-vm)改走 `z42 run ... -- args`，去 env-var hack，行为不变
- [ ] 3.4 `docs/design/runtime/launcher.md`(架构 + ~/.z42 + 命令 + resolve)
- [ ] 3.5 `docs/design/runtime/vm-architecture.md` argv 透传段
- [ ] 3.6 `docs/roadmap.md` 登记 launcher + 更新 build-driver 相关条目
- [ ] 3.7 spec scenarios 逐条覆盖确认

## 备注
- P2(install/uninstall/self update/下载+校验、app 自带版本声明格式)= 后续独立 spec。
- 阶段 0 可作为独立 commit(vm/cli 使能,本身解了 script 传参问题);阶段 1-3 launcher 工具。
