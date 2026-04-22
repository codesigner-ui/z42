# Tasks: L3-G4d stdlib 泛型类导出

> 状态：🟢 已完成 | 创建：2026-04-22 | 完成：2026-04-22

## 进度概览
- [x] 阶段 1: TSIG 格式扩展 + Reader/Writer 同步
- [x] 阶段 2: ExportedTypeExtractor + ImportedSymbolLoader 保留 TypeParams
- [x] 阶段 3: SymbolCollector local 覆盖 imported
- [x] 阶段 4: IrGen QualifyClassName + EmitBoundNew 路由
- [x] 阶段 5: VM ObjNew lazy-load
- [x] 阶段 6: 启用 stdlib Stack<T> / Queue<T>
- [x] 阶段 7: 测试 + 全量重生成
- [x] 阶段 8: 文档 + 验证

## 阶段 1: TSIG 格式扩展

- [x] 1.1 `ExportedTypes.cs`: `ExportedClassDef` 加 `TypeParams: List<string>?`
- [x] 1.2 `ZpkgWriter.cs`: TSIG class encoding — flags 后写 tp_count + name_idx[]
- [x] 1.3 `ZpkgWriter.cs`: class name / tp name 加入 string pool intern
- [x] 1.4 `ZpkgReader.cs`: TSIG class decoding — 读 tp_count + name_idx[]
- [x] 1.5 `dotnet build` 全绿

## 阶段 2: 编译器 TSIG 消费

- [x] 2.1 `ExportedTypeExtractor.Extract`: 从 `Z42ClassType.TypeParams` 填充 `ExportedClassDef.TypeParams`
- [x] 2.2 `ImportedSymbolLoader.RebuildClassType`: 保留 TypeParams 到 Z42ClassType
- [x] 2.3 `dotnet build` 全绿

## 阶段 3: SymbolCollector 冲突裁决

- [x] 3.1 `SymbolCollector.Classes.cs`: 检测 local 注册时若已有 imported 同名 → 移除 imported 记录，不报 duplicate
- [x] 3.2 同步移除 `_importedClassNames` / `_importedClassNamespaces` 对应条目
- [x] 3.3 既有 duplicate 检查仍对 local-local 生效
- [x] 3.4 `dotnet build` 全绿；既有 L1/L2 测试零回归

## 阶段 4: IrGen + 方法调用路由

- [x] 4.1 `SemanticModel.cs`: 暴露 `ImportedClassNames: IReadOnlySet<string>`
- [x] 4.2 `IrGen.QualifyClassName`: local 优先 + imported 全限定
- [x] 4.3 `FunctionEmitterExprs.EmitBoundNew`: default 分支用 QualifyClassName
- [x] 4.4 `FunctionEmitterCalls`（若需要）: 方法调用的 class qualification 用 QualifyClassName
- [x] 4.5 `dotnet build` 全绿；既有 38/74 golden 不回归

## 阶段 5: Rust VM lazy-load

- [x] 5.1 `interp/exec_instr.rs` Instruction::ObjNew: type_registry miss → try_lookup_type → fallback
- [x] 5.2 确认 VCall 路径对 lazy-loaded TypeDesc 的 name 是 qualified 的（复用既有）
- [x] 5.3 `cargo build` 全绿

## 阶段 6: stdlib 启用

- [x] 6.1 `src/libraries/z42.collections/src/Stack.z42`: 还原为真实 `Std.Collections.Stack<T>`（L3-G4c 注释里的版本）
- [x] 6.2 `src/libraries/z42.collections/src/Queue.z42`: 同上
- [x] 6.3 `./scripts/build-stdlib.sh`: 5/5 成功（zpkg 新 TSIG 格式）

## 阶段 7: 测试 + 全量重生成

- [x] 7.1 `ZbcRoundTripTests.cs`: `Tsig_ClassTypeParams_RoundTrip`（往返保留 TypeParams）
- [x] 7.2 Golden `run/77_stdlib_stack_queue`: user 代码用 `new Stack<int>()` / `new Queue<string>()`
- [x] 7.3 Golden `run/78_stdlib_generic_shadow`: user 定义 local Stack 覆盖 stdlib
- [x] 7.4 既有 38_self_ref_class / 74_generic_stack 通过
- [x] 7.5 `./scripts/regen-golden-tests.sh`: 全部成功
- [x] 7.6 `dotnet test` + `cargo test --lib` 全绿
- [x] 7.7 `./scripts/test-vm.sh` 全绿

## 阶段 8: 文档 + 验证

- [x] 8.1 `docs/design/generics.md`: L3-G4d 小节
- [x] 8.2 `docs/roadmap.md`: L3-G4d → ✅
- [x] 8.3 Spec 场景全覆盖校验

## 备注

- zpkg 格式向前兼容：tp_count=0 时 1 字节；reader 遇不足返回 0
- local 覆盖策略保守：只在 imported 同名时覆盖，不影响其他
- Scope 守门：若阶段 5 VM lazy-load 路径更改引入回归，记入备注，不扩展 scope

## Scope 外发现记录区

- **DepIndex 劫持 local 方法调用**（新发现）：原 `FunctionEmitterCalls` 在 Instance 分支
  先查 DepIndex；若 user `class Stack` 与 stdlib `Std.Collections.Stack<T>` 都有 `Push(1 arg)`
  方法，DepIndex 会命中 stdlib 版本，导致 user.Push 被劫持到 stdlib — 引入 L3-G4d 的
  "receiverIsLocalClass" 守卫才修好。记录为本次必须的辅助修正。
- **lazy_loader try_lookup_type 不触发 load**（设计缺口）：原实现只查已加载的
  type_registry；stdlib namespace 首次使用 `new Stack<int>()` 时尚未加载 →
  ObjNew 拿到空白 fallback TypeDesc，ctor 字段布局完全错位。扩展为 "miss → 按命名空间
  触发 load → 重查"，同时 ObjNew ctor 调用也改从 lazy_loader 兜底拿 Function。
