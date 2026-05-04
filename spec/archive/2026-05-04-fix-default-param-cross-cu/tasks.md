# Tasks: D-9 — fix default param fill for cross-CU calls

> 状态：🟢 已完成 | 创建：2026-05-04 | 完成：2026-05-04 | 类型：lang/codegen

## 进度概览
- [x] 阶段 1: IrGen + IEmitterContext 加 _funcSignatures 注册
- [x] 阶段 2: FillDefaults fallback + EmitTypeDefault helper
- [x] 阶段 3: 还原 stdlib MulticastAction/Func/Predicate workaround
- [x] 阶段 4: 测试
- [x] 阶段 5: 验证 + 文档同步 + 归档

## 阶段 1: IrGen 注册
- [x] 1.1 `IEmitterContext` 加 `bool TryGetMethodSignature(string qualifiedName, out Z42FuncType sig)`
- [x] 1.2 IrGen 加 `_funcSignatures: Dictionary<string, Z42FuncType>`
- [x] 1.3 IrGen 初始化遍历 `_semanticModel.Classes`（含 imported）+ `Funcs`，双 key 注册（local QualifyName + imported QualifyClassName）

## 阶段 2: FillDefaults 改造
- [x] 2.1 FillDefaults 双层 fallback —— FuncParams (local) → funcSignatures (imported type-default)
- [x] 2.2 EmitTypeDefault(Z42Type) helper —— bool=false / int=0 / long=0 / float=0.0 / char='\0' / ref=null
- [x] 2.3 DepIndex path 增加 FillDefaults 调用（之前漏）
- [x] 2.4 vcall path 用 ReceiverClass 直接查 funcSignatures（避歧义，不全 _entries 遍历）

## 阶段 3: stdlib 还原
- [x] 3.1 `MulticastAction.z42` 回归 single signature `Invoke(T, bool=false)`
- [x] 3.2 `MulticastFunc.z42` 同上
- [x] 3.3 `MulticastPredicate.z42` 同上

## 阶段 4: 测试
- [x] 4.1 既有 multicast_* / event_keyword_* / interface_event golden 全 GREEN（核心回归 — 7 个 golden 覆盖跨 CU default param 路径）
- [x] 4.2 `./scripts/regen-golden-tests.sh` 125 ok

## 阶段 5: 验证 + 文档 + 归档
- [x] 5.1 `dotnet build` ✅
- [x] 5.2 `dotnet test` 970/971 ✅（基线 970+1 但 1 个 unrelated `IncrementalBuildIntegrationTests` 失败，pre-existing 不属本 spec）
- [x] 5.3 `cargo test --lib` ✅（不动 VM）
- [x] 5.4 `./scripts/test-vm.sh` 244/246 ✅（13_assert pre-existing failure 已记入 D-10）
- [x] 5.5 `./scripts/build-stdlib.sh` 6/6 绿
- [x] 5.6 spec scenarios 逐条核对
- [x] 5.7 文档同步：
  - `docs/deferred-features.md` D-9 移除（已实施）；NEW D-10 跟踪 13_assert pre-existing dispatch bug
  - `docs/roadmap.md` 加 2026-05-04 行
- [x] 5.8 移动 `spec/changes/fix-default-param-cross-cu/` → `spec/archive/2026-05-04-fix-default-param-cross-cu/`
- [x] 5.9 commit + push

## 备注
- v1 type-default 兜底；用户自定义 default value 跨 CU 退化为 type-default
- 完整 default value 传递留 follow-up（需 TSIG schema 扩展 `ExportedParamDef.DefaultValue`）
- 13_assert 失败 pre-existing（HEAD `de52807` baseline 也 fail）—— stdlib Assert.z42 中 `string.Contains` dispatch 误指 LinkedList.Contains，DepIndex 类匹配 bug，与本 spec 无关，记 D-10 跟踪
