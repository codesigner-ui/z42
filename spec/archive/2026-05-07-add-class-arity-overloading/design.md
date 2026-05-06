# Design: add-class-arity-overloading

## Architecture

```
Source                Compiler registry          IR / VM
─────────             ──────────────────         ───────────
class Foo            → _classes["Foo"]          → IR class "Foo"
class Foo<T>         → _classes["Foo$1"]        → IR class "Foo$1"
class Pair<A, B>     → _classes["Pair$2"]       → IR class "Pair$2"

NamedType("Foo")     → _classes["Foo"]          (arity 0)
GenericType("Foo",1) → _classes["Foo$1"]
GenericType("Pair",2)→ _classes["Pair$2"]

Z42ClassType:
  Name        = "Foo"      ← 用户可见（诊断 / typeof）
  IrName      = "Foo$N"    ← IR / VM / 跨 zpkg 标识 (派生：non-generic 时 = Name)
  TypeParams  = [...]      ← arity 来源

IR / zpkg / VM 三端透明：所有 class 标识都是 IrName 字符串。
```

## Decisions

### Decision 1: IrName 派生 vs 显式存储

**问题**：`Z42ClassType.IrName` 是用 `get => TypeParams?.Count > 0 ? $"{Name}${TypeParams.Count}" : Name` 派生，还是作为独立 record 字段？

**决定**：选 **派生 property**。理由：
1. record 的不可变性 + ToString round-trip 不受影响
2. 不需要在所有构造点 / Combine / Empty 同步新字段
3. 计算成本零：每次访问只是读 TypeParams.Count 比较 + 字符串拼接
4. 与现有 `BoundDefault.Type => Target` 同款 record-derived 模式一致

### Decision 2: 何时 mangle — 永远 vs 仅冲突

**问题**：泛型类的 IR 名是**永远** `Name$N`，还是只在与同名非泛型冲突时 mangle？

**选项**：
- A. **永远 mangle 泛型** — `class List<T>` 永远 IrName="List$1"，无论是否有 `class List` 同 CU 共存
- B. **仅冲突时 mangle** — 没有 `class List` 时 List<T> IrName="List"；增加 `class List` 时退场为 List$1

**决定**：选 **A（永远 mangle）**。理由：
1. **一致性**：用户读 zpkg / VM 错误时永远看到一致的 IR 命名规则；选 B 会出现 "为什么 stdlib List 是裸名而我的 Box 加了 $1" 困惑
2. **可演进**：将来 stdlib 加 `class List` 不会触发 IR 名 silent 切换，导致旧 zpkg 不兼容
3. **诊断与 IR 解耦**：用户诊断永远走 `Name`，IR 永远走 `IrName`；两者分离边界清晰
4. **stdlib regen 无论如何都要做**（zpkg 二进制格式向 mangled 演进）；选 B 节省的 regen 工作量为 0

代价：所有 stdlib zpkg 重新生成；既有用户 zpkg 也需重编（与 z42 pre-1.0 "不兼容旧版本"约束一致）。

### Decision 3: 与 delegate `Action$N` 命名约定的对齐

z42 现有 delegate 注册早就是 arity-aware：`Action` (arity 0) / `Action$1` / `Action$2` ...。class 用同样语义保持术语一致：`Foo` (arity 0) / `Foo$1` / `Foo$2`。`$` 作为 mangling 分隔符不与任何 z42 标识符字符冲突（标识符不允许 `$`）。

### Decision 4: 跨 zpkg lookup —— ImportedSymbolLoader.Classes 的 key

ImportedSymbolLoader 把 zpkg TSIG 中的 ExportedClassDef 加载为 Z42ClassType。当前 `Classes: Dictionary<string, Z42ClassType>` 用 ExportedClassDef.Name 作 key。

**决定**：ImportedSymbolLoader 的 key 也升级为 IrName（即 `Name${TypeParams.Count}`）。这样：
- 用户 CU 引用 `MulticastAction<int>` → ResolveType GenericType → look up `_classes["MulticastAction$1"]` → 命中 imported
- 跨 zpkg 也能 register 同名不同 arity（虽然 stdlib 暂时没用此能力）

ExportedClassDef 序列化字段不变（已有 TypeParams），消费侧派生 IrName。

### Decision 5: typeof / 诊断 / ToString 用户面

`typeof(MyClass<int>)` 当前 desugar 到 `LitStrExpr` 直接由 parser 输出 Name 字符串（见 ExprParser.Atoms.cs::ParseTypeof）。

**决定**：parser 路径不变（仍取 NamedType.Name 而非 IrName）。`typeof(List<int>)` 输出 `"List"`（与现有用户 case 一致）；如果用户希望区分泛型实例化结果的字符串，独立 spec（`add-typeof-generic-fully-qualified`）扩展 — 不在本变更范围。

诊断同样：错误消息中 `_diags.Error(..., $"... \`{cls.Name}\` ...")` 不变；不引入 `cls.IrName` 进消息文案。Spec 场景"Diagnostics preserve user-facing name"由"`Name` 不被 mangle"自动满足。

