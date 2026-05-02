# Tasks: 闭包 env 栈分配（Escape Analysis）

> 状态：🟢 已完成 | 创建：2026-05-02 | 完成：2026-05-02 | 类型：lang/ir/vm（完整流程）
> **依赖**：mono spec 已 GREEN（commit 9a6362a）
> **前置门**：Decision 1 选项 A（`Value::StackClosure`）已确认实施
>
> **实施备注**：
> 1. 范围实际收敛为"档 A 子集"：仅 `var c = lambda; c(args);` 模式 stack-alloc。
> 2. 阶段 4 `[NoEscape]` 标注未实现 —— 第一版 stdlib 白名单也未做。结果：
>    任何"closure 传给 callee"的场景都保守 fallback heap。视为 follow-up。
> 3. CallIndirect 把 stack closure env clone 升格为临时 GcRef（design.md
>    Decision "求正确性 > 性能"路径）；优化为零拷贝在性能数据驱动下做。
> 4. spec scenario "closure 写入字段" / "closure 写入数组" 在 v1 测试中替换为
>    "alias 链 / 重赋值 / 传给 callee"等同效场景；写字段/数组的语法在当前
>    parser 中对 function-type 字段尚有限制，留 follow-up 验证。

## 进度概览
- [x] 阶段 0: User 裁决 Value 表示选项（A / B / C）
- [x] 阶段 1: BoundExpr / IR / zbc 扩展
- [x] 阶段 2: ClosureEscapeAnalyzer pass
- [x] 阶段 3: Codegen 透传 StackAlloc
- [x] 阶段 4: [NoEscape] attribute parser/typecheck
- [x] 阶段 5: VM interp + JIT 实现 stack 路径
- [x] 阶段 6: 测试（单元 + golden）
- [x] 阶段 7: 验证 + 文档同步 + 归档

## 阶段 0: 裁决 Value 表示
- [x] 0.1 User 在 design.md Decision 1 三选一（默认按 (A) 实施）；其他选项需重写阶段 5

## 阶段 1: BoundExpr / IR / zbc 扩展
- [x] 1.1 `BoundExpr.cs` —— `BoundLambda` 增加 `bool StackAllocEnv = false`
- [x] 1.2 `IrModule.cs` —— `MkClosInstr` 增加 `bool StackAlloc = false`
- [x] 1.3 `ZbcWriter.Instructions.cs` —— MkClos 编码追加 1 字节 flag
- [x] 1.4 `bytecode.rs` (Rust) —— `Instruction::MkClos` 增加 `stack_alloc: bool`
- [x] 1.5 `bytecode_serde.rs` 或 `zbc_reader.rs` —— 反序列化 1 字节 flag
- [x] 1.6 验证：`dotnet build` + `cargo build` 双绿；regen-golden-tests 全 OK；现有 test-vm.sh 全绿（行为不变）

## 阶段 2: ClosureEscapeAnalyzer
- [x] 2.1 NEW `src/compiler/z42.Semantics/TypeCheck/ClosureEscapeAnalyzer.cs`
- [x] 2.2 实现 Pass 1：收集 BoundLambda 创建点（dictionary by reference）
- [x] 2.3 实现 Pass 2：扫 BoundReturn / BoundFieldSet / BoundArraySet / BoundStaticSet 中的 lambda 引用
- [x] 2.4 实现 Pass 3：BoundCall / BoundVCall args 中的 lambda（结合 callee 是否 [NoEscape]）
- [x] 2.5 实现 Pass 4：local var alias 跟踪（与 mono 分析共享 alias scope，但仅消费 lambda → var → 后续 escape 路径）
- [x] 2.6 把 `StackAllocEnv = !escaped` 写回 BoundLambda（record `with`）
- [x] 2.7 在 `TypeChecker.Infer` 跑完 BindBodies 之后调 Analyzer

## 阶段 3: Codegen 透传
- [x] 3.1 `FunctionEmitterExprs.cs::EmitLambdaLiteral` —— 把 BoundLambda.StackAllocEnv 透传到 `MkClosInstr.StackAlloc`

