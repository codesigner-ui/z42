# Tasks: D-7 — 单播 event 关键字

> 状态：🟢 已完成 | 创建：2026-05-04 | 完成：2026-05-04 | 类型：lang/parser

## 进度概览
- [x] 阶段 1: parser 实施
- [x] 阶段 2: 测试
- [x] 阶段 3: 验证 + 文档同步 + 归档

## 阶段 1: 实施
- [x] 1.1 `TopLevelParser.Members.cs` 加 `SinglecastTypeNames` HashSet
- [x] 1.2 `SynthesizeClassEvent` 加单播路径分支：字段 OptionType wrap + add 体含 throw + remove 体含 ref-eq 检查
- [x] 1.3 `SynthesizeInterfaceEvent` 单播路径放宽（与多播 interface 路径同款，handler 类型即字段类型，单播 add 返回 void）

## 阶段 2: 测试
- [x] 2.1 `EventKeywordTests` +4 单播 scenarios（class add/remove 合成 + 字段 nullable + interface 单播 + 未知 generic 报错）
- [x] 2.2 NEW `event_keyword_singlecast/source.z42` 端到端
- [x] 2.3 NEW `expected_output.txt`
- [x] 2.4 `./scripts/regen-golden-tests.sh` 125 ok

## 阶段 3: 验证 + 文档 + 归档
- [x] 3.1 `dotnet build` ✅
- [x] 3.2 `dotnet test` 971/971 ✅（基线 968 + 4 新单元 + 1 golden 自发现 - 1 旧 not-yet-supported test 移除 = +3 净增）
- [x] 3.3 `./scripts/test-vm.sh` 246/246 ✅（基线 244 + interp/jit 各 1）
- [x] 3.4 `./scripts/build-stdlib.sh` 6/6 绿
- [x] 3.5 spec scenarios 逐条核对
- [x] 3.6 文档同步：
  - `docs/design/delegates-events.md` 顶部状态加 D-7
  - `docs/roadmap.md` 加一行
  - `docs/deferred-features.md` D-7 → D-7-residual（IDisposable token + 严格 access control 留 follow-up）
- [x] 3.7 移动 `spec/changes/add-event-keyword-singlecast/` → `spec/archive/2026-05-04-add-event-keyword-singlecast/`
- [x] 3.8 commit + push

## 备注
- add_X 返回 void（不返回 IDisposable）；闭包 IDisposable 留 D-7-residual follow-up
- 严格 access control（外部 invoke / 直接 set 报错）也留 D-7-residual
- 实施期间发现 / 处理：旧 `Interface_SingleCast_Event_Reports_Not_Yet_Supported` 测试移除，正路径覆盖
- D-1b WeakRef wrapper 仍延后
- D-8b Func/Predicate 异常聚合 + MulticastException<R> 仍延后
