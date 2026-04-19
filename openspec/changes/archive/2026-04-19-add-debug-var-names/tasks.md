# Tasks: 调试变量名 + 调用栈

> 状态：🟢 已完成 | 创建：2026-04-19 | 完成：2026-04-19

## 阶段 1: 编译器侧

- [x] 1.1 `IrModule.cs`: 新增 `IrLocalVarEntry` record + IrFunction `LocalVarTable` 字段
- [x] 1.2 `FunctionEmitter.cs`: SnapshotLocalVarTable() 在 EmitMethod/EmitFunction 末尾生成
- [x] 1.3 `ZbcWriter.cs`: BuildDbugSection 写入变量名表 + HasDebug 标志
- [x] 1.4 `ZbcReader.cs`: ReadDbugSection 读取变量名表
- [x] 1.5 `ZasmWriter.cs`: 输出 `.locals` section
- [x] 1.6 dotnet build + dotnet test 全绿 (458 passed)

## 阶段 2: VM 侧

- [x] 2.1 `bytecode.rs`: Function 新增 `local_vars: Vec<LocalVar>` + LocalVar 结构
- [x] 2.2 `zbc_reader.rs`: read_dbug() 解析 DBUG section + zpkg 路径 local_vars=vec![]
- [x] 2.3 调用栈：anyhow .context() 链已自然构建（每层 exec_function 追加 at FuncName (line N)）
- [x] 2.4 cargo build + ./scripts/test-vm.sh 全绿 (128 passed)

## 阶段 3: 测试

- [x] 3.1 ZbcRoundTripTests: LocalVarTable_SurvivesBinaryRoundTrip
- [x] 3.2 Golden test: 67_stack_trace — 多层调用 + try/catch
- [x] 3.3 dotnet test 458 passed
- [x] 3.4 ./scripts/test-vm.sh 128 passed (interp 64 + jit 64)
- [x] 3.5 手动验证 z42c disasm 输出 .locals（confirmed）
