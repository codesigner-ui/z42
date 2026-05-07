# Tasks: split-typechecker-calls

> 状态：🟢 已完成 | 创建：2026-05-08 | 完成：2026-05-08
> 类型：refactor（最小化模式）
> 来源：[docs/review.md](../../../docs/review.md) Part 1 §1.1 P0 残留（4/4 末项）

**变更说明**: 把 `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Calls.cs` (686 LOC) 按方法主题拆为主文件 + 3 个 partial 子文件。class 已是 `public sealed partial class TypeChecker`，直接添加新 partial 文件即可。

**原因**: 单文件 686 LOC 超 500 硬限。

## 验证报告

### 编译状态
- ✅ `dotnet build src/compiler/z42.slnx`: 0 Warning / 0 Error

### 测试结果
- ✅ `dotnet test`: **1104/1104**
- ✅ `./scripts/test-vm.sh`: interp 157/157 + jit 153/153 = **310/310**

### 拆分结果
| 文件 | LOC | 涵盖 |
|---|---|---|
| TypeChecker.Calls.cs (主) | 405 | BindCall 主方法（含 Static / Member / Free function 三个分支） |
| TypeChecker.Calls.Modifiers.cs | 151 | Parameter modifier 处理（BindModifiedArg / CheckArgModifiers / IsLvalueForRef 等） |
| TypeChecker.Calls.Overload.cs | 124 | LookupMethodOverload / CheckArgTypes / 泛型参数推断 |
| TypeChecker.Calls.Helpers.cs | 38 | BindAndCheckArgs / IsBuiltinCollectionType / CheckArgCount |

**已知遗留**: 主文件 405 LOC 仍超 300 软限（< 500 硬限）。瓶颈是 `BindCall` 单方法 ~395 行，远超 60 行函数硬限。本 spec 仅做文件级拆分，不动方法内部结构。函数级 refactor（拆 BindCall 三大分支为独立 helper 方法）属于"call dispatch visitor"类型问题，与 D-11 同性质——等设计重构（M7 启动前的 split-symbol-from-type 或类似时机）一并处理。

### 结论：✅ 全绿，可归档；review.md §1.1 P0 残留 **4/4 整体收口**
