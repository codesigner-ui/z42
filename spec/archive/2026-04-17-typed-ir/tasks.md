# Tasks: A2 Typed IR — TypedReg 全面类型化

> 状态：🟢 已完成 | 创建：2026-04-16 | 完成：2026-04-17
> 变更类型：ir（IR 指令格式变更）

## 变更说明

每个 IR 寄存器引用从 `int` 改为 `TypedReg(int Id, IrType Type)`，所有指令携带
静态类型信息。TypeChecker 推断的 Z42Type 在 IrGen 阶段映射为 IrType，一路传递到
VM。运行时可跳过动态类型 dispatch。

## 设计决策

- 方案 A (TypedReg)：全面类型化，所有寄存器引用带类型，LLVM IR 风格
- `IrType` enum：I8–I64, U8–U64, F32, F64, Bool, Char, Str, Ref, Void, Unknown
- `TypedReg(int Id, IrType Type)` readonly record struct
- ZBC 格式：type_tag 字节已预留，写入真实类型
- JSON 格式：TypedReg 序列化为 `{"id":N,"type":"kind"}`

## 任务清单

### 阶段 1：IR 定义 (z42.IR)
- [x] 1.1 新增 `IrType` enum + `TypedReg` record struct
- [x] 1.2 所有 IrInstr `int Dst/Src/A/B` → `TypedReg`，`List<int>` → `List<TypedReg>`
- [x] 1.3 IrTerminator `int` 字段 → `TypedReg`
- [x] 1.4 `IrExceptionEntry.CatchReg` → `TypedReg`

### 阶段 2：代码生成 (z42.Semantics/Codegen)
- [x] 2.1 FunctionEmitter：`Alloc()` → `Alloc(IrType)` 返回 `TypedReg`
- [x] 2.2 FunctionEmitter：`_locals` 改为 `Dictionary<string, TypedReg>`
- [x] 2.3 FunctionEmitter：`EmitExpr` 返回 `TypedReg`
- [x] 2.4 FunctionEmitterExprs/Stmts/Calls：所有指令构造适配 TypedReg
- [x] 2.5 IrGen：`EmitNativeStub` + 参数寄存器类型化
- [x] 2.6 新增 `IrType ToIrType(Z42Type)` + `ToIrType(TypeExpr)` 映射

### 阶段 3：序列化 (z42.IR/BinaryFormat)
- [x] 3.1 ZbcWriter：写 TypedReg 的类型到 type_tag
- [x] 3.2 ZbcReader：读 type_tag → TypedReg

### 阶段 4：测试 (z42.Tests)
- [x] 4.1 IrGenTests 适配 TypedReg 断言
- [x] 4.2 ZbcRoundTripTests 适配

### 阶段 5：Rust VM
- [x] 5.1 zbc binary 无需变更（type_tag 已有，读取后忽略）
- [x] 5.4 JSON IR 反序列化适配 TypedReg 格式（typed_reg_serde）

### 阶段 6：验证 & 文档
- [x] 6.1 重新生成 golden test .zbc 文件
- [x] 6.2 `dotnet build && cargo build` —— 无编译错误
- [x] 6.3 `dotnet test` —— 全绿（396/396）
- [x] 6.4 `./scripts/test-vm.sh` —— 全绿（114/114）
- [ ] 6.5 `docs/design/ir.md` 文档同步（已有 TypedReg 描述，无需更新）

## 文档影响
- `docs/design/ir.md`：指令格式描述更新（TypedReg）
