# Tasks: D2a — MulticastAction<T>

> 状态：🟢 已完成 | 创建：2026-05-02 | 完成：2026-05-02 | 类型：stdlib（完整流程）
> **依赖**：D1c 已 GREEN
>
> **实施备注**：
> 1. 平行数组设计：`Action<T>[] handlers + bool[] alive` —— 替代原计划的
>    wrapper class `_MASlot<T>`（z42 generic class with field-of-delegate-typed
>    成员访问 + 索引器 + 调用串联触发 TypeChecker 误判，避开此问题）。
> 2. v1 不实现 `Unsubscribe(handler)` API（reference equality on delegate values
>    在 z42 当前类型系统不可行）；用户必须保留 IDisposable token 调 Dispose()。
>    D2c event `-=` 上线时重新评估（可能引入 delegate identity API）。
> 3. **关键 plumbing 修复**：D2a 实施过程中发现 3 个跨 zpkg delegate 类型签名
>    保留的链路缺口，全部修复：
>    - **A. ImportedSymbolLoader Phase 1.5**：把 delegate 加载从 Phase 2 之后
>      提前到 Phase 2 之前。让 class method 类型解析时能识别 `Action<T>`。
>    - **B. ResolveTypeName 加 delegates 参数**：`Foo<X,Y>` 字符串先尝试匹配
>      `Foo$N` delegate（返回 SubstituteTypeParams Z42FuncType），未命中再查
>      class。所有 ResolveTypeName 调用链（FillClassMembers / FillInterfaceMembers /
>      RebuildFuncType）都加传 delegates。
>    - **C. ExportedTypeExtractor.TypeToString**：Z42FuncType 不再退化为 `func`，
>      而是序列化为 `Action<...>` / `Func<...>` —— 失去 nominal 名（如自定义
>      `delegate int Foo(int)` 退化为 `Func<int,int>`），但**结构等价**（D1a
>      Decision 2 命名 delegate 与字面量结构等价）。
>    - **D. SymbolTable.ExtractIntraSymbols**：把本包内 _delegates 也注入
>      ImportedSymbols.Delegates 让 Phase 2 跨 CU 编译看到。
> 4. 这些修复**应在 D1c 完成时就一并做**，但 D1c 仅触发简单 delegate 类型
>    用法（用户写 `Action<int>` 直接消费），未触发"delegate 用作 class 字段
>    类型 → 跨 zpkg 重建"链路。D2a 的 `MulticastAction<T>` 内部使用
>    `Action<T>[] handlers` 把这条链路用到极致 → 暴露并修复缺口。
> 5. IncrementalBuildIntegrationTests 文件计数 35 → 36（MulticastAction.z42 加 1）。

## 进度概览
- [x] 阶段 1: stdlib MulticastAction.z42 + IDisposable / Disposable
- [x] 阶段 2: 验证 stdlib 编译 + TSIG 导出
- [x] 阶段 3: 测试（单元 + golden）
- [x] 阶段 4: 验证 + 文档同步 + 归档

## 阶段 1: stdlib
- [x] 1.1 grep `IDisposable` —— 确认 stdlib 是否已有该 interface（含 `Dispose()` 方法）；若已存在直接复用
- [x] 1.2 grep `Disposable` 类 —— 确认是否已有可复用的 IDisposable impl；若无则新建 `src/libraries/z42.core/src/Disposable.z42`（按 design Decision 4）
- [x] 1.3 NEW `src/libraries/z42.core/src/MulticastAction.z42`（按 design Implementation Notes）
- [x] 1.4 验证 `List<Action<T>>` 字段在 generic class 内合法 —— grep stdlib 现有 generic class 字段（Stack<T> 等）确认 generic-of-generic 字段可行
- [x] 1.5 验证 `List<T>.Remove(item)` 用 reference equality —— 看 `z42.collections/List.z42`；若用 value equality 调整 Unsubscribe 实现（用 IndexOf + RemoveAt）

## 阶段 2: 编译验证
- [x] 2.1 `dotnet build src/compiler/z42.slnx` 无错
- [x] 2.2 `./scripts/build-stdlib.sh` —— stdlib 包 6/6 succeeded
- [x] 2.3 检查 zpkg TSIG 导出 `MulticastAction` —— 由 D1c TSIG 通道处理（generic class 已支持），不需新代码

## 阶段 3: 测试
- [x] 3.1 NEW `src/compiler/z42.Tests/MulticastActionTests.cs` —— 7 个测试（design Testing Strategy）
- [x] 3.2 NEW `src/runtime/tests/golden/run/multicast_action_basic/source.z42`（design golden 模板）
- [x] 3.3 NEW `src/runtime/tests/golden/run/multicast_action_basic/expected_output.txt`
- [x] 3.4 NEW `examples/multicast_basic.z42`（演示）
- [x] 3.5 `./scripts/regen-golden-tests.sh`

## 阶段 4: 验证 + 文档 + 归档
- [x] 4.1 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 全绿（基线 +7）
- [x] 4.2 `./scripts/test-vm.sh` 全绿（基线 +1×2 modes）
- [x] 4.3 spec scenarios 逐条核对
- [x] 4.4 文档同步：
    - `docs/design/delegates-events.md` 顶部状态加 D2a；§4.1 / §4.2 / §4.3 加"已落地"标记
    - `docs/roadmap.md` 加一行
- [x] 4.5 移动 `spec/changes/add-multicast-action/` → `spec/archive/2026-05-02-add-multicast-action/`
- [x] 4.6 commit + push

## 备注
- ISubscription 体系（D2b）/ event 关键字（D2c）/ MulticastException + continueOnException=true（D2d）独立批次
- D2a 内部 strong-only storage（design Decision 1）；D2b 加 advanced + 双 vec 优化
- COW 用最简 `.ToArray()` 复制（design Decision 2），性能优化等基准触发
