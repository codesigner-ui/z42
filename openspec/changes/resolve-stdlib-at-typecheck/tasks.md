# Tasks: 在 TypeCheck 时解析标准库调用

**变更说明：** 扩展 DepIndex 包含参数类型，注入到 TypeChecker，消除 BoundCallKind.Unresolved 和 EmitUnresolvedCall

**原因：** 现有 DepIndex 可以加载完整的 stdlib 元数据，在 TypeCheck 时就能完全解析，无需延迟到 IrGen

**文档影响：** 
- docs/design/type-checking.md（BoundCallKind 语义、DepIndex 注入）
- docs/roadmap.md（TypeCheck 完成度）

---

## 阶段 1：扩展数据结构

- [ ] 1.1 扩展 DepCallEntry 添加参数类型数组和返回类型
  - 当前：`DepCallEntry(QualifiedName, Namespace)`
  - 新增：`DepCallEntry(QualifiedName, Namespace, ParamTypes: IReadOnlyList<Z42Type>, RetType: Z42Type)`
  
- [ ] 1.2 修改 DependencyIndex.Build() 从 IrFunction 提取类型信息
  - IrFunction.RetType 是字符串（如 "I32", "Str"），需转换为 Z42Type
  - IrFunction 没有参数类型列表，只有 ParamCount
  - **决策：** 参数类型统一设为 Z42Type.Unknown（因为 IrFunction 不提供参数类型）
  - 返回类型从 `RetType` 字符串转换为 Z42Type

- [ ] 1.3 添加 IrType → Z42Type 转换函数在 DependencyIndex

## 阶段 2：注入 DepIndex 到 TypeChecker

- [ ] 2.1 修改 TypeChecker 构造函数，添加可选 DependencyIndex 参数
  - `TypeChecker(DiagnosticBag diags, LanguageFeatures? features = null, DependencyIndex? depIndex = null)`

- [ ] 2.2 修改所有 TypeChecker 的创建站点，传入 depIndex
  - PackageCompiler.TryCompileSourceFiles()
  - SingleFileCompiler.CompileUnit()
  - GoldenTests.cs
  - ZbcRoundTripTests.cs

## 阶段 3：修改 BindCall 逻辑

- [ ] 3.1 修改 BindCall() 的 "static class method" 分支（TypeChecker.Calls.cs:16-40）
  - 当 staticSig == null 且 isImported == true 时，查询 `_depIndex?.TryGetStatic()`
  - 如果找到，生成 BoundCall.Static + 从 DepCallEntry 获取返回类型
  - 如果找不到，返回错误（不再生成 Unresolved）

- [ ] 3.2 修改 "member call on unknown target" 分支（TypeChecker.Calls.cs:45-55）
  - 查询 `_depIndex?.TryGetInstance()` 
  - 如果找到，生成 BoundCall.Instance + 返回类型
  - 否则报错

- [ ] 3.3 修改 "instance method on class" 分支（TypeChecker.Calls.cs:88-95）
  - 当方法未找到但 isImportedCls == true 时，查询 DepIndex
  - 如果找到，生成 BoundCall.Instance
  - 否则报错

- [ ] 3.4 修改 "unknown function" 分支（TypeChecker.Calls.cs:143-146）
  - 查询 `_depIndex?.TryGetStatic()` 用 "Free" 命名空间尝试
  - 如果找到，生成 BoundCall.Static
  - 否则报错

- [ ] 3.5 移除所有 `new BoundCall(BoundCallKind.Unresolved, ...)` 创建

## 阶段 4：清理编译器

- [ ] 4.1 从 BoundCallKind 枚举中删除 Unresolved variant（Bound/BoundCallKind.cs）

- [ ] 4.2 删除 FunctionEmitterCalls.cs 的 EmitUnresolvedCall() 方法（第 76-172 行）

- [ ] 4.3 修改 FunctionEmitterCalls.cs 的 EmitBoundCall() 的 switch
  - 移除 `case BoundCallKind.Unresolved:` 分支

- [ ] 4.4 搜索并清理所有引用 BoundCallKind.Unresolved 的代码

## 阶段 5：验证

- [ ] 5.1 `dotnet build` — 消除所有编译错误

- [ ] 5.2 `dotnet test` — 单元测试全绿

- [ ] 5.3 `./scripts/test-vm.sh` — VM 测试全绿

- [ ] 5.4 确认参数类型检查行为与修改前一致（虽然参数类型为 Unknown）

## 备注

- **参数类型为 Unknown 的含义：** stdlib 调用的参数不进行类型检查，但返回类型已知。这在运行时通过 VM 的动态类型检查来保证安全。
- **如果 IrFunction 后续添加参数类型信息，** 可以在 DependencyIndex.Build() 中使用，无需修改 TypeChecker。
