# Proposal: 闭包 env 栈分配（Escape Analysis）

## Why

L3 闭包 env 当前一律走 `heap().alloc_array()` —— 即使 closure **不逃逸出当前函数**：

- 每次 `MkClos` 都触发一次 GC-tracked 堆分配
- env 进入 GC root 集合，每次 GC 扫描都要走一遍
- 典型场景：`list.Filter(x => x > 0)`、`array.Map(...)`、`Btn.OnClick = () => { ... };`（*前两者*的 closure 完全不逃逸）

栈分配收益：
- 零 GC 压力（env 随调用帧回收）
- 零 alloc cost（frame slot 复用）
- 内联性好（callee 直接访 caller frame 槽）

## What Changes

> ⚠️ **本变更存在一个尚未定型的设计决策（Value 表示扩展），design.md 会列出选项让 User 裁决。proposal 仅锁定问题边界，不预判实现路径。**

- **Escape 分析（Bound 层 / IR 层）**：扫描 closure 创建点（MkClos）→ 判断该 closure 是否会从当前函数逃逸（return / store-to-field / store-to-array / pass-as-arg-to-non-no-escape-callee）
- **新增 IR 提示**：`MkClos` 增加 `bool stack_alloc` 字段，由编译器在不逃逸时设为 true
- **VM 端实现**：根据 stack_alloc 标记选 frame-local arena vs heap
- **Value 表示**（待定，见 design.md Decision 1）

## Scope（允许改动的文件）

> 实际触及范围依赖于 design.md 的 Value 布局选项裁决。以下为**最大可能集**；落地时 tasks.md 会基于 User 选项收敛。

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Semantics/Bound/BoundExpr.cs` | MODIFY | `BoundLambda` / `BoundClosure` 增加 `bool StackAllocEnv = false` 标记 |
| `src/compiler/z42.Semantics/TypeCheck/ClosureEscapeAnalyzer.cs` | NEW | escape 分析 pass（可在 TypeChecker 之后跑，不污染 binding） |
| `src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs` | MODIFY | EmitLambdaLiteral 把 StackAllocEnv 透传到 `MkClosInstr.StackAlloc` |
| `src/compiler/z42.IR/IrModule.cs` | MODIFY | `MkClosInstr` 增加 `bool StackAlloc` 字段 |
| `src/compiler/z42.IR/BinaryFormat/Opcodes.cs` | NO CHANGE | opcode 不变（额外字段在 instr 编码） |
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.Instructions.cs` | MODIFY | MkClos 序列化加 1 字节 stack_alloc flag |
| `src/runtime/src/metadata/bytecode.rs` | MODIFY | Instruction::MkClos 增加 `stack_alloc: bool` 字段 |
| `src/runtime/src/metadata/bytecode_serde.rs` 或 `zbc_reader.rs` | MODIFY | 反序列化 stack_alloc flag |
| `src/runtime/src/metadata/types.rs` | **可能 MODIFY**（依赖 Decision 1）| 可能新增 `Value::StackClosure` variant 或扩展现有 `Value::Closure` |
| `src/runtime/src/interp/frame.rs` | **可能 MODIFY** | 可能新增 `env_arena: Vec<Vec<Value>>` 持有 frame-local env |
| `src/runtime/src/jit/frame.rs` | **可能 MODIFY** | 同上（JitFrame） |
| `src/runtime/src/interp/exec_instr.rs` | MODIFY | MkClos 分支按 stack_alloc 选 path |
| `src/runtime/src/jit/helpers_closure.rs` | MODIFY | jit_mk_clos 镜像 |
| `docs/design/closure.md` | MODIFY | 新增 §"escape 分析与栈分配"章节 |
| `src/runtime/tests/golden/run/closure_l3_stack/source.z42` | NEW | 验证非逃逸场景 stack alloc 命中 + 逃逸场景 fallback heap |
| `src/runtime/tests/golden/run/closure_l3_stack/expected_output.txt` | NEW | golden 期望输出 |
| `src/compiler/z42.Tests/ClosureEscapeAnalyzerTests.cs` | NEW | 单元测试：标准模式（map / filter / event handler）的 stack vs heap 决策 |

**只读引用**：

- `src/runtime/src/gc/` — 理解 GcRef 持有 env 的现状，决定 stack alloc 后 GC root 注册策略
- `spec/archive/2026-05-02-impl-closure-l3-core/design.md` — Tier C 当时为什么选堆
- `docs/design/closure.md` §6 — 当前 env 路径

## Out of Scope

- ❌ 不引入逃逸分析的"全局 / 跨函数"维度（只在 MkClos 所在函数内分析）
- ❌ 不做"closure → 直接内联展开"（inlining 是独立优化）
- ❌ 不改 GcRef / 堆 GC 整体语义；逃逸的 closure 仍走当前堆路径
- ❌ 不为 generic / 含 type-param 的 closure 加专门规则
- ❌ JIT 端的栈分配优化等 interp 路径稳定后再做（如果 interp 难以做安全的 stack alloc，**可能会 fallback 改用 frame-local arena 而非真栈**，详见 design.md）

## Open Questions

- [ ] **Value 布局**（最关键）：`Value::Closure { env: GcRef<Vec<Value>> }` 当前持 GC 引用。栈分配的 env 不能用 GcRef（会被 GC 当成可移动对象）。三选一：
  - **(a) 新增 `Value::StackClosure { env_idx: u32, fn_name: String }`** + frame 持 `env_arena: Vec<Vec<Value>>`
  - **(b) `Value::Closure` 扩展为 enum-of-enum**：`env: ClosureEnv { Heap(GcRef<...>), Stack(*const Vec<Value>) }`（unsafe 但灵活）
  - **(c) 不做"真栈分配"，改用"frame-local arena"** —— 在 frame 上加 `Vec<Vec<Value>>` 池，env 按 index 引用，逃逸时晋升到堆。Value 仍持 GcRef 但 GC root 注册时排除 frame-local 池
- [ ] **GC root 注册**：栈/arena env 在 frame_regs 之外，需要让 GC 扫描时正确处理（不当成回收对象）
- [ ] **逃逸分析覆盖率**：第一版只检测明显逃逸点（return / FieldSet / ArraySet / Call args 中的 closure）即可？跨复杂控制流的保守 fallback 到 heap 可接受？建议**可接受**
- [ ] **是否有 hot-path 数据证明这值得做**？当前没基准 → 实施前是否要先加 `vm/closure_alloc_count` 计数器跑 stdlib + golden tests 看 baseline？建议**作为 follow-up**，本 spec 先按"L3-C2 已规划项"落地基础设施，性能验证作为独立环节
