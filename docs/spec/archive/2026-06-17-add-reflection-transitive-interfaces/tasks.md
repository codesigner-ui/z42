# Tasks: 传递接口闭包

> 状态：🟢 已完成 | 创建：2026-06-17 | 完成：2026-06-17 | 类型：ir（接口继承图持久化 + VM 传递判定，复用接口块无格式 bump，完整流程）

## 阶段 1: compiler
- [x] 1.1 `Ast.cs`：`InterfaceDecl` 加 `List<string>? BaseInterfaces`
- [x] 1.2 `TopLevelParser.Types.cs`：`ParseInterfaceDecl` 捕获基接口（替"Skip"）
- [x] 1.3 `IrGen.Classes.cs`：`EmitInterfaceDesc` 填 `Interfaces`（FQ，QualifyClassName）
- [x] 1.4 dotnet build 编译器：0 error

## 阶段 2: runtime
- [x] 2.1 `reflection.rs`：`builtin_type_interfaces` 传递 BFS（接口展开其基接口）
- [x] 2.2 `dispatch.rs`：`is_subclass_or_eq_td` 接口命中递归基接口（抽 iface_implements helper）
- [x] 2.3 `jit/helpers/object.rs`：`is_subclass_or_eq` 同步传递查接口
- [x] 2.4 cargo build（debug+release）：0 error

## 阶段 3: 测试与验证
- [x] 3.1 golden `src/tests/types/transitive_interfaces.z42`（interp+jit；GetInterfaces 传递 + is/IsAssignableFrom 传递 + 直接不回归 + 无关 false）
- [x] 3.2 cargo test --lib 全绿
- [x] 3.3 dotnet GoldenTests 全绿（接口块填充不破坏既有）
- [x] 3.4 xtask vm interp 188/0 + jit 180/0（首跑 1 interp flake，同产物重跑即过）/ cross-zpkg 2/0 / stdlib 272·22

## 阶段 4: docs + 归档
- [x] 4.1 `reflection.md`：传递接口主体 + Deferred（标 transitive-interfaces 落地）
- [x] 4.2 ACTIVE.md 释放 compiler+runtime 锁 + 归档
- [x] 4.3 z42c 同步 follow-up（接口块填充 + parser 捕获；byte-identical → port-z42c）

## 备注
- 无格式 bump：复用 #2/#3 接口块；接口条目接口块空→非空，仅 regen。
- 泛型接口 base 的 T 替换 / 继承接口方法纳入 GetMethods 延后。
- `is`/`as` 两实现（interp + JIT）必须同步（#3 教训）。
