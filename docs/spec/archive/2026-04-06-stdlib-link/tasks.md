# Tasks: stdlib-link

> 状态：🟢 已完成 | 创建：2026-04-06 | 完成：2026-04-06

## 进度概览
- [x] 阶段 1: 删除旧表
- [x] 阶段 2: 新建 StdlibCallIndex
- [x] 阶段 3: 接通 BuildCommand + IrGen
- [x] 阶段 4: 测试与验证

## 阶段 1: 删除旧表

- [x] 1.1 删除 `z42.IR/NativeTable.cs`；修改 `TypeChecker.cs` `ValidateNativeMethod`
- [x] 1.2 删除 `z42.Compiler/TypeCheck/BuiltinTable.cs`；修改 `TypeChecker.Exprs.cs`
- [x] 1.3 修改 `IrGenExprs.cs`：移除 BuiltinTable 两个调用分支

## 阶段 2: 新建 StdlibCallIndex

- [x] 2.1 新建 `z42.IR/StdlibCallIndex.cs`：`StdlibCallEntry`、`StdlibCallIndex.Build()`

## 阶段 3: 接通 BuildCommand + IrGen

- [x] 3.1 修改 `IrGen.cs`：接受 StdlibCallIndex，追踪 UsedStdlibNamespaces
- [x] 3.2 修改 `IrGenExprs.cs`：插入 stdlib 静态/实例查询，伪类伪方法分支，发出 CallInstr
- [x] 3.3 修改 `BuildCommand.cs`：加载 stdlib zpkg，构建 StdlibCallIndex，收集依赖
- [x] 3.4 更新 golden test 期望输出（BuiltinInstr → CallInstr）

## 阶段 4: 测试与验证

- [x] 4.1 `dotnet build` — 无编译错误
- [x] 4.2 `dotnet test` — 全绿（382/382）
- [x] 4.3 `./scripts/build-stdlib.sh` — stdlib 仍能编译
- [x] 4.4 `./scripts/test-vm.sh` — 全绿（86/86，interp + jit）
- [x] 4.5 文档同步：`docs/design/ir.md`（builtin 指令 + stdlib 调用链说明）；`docs/roadmap.md`
- [x] 4.6 归档

## 备注

- `StdlibCallIndex` 中发现 arity-key 双重后缀 bug（`Substring$1$1`）：函数名已含 `$N` 时不再追加 `$userArity`，直接使用 methodPart 作为 arityKey
- List/Dict 伪类方法（Add、Remove、Contains 等）在 IrGenExprs 中直接分发到 builtin，不走 StdlibCallIndex，因为 VCallInstr 无法在 Array/Map 值上派发
- `Contains` 统一路由到 `__contains`（同时支持 String 和 Array receiver），消除 Assert.Contains vs String.Contains 实例索引歧义，无需重建 stdlib
- `IrFunction.IsStatic` + `[JsonIgnore(WhenWritingDefault)]` 确保静态方法只在值为 true 时序列化到 JSON，golden test 快照不受影响
