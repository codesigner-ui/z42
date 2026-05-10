# Tasks: split-exec-instr

> 状态：🟢 已完成 | 创建：2026-05-07 | 完成：2026-05-07
> 类型：refactor（最小化模式）

## 验证报告

### 编译状态
- ✅ `cargo build --manifest-path src/runtime/Cargo.toml` 无 warning 无 error
- ✅ `dotnet build src/compiler/z42.slnx -c Debug --no-incremental` 0 Error / 0 Warning
  （清空 obj/ 缓存，规避 pre-existing MSB3492 stale-cache 问题）

### 测试结果
- ✅ `cargo test --manifest-path src/runtime/Cargo.toml`: 全绿
- ✅ `./scripts/test-vm.sh`: interp 157/157 + jit 153/153 = **310/310** 全绿
- ✅ `dotnet test`: **1104/1104** 全绿

### LOC 目标
- 主 `exec_instr.rs`: 802 → **131** 行（目标 ≤ 200，超额完成）
- 子模块全部 ≤ 200 行（目标 ≤ 300）
- 全部远低于 [code-organization.md](../../../.claude/rules/code-organization.md) 500 LOC 硬限

### 穷尽性
- ✅ `grep "^\s*_\s*=>" exec_instr.rs` → 无匹配（[runtime-rust.md](../../../.claude/rules/runtime-rust.md) 合规）

### 结论：✅ 全绿，可归档

**变更说明**: 把 `src/runtime/src/interp/exec_instr.rs` (802 LOC) 按 IR 指令类别拆成多个 `exec_<category>.rs` 子模块；主分发器 `exec_instr` 保留完整穷尽 match，每条 arm 调用对应类别的 helper 函数。

**原因**: 单文件超 [code-organization.md](../../../.claude/rules/code-organization.md) 500 LOC 硬限。每个新 IR 指令都让它继续涨（review.md §1.2 / Part 4 §4.2 已点名）。**纯结构 refactor，零行为变化**。

**文档影响**:
- `docs/review.md` — 路线图 §VM 线 `split-exec-instr` 状态 `📋` → `🟢`
- `src/runtime/src/interp/README.md` — 如存在则同步核心文件表（[code-organization.md](../../../.claude/rules/code-organization.md) 同步规则）
- `docs/design/runtime/vm-architecture.md` — **不动**（这是结构 refactor，不改外部行为/机制）

