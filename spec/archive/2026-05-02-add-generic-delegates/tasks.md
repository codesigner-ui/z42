# Tasks: D1c — stdlib `Action` / `Func` / `Predicate` 真实类型 + 移除 hardcoded desugar

> 状态：🟢 已完成 | 创建：2026-05-02 | 完成：2026-05-02 | 类型：lang/stdlib（完整流程）
> **依赖**：D1a 已 GREEN（含泛型 delegate 解析 + DelegateInfo 实例化）；D1b 已 GREEN
>
> **2026-05-02 scope 调整**：泛型 delegate + where 约束已合并到 D1a 实施。
> User 选择 B 方案（完整版）：除 stdlib 真实类型 + 移除 hardcoded 外，
> 完整实现 TSIG 跨 zpkg 导出 + ImportedSymbols.Delegates 路径。
>
> **实施备注**：
> 1. ExportedDelegateDef + ExportedModule.Delegates 持类型签名（IR-level
>    type-name strings）+ TypeParams + ContainerClass。
> 2. TSIG section 末尾追加 delegates trailer（forward-compat：reader 用
>    position-guard，older zpkg 缺此 trailer 时 Delegates=null）。
> 3. ImportedSymbolLoader 把 delegates 单独 loop 处理（无 forward-ref；
>    类型签名只引用已 phase-2 完成的 classes/interfaces/primitives + generic
>    params）。
> 4. SymbolCollector 自我导入处理：当 imported delegate 与 local declaration
>    同 key 时，silent override（用 _importedDelegateKeys 集合区分）。修复了
>    z42.core 重建时自身 cached TSIG 引发的 duplicate-decl 误报。
> 5. SymbolCollector + SymbolTable hardcoded `"Action"`/`"Func"` desugar
>    全部移除。
> 6. 已有 LambdaTypeCheckTests + ClosureCaptureTypeCheckTests 用 inline
>    delegate 替代 hardcoded（不再依赖移除的路径）。
> 7. IncrementalBuildIntegrationTests cached 计数 34 → 35（Delegates.z42 加 1）。

## 进度概览
- [x] 阶段 1: stdlib Delegates.z42 创建
- [x] 阶段 2: SymbolCollector hardcoded `Action`/`Func` desugar 移除
- [x] 阶段 3: 测试
- [x] 阶段 4: 验证 + 文档同步 + 归档

## 阶段 1: stdlib Delegates.z42
- [x] 1.1 NEW `src/libraries/z42.core/src/Delegates.z42` —— 包含 0-4 arity Action + 1-4 arity Func + 1 arity Predicate（design Implementation Notes）
- [x] 1.2 `src/libraries/z42.core/z42.toml` 或入口列表 —— 加 Delegates.z42（视当前 stdlib 构建机制；如果 glob `*.z42` 自动收集则跳过）
- [x] 1.3 `./scripts/build-stdlib.sh` 验证 stdlib 编译通过（D1a 完成后 Parser 应当已支持泛型 delegate）

## 阶段 2: 移除 hardcoded desugar
- [x] 2.1 `SymbolCollector.cs:211` —— 删除 `"Action"` 单 NamedType desugar 分支
- [x] 2.2 `SymbolCollector.cs:248-253` —— 删除 `"Func"` / `"Action"` GenericType desugar 分支
- [x] 2.3 grep 全代码库确认无 hardcoded `"Action"` / `"Func"` 字符串残留
- [x] 2.4 验证现有 LambdaTypeCheckTests / ClosureCaptureTypeCheckTests 全绿（用 stdlib delegate 替代 hardcoded）

## 阶段 3: 测试
- [x] 3.1 NEW `src/compiler/z42.Tests/StdlibDelegateTests.cs` —— stdlib `Action` / `Func` / `Predicate` 解析 + 0-4 arity 实例化（5 个测试）
- [x] 3.2 NEW `src/compiler/z42.Tests/PredicateTests.cs` —— Predicate 端到端（2 个测试）
- [x] 3.3 NEW `src/runtime/tests/golden/run/delegate_d1c_stdlib/source.z42` + expected_output.txt
- [x] 3.4 NEW `examples/delegate_stdlib.z42`
- [x] 3.5 `./scripts/regen-golden-tests.sh`

## 阶段 4: 验证 + 文档 + 归档
- [x] 4.1 `dotnet build` / `cargo build` 双绿
- [x] 4.2 `dotnet test` 全绿（基线 +7）
- [x] 4.3 `./scripts/test-vm.sh` 全绿（基线 +1×2 modes）
- [x] 4.4 spec scenarios 逐条核对
- [x] 4.5 文档同步：
    - `docs/design/delegates-events.md` —— D1c 完成；§3.4 "脚本生成"改为"0-4 已手写，>4 follow-up"
    - `docs/design/language-overview.md` —— delegate 章节加 stdlib 用法示例
    - `docs/roadmap.md` —— 加一行
- [x] 4.6 移动 `spec/changes/add-generic-delegates/` → `spec/archive/2026-05-02-add-generic-delegates/`
- [x] 4.7 commit + push

## 备注
- 删除 hardcoded desugar 后必须验证现有 lambda / closure 测试全绿（替代路径正常工作）
- N>4 arity 留 follow-up（z42 自举完成后用脚本生成）