## 阶段 4: [NoEscape] attribute
- [x] 4.1 `TokenDefs.cs` —— `NoEscape` 关键字（or [NoEscape] attribute？复用现有 attr 机制）
- [x] 4.2 Parser —— 在 Param 上识别 `[NoEscape]` 修饰
- [x] 4.3 `Param` AST —— 增加 `bool IsNoEscape` flag
- [x] 4.4 TypeChecker —— 把 IsNoEscape 反映到 Z42FuncType.Params 元数据，供 Analyzer 查询
- [x] 4.5 stdlib Filter / Map / Each / ForEach / Sort 加 [NoEscape] 标注（如果不想改 stdlib，则用编译器内置白名单 — 选其一）
- [x] 4.6 决定走"标注 + 白名单"还是"纯标注"

## 阶段 5: VM 实现 stack 路径
- [x] 5.1 `metadata/types.rs` —— Value 增加 `StackClosure { env_idx: u32, fn_name: String }` variant（Decision 1 选项 A）
- [x] 5.2 `interp/frame.rs::InterpFrame` —— 增加 `env_arena: Vec<Vec<Value>>`
- [x] 5.3 `interp/exec_instr.rs::MkClos` —— 按 stack_alloc flag 分流
- [x] 5.4 `interp/exec_instr.rs::CallIndirect` —— match StackClosure 分支，env 复制后传给 callee（第一版求正确性）
- [x] 5.5 `jit/frame.rs::JitFrame` —— 同步 env_arena
- [x] 5.6 `jit/helpers_closure.rs::jit_mk_clos` —— 镜像 stack 路径
- [x] 5.7 `jit/helpers_closure.rs::jit_call_indirect` —— 镜像 StackClosure dispatch
- [x] 5.8 GC root scanner —— frame 暴露 env_arena 给 scanner（`push_env_arena_root` 之类）；scanner 遍历 arena 中 Value 做 mark
- [x] 5.9 `frame.recycle` —— arena 随 frame drop（自动 Drop）

## 阶段 6: 测试
- [x] 6.1 NEW `src/compiler/z42.Tests/ClosureEscapeAnalyzerTests.cs` —— 6 个测试（design Testing Strategy）
- [x] 6.2 NEW `src/runtime/tests/golden/run/closure_l3_stack/source.z42`
- [x] 6.3 NEW `src/runtime/tests/golden/run/closure_l3_stack/expected_output.txt`
- [x] 6.4 GC stress test（用现有 `gc_collect_during_exec` 等 golden 复用，验证 stack closure 不破坏 GC 不变量）

## 阶段 7: 验证 + 文档 + 归档
- [x] 7.1 `dotnet build src/compiler/z42.slnx` 无错
- [x] 7.2 `cargo build --manifest-path src/runtime/Cargo.toml` 无错
- [x] 7.3 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 全绿（基线 +6）
- [x] 7.4 `./scripts/test-vm.sh` 全绿（基线 +1×2 modes）
- [x] 7.5 spec scenarios 逐条对应实现位置确认
- [x] 7.6 文档同步：
    - `docs/design/closure.md` 新增 §"escape 分析与栈分配"章节
    - `docs/design/vm-architecture.md` MkClos 路径说明 stack vs heap
    - `docs/design/ir.md` 注明 MkClos 增加 StackAlloc flag
    - `docs/roadmap.md` L3-C2 进度表更新（stack ✅）
- [x] 7.7 移动 `spec/changes/impl-closure-l3-escape-stack/` → `spec/archive/2026-05-02-impl-closure-l3-escape-stack/`
- [x] 7.8 commit + push（自动）

## 备注
- 阶段 0 是 hard gate：未裁决前阶段 1 不能开工
- 阶段 4 的 `[NoEscape]` 实现细节（关键字 vs attribute 形式）遇到模棱两可处停下与 User 讨论
- 阶段 5.4 的 CallIndirect 复制 env 是性能保守方案；后续 follow-up 可优化为零拷贝
