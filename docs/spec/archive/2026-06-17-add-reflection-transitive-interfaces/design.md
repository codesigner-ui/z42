# Design: 传递接口闭包

## Architecture

```
编译期                                    运行期（每个接口现在有自己的接口块）
─────────────────────────────────        ──────────────────────────────────────────
interface IFoo {}
interface IBar : IFoo {}        ──────►   TypeDesc[IBar].interfaces = [IFoo]   （新：接口条目接口块填充）
class C : IBar {}                         TypeDesc[C].interfaces    = [IBar]

GetInterfaces(typeof(C))：类 base 链收集 [IBar] → BFS 展开接口基接口 → [IBar, IFoo]
c is IFoo：is_subclass_or_eq_td 类链查接口命中 IBar → 递归 IBar 的基接口 → 命中 IFoo → true
```

复用 add-reflection-interface-class-predicates（#2）的接口 TYPE 条目 + add-reflection-assignable-from
（#3）的接口块 FQ 名 —— 接口条目的接口块此前恒空（EmitInterfaceDesc 设 null），现填其基接口。
**结构不变** → 同版本 reader 已能读，无格式 bump。

## Decisions

### Decision 1: 复用接口块，无格式 bump

**问题**：接口的基接口存哪？

**决定**：存进接口 TYPE 条目的**接口块**（与类的接口块同一结构，#2/#3 已建）。接口条目此前接口块
恒空（count=0），现填基接口（FQ 名）。reader 读 count+names 不变 → **无 wire 新字段、无版本 bump**，
仅需 regen（接口条目字节从空接口块变非空）。

### Decision 2: GetInterfaces 传递 BFS

**问题**：传递闭包怎么算？

**决定**：`builtin_type_interfaces` 先沿类 base 链收集各类声明的接口（现状），再对收集到的每个接口
BFS 展开——解析接口 TypeDesc、把它的接口块（基接口）入队，去重直到收敛。

### Decision 3: `is`/IsAssignableFrom 传递查接口（两实现同步）

**问题**：`c is IFoo`（C : IBar : IFoo）。

**决定**：`is_subclass_or_eq_td`（interp）/ `is_subclass_or_eq`（JIT）在类 base 链每层查接口时，
对命中的接口**再递归其基接口**（小 BFS/worklist）。两实现必须同步（golden jit 模式会暴露 JIT 路径，
见 #3 教训）。

## Implementation Notes

- **AST/parser**：`InterfaceDecl.BaseInterfaces: List<string>?`；`ParseInterfaceDecl` 把现"Skip"段
  改为收集 `ParseQualifiedName` 的名字（drop 泛型 args，与类接口名同款 bare→codegen 再 Qualify）。
- **EmitInterfaceDesc**：`Interfaces: iface.BaseInterfaces?.Select(n => QualifyClassName(n)).ToList()`
  （与类的接口块 FQ 名一致，#3）。
- **builtin_type_interfaces**（reflection.rs）：
  ```
  queue = 类 base 链收集的接口名; out=[]; seen={}
  while name = queue.pop:
      if !seen.insert(name): continue
      out.push(make_type_from_name(name))
      if itd = resolve(name): for bi in itd.interfaces(): queue.push(bi)   // 传递展开
  ```
- **is_subclass_or_eq_td / is_subclass_or_eq**：抽一个 `iface_implements(ctx/module, iface_name, target)`
  小 helper——BFS 接口基接口；类 base 链每层 `if iface_implements(i, target) return true`。
- **无 stdlib 改动**：GetInterfaces/IsAssignableFrom/is 的 z42 端不变，行为由 runtime 升级。

## Testing Strategy

- **golden（interp+jit）**：`transitive_interfaces.z42` ——
  `interface IA {}` `interface IB : IA {}` `class C : IB {}`：
  - `typeof(C).GetInterfaces()` 含 IA + IB；`typeof(IB).GetInterfaces()` 含 IA；
  - `c is IA` true（经 IB 传递）、`c is IB` true；`typeof(IA).IsAssignableFrom(typeof(C))` true；
  - 无关接口 `c is IC` false；非传递回归（直接接口）不变。
- **回归**：全量 dotnet GoldenTests（接口块填充不破坏既有）+ z42.core 重编（IEnumerator : IDisposable 现填充）。
- **GREEN**：cargo + dotnet + xtask vm/cross-zpkg/stdlib（regen 后；无 bump 无 version dance）。
