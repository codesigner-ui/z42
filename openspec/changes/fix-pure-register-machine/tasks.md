# Tasks: 消除 StoreInstr/LoadInstr，实现纯寄存器机

> 状态：🟡 进行中 | 创建：2026-04-18

**变更说明：** 将 IR 从混合寄存器/命名槽机改为纯寄存器机，消除 `StoreInstr`/`LoadInstr`，所有变量存取用整数寄存器 ID。

**原因：** 字符串 key 的 HashMap 查找比整数索引慢一个数量级；纯寄存器机是 JIT 实现的前置条件。

**文档影响：** `docs/design/ir.md` 需更新 IR 指令集定义。

---

## 核心任务

### Phase 1: 分析和设计
- [ ] 1.1 确认 IR 中所有 StoreInstr/LoadInstr 的使用位置
  - [ ] 检查 FunctionEmitter.cs 中的发出位置
  - [ ] 检查 IrGen.cs 中是否有相关逻辑
  - [ ] 检查运行时解释器的处理

- [ ] 1.2 设计变量→寄存器映射方案
  - [ ] 计划参数槽索引范围（已占用：`0` 到 `paramCount`）
  - [ ] 计划本地变量槽索引范围（从 `paramCount + 1` 开始）
  - [ ] 确保 FunctionEmitter 能预计算整个映射

### Phase 2: 编译器侧改造（C#）
- [ ] 2.1 修改 IrModule.cs
  - [ ] 删除 `StoreInstr` 和 `LoadInstr` sealed record 定义
  - [ ] 删除相关的 JsonDerivedType 特性（如果还有）

- [ ] 2.2 修改 FunctionEmitter.cs
  - [ ] 在方法/函数开头预计算所有本地变量的寄存器 ID
  - [ ] 将 `Emit(new StoreInstr(...))` 替换为 `Emit(new CopyInstr(...))`
  - [ ] 将 `EmitExpr` 中的 LoadInstr 处理删除，直接返回变量对应的 TypedReg

- [ ] 2.3 验证编译器侧完整性
  - [ ] `dotnet build` 无编译错误
  - [ ] `dotnet test` 全绿

### Phase 3: 运行时侧改造（Rust）
- [ ] 3.1 修改 `src/runtime/src/interp/frame.rs`
  - [ ] 删除 `vars: HashMap<String, Value>`
  - [ ] 确保 `regs: Vec<Value>` 覆盖所有变量

- [ ] 3.2 修改 `src/runtime/src/interp/mod.rs`
  - [ ] 删除 `StoreInstr` 和 `LoadInstr` 的执行分支
  - [ ] 运行时不再需要处理字符串查找

- [ ] 3.3 验证运行时完整性
  - [ ] `cargo build` 无编译错误
  - [ ] `./scripts/test-vm.sh` 全绿（114 passed）

---

## 关键设计决策

| 问题 | 决策 |
|------|------|
| 参数如何分配 | 按顺序：`reg[0]` = `this`（if instance method），`reg[1..]` = params |
| 本地变量如何分配 | 从 `reg[paramCount + 1]` 开始，按声明顺序 |
| 变量赋值如何处理 | 直接 `Emit(new CopyInstr(varReg, rhsReg))` |
| 变量读取如何处理 | `EmitExpr(VarExpr)` 直接返回 `varReg` |

---

## 验证标准

✅ 完成时必须满足：

```bash
dotnet build src/compiler/z42.slnx
  → 0 errors, 0 warnings

dotnet test src/compiler/z42.Tests/z42.Tests.csproj
  → 448 passed

cargo build --manifest-path src/runtime/Cargo.toml
  → Finished

./scripts/test-vm.sh
  → Total: 114 passed, 0 failed
```

---

## 备注

- 这是 M7 JIT 实现的前置条件，确保不跳过任何步骤
- IR 指令集删除后，需要同步更新 `docs/design/ir.md`
- 可能需要调整 `FunctionEmitter._nextReg` 的初始值（参考参数偏移）

