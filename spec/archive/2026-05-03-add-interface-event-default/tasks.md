# Tasks: D2c — interface event default 实现 (I10)

> 状态：🟢 已完成 | 创建：2026-05-03 | 完成：2026-05-03 | 类型：lang/parser（完整流程）
> **依赖**：D2c-多播 (`add-event-keyword-multicast`) GREEN

## 进度概览
- [x] 阶段 1: parser 实施
- [x] 阶段 2: 测试
- [x] 阶段 3: 验证 + 文档同步 + 归档

## 阶段 1: 实施
- [x] 1.1 `TopLevelParser.Members.cs` 加 `SynthesizeInterfaceEvent(typeExpr, name, span)`：验证 `GenericType("MulticastAction", [T])`，返回 2 个 instance abstract MethodSignature（add_X / remove_X，Body=null）
- [x] 1.2 `ParseInterfaceDecl` 在 modifier 解析后检测 `event` token：parse type + name + `;`，调 SynthesizeInterfaceEvent，methods.AddRange
- [x] 1.3 `TypeChecker.Exprs.Operators.cs` BindAssign desugar 扩展 `Z42InterfaceType` receiver path：用 `add_X` 方法存在判断 event 声明（无 EventFieldNames 概念）

## 阶段 2: 测试
- [x] 2.1 `EventKeywordTests.cs` +2 scenarios：interface event 产生 add_X/remove_X signatures；interface 单播 event 报 not-yet-supported
- [x] 2.2 NEW `src/runtime/tests/golden/run/interface_event/source.z42`：interface 声明 + class 实现 + interface 引用 +=/-=（vtable dispatch）
- [x] 2.3 NEW `expected_output.txt`
- [x] 2.4 `./scripts/regen-golden-tests.sh` 121 ok

## 阶段 3: 验证 + 文档 + 归档
- [x] 3.1 `dotnet build` ✅
- [x] 3.2 `dotnet test` 962/962 ✅（基线 959 + 2 EventKeywordTests + 1 golden 自发现）
- [x] 3.3 `./scripts/test-vm.sh` 238/238 ✅（基线 236 + interp/jit 各 1）
- [x] 3.4 `./scripts/build-stdlib.sh` 6/6 ✅
- [x] 3.5 spec scenarios 逐条核对
- [x] 3.6 文档同步：
  - `docs/design/delegates-events.md` 顶部状态加 D2c-interface 已落地
  - `docs/roadmap.md` 加一行
- [x] 3.7 移动 `spec/changes/add-interface-event-default/` → `spec/archive/2026-05-03-add-interface-event-default/`
- [x] 3.8 commit + push

## 备注
- 单播 event 在 interface 同样报 not-yet-supported；Spec 2b 解锁后双端放宽
- D2d 后扩 MulticastFunc/Predicate 时同步放宽 interface 校验
- BindAssign 同时支持 Z42ClassType (EventFieldNames) + Z42InterfaceType (add_X 存在) 双 receiver path