### Decision 6: 本类自身方法体内 `_classes[currentClass.Name]` 查找的兼容性

风险：方法体内 TypeChecker 用 `env.CurrentClass` 字符串名（裸 `Foo`）查 `_classes`。当方法属于 `Foo<R>` 时，原来 `_classes["Foo"]` 命中（旧 key）；新方案 `_classes["Foo$1"]`，裸 lookup 会失败。

**决定**：所有内部使用 class **name 作 key 的查找点改为使用 IrName**。具体落点：
- `env.CurrentClass`（TypeEnv 字段）改存 IrName 而非 cls.Name（影响 TypeChecker.Stmts.cs 等多处）
- IrGen 中 `cls.Name` 用作 emit / lookup 的全部位点改为 `cls.IrName` 派生

实现策略：因 `Z42ClassType.Name` 不变，在 ClassDecl → Z42ClassType 注册时用 IrName 作 key；所有消费方原本写 `_classes[someClassName]` 的处需检查 `someClassName` 是来自 ClassDecl 的 `cls.Name`（裸）还是 Z42ClassType 的 IrName，**统一用 IrName**。

## Implementation Notes

### 关键文件改动顺序（pipeline 顺序，先编译器后 VM）

1. **Z42Type.cs**：`Z42ClassType` 增 `IrName` property
2. **SymbolCollector.Classes.cs**：class 注册全用 IrName key（pre-pass / collect / inheritance merge / duplicate detection）
3. **SymbolCollector.cs**：ResolveType GenericType 分支用 `$"{gt.Name}${gt.TypeArgs.Count}"` lookup；NamedType 保持裸 lookup
4. **SymbolTable.cs**：镜像 ResolveType 改动 + 任何 SymbolTable 内裸 lookup 改 IrName
5. **TypeChecker.* (Stmts / Exprs / Calls / GenericResolve / Operators)**：`env.CurrentClass` 改用 IrName；其它 `_symbols.Classes.TryGetValue(name, ...)` 处审查每条
6. **TypeEnv.cs**：CurrentClass 字段语义注释改为 IrName
7. **ImportedSymbolLoader.cs**：imported class 注册按 IrName key（同等改动 ImportedClassNamespaces 等并行 dict）
8. **IrGen.cs / FunctionEmitter*.cs**：emit class IR 名用 IrName；ObjNew / FieldGet / VCall / IsInstance / AsCast 接收的 className 全用 IrName
9. **TestAttributeValidator.cs**：`sem.Classes.TryGetValue(attr.TypeArg, ...)` 处理两种形态（attr.TypeArg 用户写的可能是 `MulticastException` 或 `MulticastException<R>`，按格式选 NamedType / GenericType 路径）

VM **没有**任何代码改动 — 它的 type_registry / class.name 已是字符串透明字段，IR 名变 mangled 后透明跟随。zbc / TSIG 同样透明（字符串字段）。

### Stdlib regen 必然要做

每个 stdlib 库（z42.core / z42.collections / z42.math / z42.io / z42.text / z42.test）含泛型类的都需要重新编译产出新 IR 名 zpkg。`regen-golden-tests.sh` 会触发 `build-stdlib.sh`，链路自动覆盖。

### 用户写 `MulticastException<R>` 但只有非泛型 `MulticastException` 存在的情况

ResolveType GenericType("MulticastException", [...]) → `_classes["MulticastException$1"]` 不存在 → 返回 Z42ErrorType / 报 type-not-found。这是预期行为：用户必须先声明泛型版本才能引用它。

类比：当前 NamedType("List") (无 type-args) → `_classes["List"]` 不存在（List 是泛型，IrName="List$1"）→ 报类型错误。一致。

## Testing Strategy

- **Golden run tests**:
  - `src/tests/classes/arity_overloading/` — `class Foo` + `class Foo<R>` 共存：`new Foo()`, `new Foo<int>(...)`, ToString 各自工作
  - `src/tests/classes/arity_method_dispatch/` — `class Pair<A, B>` 实例 method dispatch 通过 mangled name 正常

- **C# 单测** (`src/compiler/z42.Tests/ClassArityOverloadingTests.cs`):
  - `Z42ClassType.IrName` 派生正确（non-generic 同 Name；arity 1 / 2 / 3 加正确后缀）
  - SymbolCollector 注册：`class Foo` + `class Foo<R>` 同 CU → `_classes["Foo"]` 和 `_classes["Foo$1"]` 各 1 项；E0408 不触发
  - ResolveType：NamedType("Foo") 命中 arity 0；GenericType("Foo", [int]) 命中 arity 1
  - 同 arity 重复仍报 E0408（`class Foo<R>` + `class Foo<S>` 同 CU → duplicate）

- **回归**：
  - 现有所有 stdlib + tests（含 List<T> / Dictionary<K,V> / MulticastAction<T> 等）回归全过 — 验证 IrName 变更不破现有 zpkg 路径
  - `regen-golden-tests.sh` 必须重新跑，让 stdlib zpkg 用新 IR 名
- **JIT + interp 双 mode** + cross-zpkg + cargo test 全绿
