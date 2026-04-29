# Tasks: Extend `[Native]` for Tier 1 dispatch (C6)

> 状态：🟢 已完成 | 完成：2026-04-29 | 创建：2026-04-29

## 进度概览

- [x] 阶段 1: AST (Tier1NativeBinding + FunctionDecl 字段)
- [x] 阶段 2: Parser (新形式识别 + E0907)
- [x] 阶段 3: TypeChecker (xor 校验，复用现有 E0903/E0904)
- [x] 阶段 4: IR Codegen (EmitNativeStub 二选一)
- [x] 阶段 5: 错误码注册 (Z0907 / E0907)
- [x] 阶段 6: 单元测试
- [x] 阶段 7: 文档同步
- [x] 阶段 8: GREEN + 归档

---

## 阶段 1: AST

- [x] 1.1 `src/compiler/z42.Syntax/Parser/Ast.cs`：
  - 加 `public sealed record Tier1NativeBinding(string Lib, string TypeName, string Entry);`
  - 在 `FunctionDecl` 末尾加 `Tier1NativeBinding? Tier1Binding = null` 默认参数

## 阶段 2: Parser

- [x] 2.1 `src/compiler/z42.Syntax/Parser/TopLevelParser.Helpers.cs`：
  - 重命名 `TryReadNativeIntrinsic` → `TryReadNativeAttribute`，返回新的 `NativeAttribute`（或 nullable struct）
  - 检测 `[Native(IDENT = STRING, ...)]` 形式
  - 解析 lib / type / entry 三个键，缺任一报 E0907
- [x] 2.2 调用方对接（grep `TryReadNativeIntrinsic` 全仓）
- [x] 2.3 单测覆盖六个解析场景（旧形式 / 新形式 / 顺序无关 / 缺 lib / 缺 type / 缺 entry / 未知键）

## 阶段 3: TypeChecker

- [x] 3.1 `TypeChecker.cs::326` 附近 `hasNative` 计算改为 `fn.NativeIntrinsic != null || fn.Tier1Binding != null`
- [x] 3.2 防御性 check：parser 已防双形式同存，但仍加 assertion

## 阶段 4: IR Codegen

- [x] 4.1 `src/compiler/z42.Semantics/Codegen/IrGen.cs::EmitNativeStub`：
  - signature 加 `Tier1NativeBinding? tier1`
  - 内部根据 tier1 emit `CallNativeInstr` vs `BuiltinInstr`
- [x] 4.2 `EmitMethod` / `EmitFunction` 调用点透传 `method.Tier1Binding` / `fn.Tier1Binding`

## 阶段 5: 错误码

- [x] 5.1 `Diagnostic.cs`：加 `public const string NativeAttributeMalformed = "E0907";`
- [x] 5.2 `DiagnosticCatalog.cs`：加 E0907 catalog 条目（title / explanation / example）
- [x] 5.3 `docs/design/error-codes.md`：Z0907 → 已启用 + 抛出条件描述

## 阶段 6: 单元测试

- [x] 6.1 `src/compiler/z42.Tests/NativeAttributeTier1Tests.cs` NEW，覆盖 spec scenarios

## 阶段 7: 文档同步

- [x] 7.1 `docs/design/interop.md` §10 加 C6 行 ✅
- [x] 7.2 `docs/roadmap.md` Native Interop 表 加 C6 → ✅

## 阶段 8: GREEN + 归档

- [x] 8.1 `dotnet build src/compiler/z42.slnx` + `dotnet test`
- [x] 8.2 `cargo test --workspace --manifest-path src/runtime/Cargo.toml`
- [x] 8.3 `./scripts/test-vm.sh`
- [x] 8.4 spec scenarios 1:1 对照
- [x] 8.5 归档 `spec/changes/extend-native-attribute` → `spec/archive/2026-04-29-extend-native-attribute`
- [x] 8.6 commit + push

## 备注

- 测试不做端到端运行（需要 test harness 在 zbc 启动前预注册 numz42-c）；由后续 spec 单独引入
- 旧形式 `[Native("__name")]` 行为零变化，所有现有 L1 stdlib 测试继续 pass