---

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/interp/mod.rs` | MODIFY | 添加新子模块声明 `mod exec_const; mod exec_arith; ...` |
| `src/runtime/src/interp/exec_instr.rs` | MODIFY | 瘦身为纯分发器（match → helper 调用），目标 ≤200 LOC |
| `src/runtime/src/interp/exec_value.rs` | NEW | Constants / Copy / Arith / Cmp / Logical / Unary / Bitwise / String 指令实现 |
| `src/runtime/src/interp/exec_address.rs` | NEW | LoadLocalAddr / LoadElemAddr / LoadFieldAddr / DefaultOf |
| `src/runtime/src/interp/exec_call.rs` | NEW | Call / Builtin / LoadFn / LoadFnCached / CallIndirect / MkClos |
| `src/runtime/src/interp/exec_array.rs` | NEW | ArrayNew / ArrayNewLit / ArrayGet / ArraySet / ArrayLen |
| `src/runtime/src/interp/exec_object.rs` | NEW | ObjNew / FieldGet / FieldSet / IsInstance / AsCast / StaticGet / StaticSet |
| `src/runtime/src/interp/exec_vcall.rs` | NEW | VCall + `primitive_class_name` 辅助 + `is_array_isa` 辅助 |
| `src/runtime/src/interp/exec_native.rs` | NEW | CallNative / CallNativeVtable / PinPtr / UnpinPtr |
| `docs/review.md` | MODIFY | 路线图 §VM 线状态更新为 🟢 |

**只读引用**（理解上下文必须读，不修改）:
- `src/runtime/src/interp/dispatch.rs` — 已存在的对象分发 helper
- `src/runtime/src/interp/ops.rs` — 已存在的数值运算 helper
- `src/runtime/src/metadata/bytecode.rs` — `Instruction` enum 定义

**目标 LOC**:
| 文件 | 预计 LOC |
|---|---|
| exec_instr.rs (瘦身后) | ~150 |
| exec_value.rs | ~140 |
| exec_address.rs | ~65 |
| exec_call.rs | ~115 |
| exec_array.rs | ~50 |
| exec_object.rs | ~135 |
| exec_vcall.rs | ~155 |
| exec_native.rs | ~125 |

每个均在 [code-organization.md](../../../.claude/rules/code-organization.md) 软限（300 LOC）以下，远低于硬限（500 LOC）。

---

## 任务清单

### 阶段 1: 准备
- [ ] 1.1 阅读 `exec_instr.rs` + `dispatch.rs` + `ops.rs`，确认依赖关系（已完成）
- [ ] 1.2 确认 `Instruction` enum 完整变体清单（避免新增 variant 未覆盖）
- [ ] 1.3 baseline 测试: `cargo test -p z42_vm` + `./scripts/test-vm.sh` 全绿（前置确认）

### 阶段 2: 拆分实现（按依赖顺序）

**Helper 函数签名约定**（每个子模块统一）:
```rust
pub(super) fn exec_<op>(
    ctx: &VmContext,
    module: &Module,
    frame: &mut Frame,
    /* destructured fields from Instruction::<Op> */
) -> Result<Option<Value>>
```

返回值 `Result<Option<Value>>`: 与现有 `exec_instr` 一致——`Ok(None)` 正常；`Ok(Some(val))` 用户异常向上传播；`Err(e)` VM 内部错误。

**对每类指令**:
- 不需要 ctx/module/frame 全部三参数的，只接受需要的
- 算术/比较/逻辑/位运算等纯寄存器操作只要 `frame: &mut Frame`

- [ ] 2.1 `exec_value.rs` — Constants/Copy/Arith/Cmp/Logical/Unary/Bitwise/String 共 ~25 个 op
- [ ] 2.2 `exec_array.rs` — Array* 共 5 个 op
- [ ] 2.3 `exec_address.rs` — LoadLocalAddr / LoadElemAddr / LoadFieldAddr / DefaultOf 共 4 个 op
- [ ] 2.4 `exec_call.rs` — Call / Builtin / LoadFn / LoadFnCached / CallIndirect / MkClos 共 6 个 op
- [ ] 2.5 `exec_object.rs` — ObjNew / FieldGet / FieldSet / IsInstance / AsCast / StaticGet / StaticSet 共 7 个 op
- [ ] 2.6 `exec_vcall.rs` — VCall + `primitive_class_name` + `is_array_isa` （单独文件因 VCall 体积较大）
- [ ] 2.7 `exec_native.rs` — CallNative / CallNativeVtable / PinPtr / UnpinPtr 共 4 个 op

### 阶段 3: 主分发器瘦身 + 模块挂载
- [ ] 3.1 `interp/mod.rs` 添加 `mod exec_value; mod exec_address; mod exec_call; mod exec_array; mod exec_object; mod exec_vcall; mod exec_native;`
- [ ] 3.2 `exec_instr.rs` 改造为纯 match dispatcher，每条 arm 形如 `Instruction::Add { dst, a, b } => exec_value::add(frame, *dst, *a, *b)?,`
- [ ] 3.3 保留 `exec_instr.rs` 顶部 doc comment 说明子模块边界
- [ ] 3.4 移除 `exec_instr.rs` 内不再需要的 import（按子模块下沉）

### 阶段 4: 验证
- [ ] 4.1 `cargo build --manifest-path src/runtime/Cargo.toml` 无 warning（含 unused import）
- [ ] 4.2 `cargo test --manifest-path src/runtime/Cargo.toml` 全绿
- [ ] 4.3 `./scripts/test-vm.sh` 全绿（interp + jit 双模式 ~106 golden）
- [ ] 4.4 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 全绿（确认 IR 生成不受影响）
- [ ] 4.5 LOC 目标确认: `wc -l src/runtime/src/interp/exec_*.rs`，每个 ≤ 300 LOC，主 dispatcher ≤ 200 LOC
- [ ] 4.6 grep 确认无 `_` 通配 match 兜底（[runtime-rust.md](../../../.claude/rules/runtime-rust.md) "不允许有 `_` 通配兜底"）

### 阶段 5: 文档同步
- [ ] 5.1 检查 `src/runtime/src/interp/README.md` 是否存在；存在则同步核心文件表
- [ ] 5.2 `docs/review.md` 路线图 §VM 线 `split-exec-instr` 状态从 📋 改为 🟢
- [ ] 5.3 检查 `docs/design/runtime/vm-architecture.md` 是否提及 `exec_instr.rs`；如有指针更新

### 阶段 6: 归档 + 提交
- [ ] 6.1 tasks.md 状态 🟡 → 🟢，更新日期
- [ ] 6.2 `docs/spec/changes/split-exec-instr/` → `docs/spec/archive/2026-05-07-split-exec-instr/`
- [ ] 6.3 commit + push（[workflow.md](../../../.claude/rules/workflow.md) 阶段 9 自动提交）

---

## 备注

- **零行为变化**: 所有 IR 指令的语义、错误信息、性能特征保持不变
- **测试要求**（per [workflow.md](../../../.claude/rules/workflow.md) 测试要求表 refactor 行）: "确保已有测试仍覆盖；不得删除测试"——本 spec 不新增测试
- **穷尽性保证**: Rust 编译器对 `match Instruction { ... }` 的穷尽性检查会确保主 dispatcher 不漏任何 variant；新增 `Instruction` variant 时编译错误自动提示
- **无 unsafe / 无 unwrap**: 所有 helper 沿用 `anyhow::Result` + `bail!` 风格，不引入新的 unsafe block
