# Proposal: port-z42c-codegen — Bound 树 → IR lowering（z42c.ir 模型 + FunctionEmitter）

## Why

z42c.semantics 的**类型检查半**已完成（port-z42c-semantics 1A–2B：Bound 树 + 全类型注解）。
codegen（Bound → IR）是 semantics 的**另一半**，也是 byte-identical 自举关键路径
`syntax✅ → semantics(typecheck✅ + codegen) → ir → ZbcWriter → .zbc` 的下一段。

当前 `src/z42c/z42c.ir/` 只有 7 行占位 `IrSkeleton`。本 change 从零镜像 C# `z42.IR` 的
**IR 内存数据模型**（`IrModule.cs`，~357 行）+ `z42.Semantics/Codegen/` 的 **lowering 算法**
（`FunctionEmitter`/`IrGen`，~2783 行 / 17 文件），把已类型检查的 Bound 树降级为寄存器式
SSA IR（IrModule → IrFunction → IrBlock → IrInstr）。

**不做 byte-identical .zbc emit**（ZbcWriter + token 分配）——那是依赖本 change 的**独立后续 design**。
本 change 产出**内存 IR 模型 + 文本 dump**（供 [Test] 断言），不产二进制。

## What Changes

- **z42c.ir**：从零建 IR 数据模型（IrType / TypedReg / IrModule / IrClassDesc / IrFunction / IrBlock / IrInstr 层次 / IrTerminator）。class-per-instruction（虚 Dump，User 裁决），按增量逐条加指令。
- **z42c.semantics**：新增 `Codegen/`（FunctionEmitter 每函数 lowering + IrGen 模块级驱动），消费 Bound 树（SemanticModel）产出 IrModule。集中 if-is 调度（沿用 D1）。
- **IrDump 工具**（类比 SemanticDump）：源 → typecheck → codegen → IR 文本，供 [Test]。
- z42c.semantics 加 z42c.ir 依赖。

## Scope（允许改动的文件）

> 多增量 change：下表是 CG-1A–1E + 模型骨架的核心文件。后续增量向 `IrInstr.z42`（及按 500 行硬限拆分的 `IrInstr*.z42`）追加指令类、向 `FunctionEmitter*.z42` 追加 lowering 分支。新增文件触发时回阶段 3 更新本表。

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/z42c/z42c.ir/src/IrType.z42` | NEW | IrType int 常量（I8..I64/U8..U64/F32/F64/Bool/Char/Str/Ref/Void）+ Z42Type→IrType 映射助手 |
| `src/z42c/z42c.ir/src/TypedReg.z42` | NEW | `TypedReg(Id, Type)` 寄存器 |
| `src/z42c/z42c.ir/src/IrModule.z42` | NEW | IrModule / IrClassDesc / IrFieldDesc / IrFunction / IrBlock（容器，含 StringPool） |
| `src/z42c/z42c.ir/src/IrInstr.z42` | NEW | IrInstr 基类 + virtual Op()/Dump() + 子类（按增量加：Const*/Copy/Add..Rem/比较/逻辑位运算/Call/VCall/FieldGet·Set/ObjNew/ArrayGet·Set/Cast/IsInstance/…） |
| `src/z42c/z42c.ir/src/IrTerminator.z42` | NEW | IrTerminator 基类 + RetTerm/BrTerm/BrCondTerm/ThrowTerm |
| `src/z42c/z42c.semantics/src/IrGen.z42` | NEW | 模块级：StringPool intern + IrClassDesc 收集 + 驱动逐函数 emit → IrModule（平铺，同现有 z42c.semantics 布局）|
| `src/z42c/z42c.semantics/src/FunctionEmitter.z42` | NEW | 每函数：nextReg/locals/blocks 状态 + EmitExpr(集中 if-is)→TypedReg + EmitStmt + 块管理 |
| `src/z42c/z42c.semantics/src/IrDump.z42` | NEW | 纯函数：源 → IR 文本（[Test] + driver `--dump-ir` 后续） |
| `src/z42c/z42c.semantics/tests/codegen/codegen_tests.z42` | NEW | codegen [Test] 单元（IR 文本断言） |
| `src/z42c/z42c.semantics/tests/codegen/z42c.semantics.test.codegen.z42.toml` | NEW | 测试单元 manifest |
| `src/z42c/z42c.ir/README.md` | MODIFY | 核心文件表 + 模型说明（去骨架） |
| `src/z42c/z42c.semantics/README.md` | MODIFY | 加 Codegen/ + IrDump |
| `src/z42c/z42c.ir/z42c.ir.z42.toml` | MODIFY | （如需）暴露/版本 |
| `docs/design/compiler/self-hosting.md` | MODIFY | 进度：codegen 子段（归档时） |

**只读引用**（理解上下文，不改）：

- `src/compiler/z42.IR/IrModule.cs` — IR 模型蓝本
- `src/compiler/z42.Semantics/Codegen/*.cs` — lowering 算法蓝本
- `src/z42c/z42c.semantics/src/Bound.z42` / `SemanticModel.z42` / `Z42Type.z42` — codegen 输入
- `docs/design/runtime/ir.md` — IR 指令集权威规范

## Out of Scope

- **byte-identical .zbc emit（ZbcWriter）+ token 分配（TokenAllocator）** —— 依赖本 change 的独立后续 design。
- **闭包 / lambda / 异常 try-catch / native interop / static_init / 字符串插值 / foreach 迭代器协议** —— 增量延后（与 semantics 既有延后一致）。
- z42c.pipeline 的 build 命令串联（codegen 就绪后另起）。
- z42c.project 文件系统段 / zpkg 读写（独立 backlog）。

## Open Questions（实施开工核查结论）

- [x] **IrSkeleton 保留**（不删）：3 个 skeleton 文件（SemanticsSkeleton/ProjectSkeleton/PipelineSkeleton）仍 `new IrSkeleton()`；删除会破坏跨包文件。新 IR 模型同样声明 `namespace Z42.IR` 使 namespace 存活，IrSkeleton 共存无害 → 待那些 skeleton 整体移除时清理。Scope 已去 DELETE 行。
- [x] **z42c.semantics 已依赖 z42c.ir**（toml 既有）→ 无需改 toml。
- [x] **codegen 文件平铺** src/（同现有布局），非 Codegen/ 子目录；增长后再 refactor 引子目录。
