# z42 编译器内部实现原理

> **目的**：记录 C# bootstrap 编译器的内部数据结构、算法、加载策略与关键设计决策。
> 让新接手者不必阅读大量源码即可理解"为什么这样设计"。
> 面向语言设计师和编译器开发者，不面向 z42 语言使用者。
>
> 使用者视角请看 `docs/design/language-overview.md`、`docs/design/compilation.md`。

---

## Pipeline 顶层流程

```
source.z42
  │
  ├── Lexer (TokenDefs + LexCombinators)          [z42.Syntax/Lexer]
  │      → Tokens
  │
  ├── Parser (Pratt + 组合子)                     [z42.Syntax/Parser]
  │      → CompilationUnit (AST, sealed record)
  │
  ├── TypeChecker (SymbolCollector + SymbolTable) [z42.Semantics/TypeCheck]
  │      → SemanticModel
  │      ↑ 读：ImportedSymbolLoader from TsigCache
  │
  ├── IrGen (FunctionEmitter + Ctx)               [z42.Semantics/Codegen]
  │      → IrModule (registers SSA-ish)
  │
  └── ZbcWriter / ZpkgWriter                       [z42.IR/BinaryFormat]
         → source.zbc / package.zpkg
```

单文件模式走 `SingleFileCompiler`；项目模式（`.z42.toml`）走 `PackageCompiler`，
两者共享 `PipelineCore` 的 TypeCheck + Codegen 阶段。

Workspace 模式（`z42.workspace.toml`）走 `ManifestLoader` 发现 + 共享继承（C1）→
后续 `WorkspaceBuildOrchestrator` 拓扑编译（C4）。

---

## Parameter Modifiers（`ref` / `out` / `in`，2026-05-05）

**编译期数据流**（运行时 codegen / VM 部分留给 follow-up spec `impl-ref-out-in-runtime`）：

```
源码:   void Foo(ref int x)           // signature 修饰符
        Foo(ref c)                    // callsite 修饰符 + `out var x` 内联声明

Lexer:  KW_REF / KW_OUT tokens
        (KW_IN 复用 foreach `in` token)
        ↓
AST:    Param { Name, Type, Default, Span, Modifier: ParamModifier (None/Ref/Out/In) }
        ModifiedArg { Inner: Expr, Modifier: ArgModifier, OutDecl: OutVarDecl?, Span }
        OutVarDecl { Name, AnnotatedType, Span }    // `out var x` 内联声明
        ↓
SymbolCollector:
        Z42FuncType { Params, Ret, RequiredCount, ParamModifiers? }
        ModifierMangling: 同 (Name, IsStatic, Arity) 组内 modifier 不同时启用
                         `Name$Arity$<modSig>` 注册键（class methods + free functions）
        ↓
TypeChecker:
        BoundModifiedArg { Inner, Modifier, OutDecl, Type, Span }
        BoundOutVarDecl { Name, Type, Span }
        TypeEnv.DefineParamModifier / LookupParamModifier
        - 修饰符一致性（CheckArgModifiers 12 处调用点）
        - lvalue 校验（IsLvalueForRef）
        - 严格类型匹配（CheckArgTypes 检测 BoundModifiedArg → exact equality）
        - LookupMethodOverload：modifier-tagged key 优先 → bare → arityKey
        - DA 扩展（FlowAnalyzer）：caller post-call + callee normal-return（throw 路径除外）
        - lambda 捕获禁止 + `in` 写保护（BindAssign 检测）
        - 占位拒绝：async / iterator + ref/out/in 参数（特性引入时再放开）
        ↓
IR Codegen（当前过渡期不变；follow-up spec 实施）：
        - 检测 BoundModifiedArg → emit LoadLocalAddr / LoadElemAddr / LoadFieldAddr
        - Call 指令携带 ref-aware 标志或新格式
        ↓
VM interp（当前过渡期不变；follow-up spec 实施）：
        - Value::Ref { kind: RefKind { Stack, Array, Field } }
        - frame.get/set 透明 deref 检测 Value::Ref
        - GC mark 包含 Value::Ref
```

**关键文件**：
- AST：`src/compiler/z42.Syntax/Parser/Ast.cs`（Param.Modifier / ModifiedArg / OutVarDecl）
- Mangling：`src/compiler/z42.Semantics/TypeCheck/Z42Type.cs`（`ModifierMangling` 静态类）
- 一致性检查：`src/compiler/z42.Semantics/TypeCheck/TypeChecker.Calls.cs`（CheckArgModifiers / IsLvalueForRef / LookupMethodOverload）
- DA：`src/compiler/z42.Semantics/TypeCheck/FlowAnalyzer.cs`（CheckDefiniteAssignment 接 functionParams）
- 写保护：`src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.Operators.cs`（BindAssign 检测 in param）
- env 修饰符表：`src/compiler/z42.Semantics/TypeCheck/TypeEnv.cs`（DefineParamModifier / LookupParamModifier）

**用户视角规范**：[parameter-modifiers.md](parameter-modifiers.md)

---

## ManifestLoader（C1，2026-04-26）

位置：`src/compiler/z42.Project/`

**职责**：解析 `z42.workspace.toml` + member `<name>.z42.toml` 文件，
应用 workspace 共享继承，输出每个 member 的 `ResolvedManifest`（含字段来源链）。

### 核心模块

| 模块 | 职责 |
|---|---|
| `ManifestLoader` | 入口：发现 workspace 根（向上找）→ 加载 + 合并 + 校验 |
| `WorkspaceManifest` | `z42.workspace.toml` 数据模型（`[workspace.*]` / `[profile.*]` / `[policy]`）|
| `MemberManifest` | member `<name>.z42.toml` 数据模型，含 `FieldRef<T>` 表达 `xxx.workspace = true` 引用 |
| `ResolvedManifest` | 合并后最终配置 + `Origins` 字典（每字段记录来源） |
| `GlobExpander` | members 的 `*` / `**` 目录级 glob 展开 |
| `PathTemplateExpander` | 4 内置变量（`${workspace_dir}` / `${member_dir}` / `${member_name}` / `${profile}`）展开 + `$$` 转义 |
| `ManifestErrors` | 错误码工厂（WS003/005/007/010/011/020-024/030-039） |
| `IncludeResolver` | DFS 展开 include 链（C2） |
| `ManifestMerger` | 合并语义实现（C2） |
| `PolicyEnforcer` | workspace [policy] 锁定字段冲突检测（C3） |
| `PolicyFieldPath` | 字段路径解析 + fuzzy 建议（C3） |
| `CentralizedBuildLayout` | 集中产物路径派生（C3） |
| `MemberDependencyGraph`（z42.Pipeline，C4a） | 跨 member 依赖图 + DFS 三色环检测 + 拓扑层 |
| `WorkspaceBuildOrchestrator`（z42.Pipeline，C4a） | 串行编排 workspace 编译 + WS001/002/006 |
| `PackageCompiler.RunResolved`（C4a） | 接受 ResolvedManifest 的编译入口（供 orchestrator 调用） |
| `CliOutputFormatter`（z42.Driver，C4b） | ManifestException 友好渲染（颜色 + Reset/Bold/Dim） |
| `QueryCommands`（z42.Driver，C4b） | info / metadata / tree / lint-manifest 四个命令；MetadataDto JSON schema_version="1" |
| `ScaffoldCommands`（z42.Driver，C4c） | new / init / fmt 命令；含内联 Templates（Workspace / Gitignore / LibPreset / ExePreset / MemberManifest / 源文件骨架） |
| `BuildCommand.TryCleanWorkspace`（C4c） | clean 命令的 workspace 模式分派（集中清理 / -p per-member） |
| `IncrementalBuild`（z42.Pipeline，C5） | source_hash 增量编译查询：比对当前 .z42 hash 与上次 zpkg 记录，分流 cached / fresh；命中时复用 cache zbc bytes + 上次 zpkg 的 ExportedModules + Dependencies |
| `ZpkgBuilder.BuildPacked` cache zbc fullMode（C5） | packed 模式 cache zbc 用 ZbcFlags.None 写（含 SIGS / TYPE / EXPT / IMPT），让 ZbcReader.Read 单独反序列化为完整 IrModule；BuildIndexed 仍 stripped（zpkg.files[] 引用，VM 通过 zpkg 全局 SIGS 加载） |
| `ZpkgReader.ReadSourceHashes`（C5） | 跨 indexed/packed 读出每文件的 (sourceFile, sourceHash, namespace) 三元组，IncrementalBuild 用作命中判定 |

### 加载流程（C1+C2 范围）

```
ManifestLoader.LoadWorkspace(ctx, profile)
  ├─ 1. WorkspaceManifest.Load()
  │     - 强制文件名 z42.workspace.toml （WS030）
  │     - 检查 virtual manifest（WS036）
  │     - 解析 [workspace.project] 字段集合（WS033）
  │     - 解析 [workspace.dependencies] / [workspace.build] / [policy] / [profile.*]
  ├─ 2. GlobExpander.Expand()
  │     - members glob → 目录列表
  │     - exclude 过滤
  │     - 同目录两 manifest → WS005
  ├─ 3. default-members 校验（WS031）
  ├─ 4. 对每个 member：
  │     a. MemberManifest.Load()（含 WS003 段限制 + WS035 旧语法检测）
  │     b. ★ C2: IncludeResolver.Resolve()
  │           - DFS 展开 include 链 → preset 列表（按合并顺序）
  │           - 路径合法性（WS023/024） / 循环（WS020） / 深度（WS022）
  │           - PresetValidator 校验段限制（WS021）
  │     c. ★ C2: ManifestMerger.Merge(presets, self)
  │           - 标量后者覆盖 / 表字段级合并 / 数组整体覆盖
  │           - Origins 字典记录每字段的最终来源
  │     d. ResolveMember()：展开 .workspace = true 引用（WS032/WS034）
  │     e. PathTemplateExpander 应用到路径字段（WS037/038/039）
  │     f. ★ C3: CentralizedBuildLayout.Resolve()
  │           - workspace 模式 → dist/<member>.zpkg + cache/<member>/
  │           - 单工程 → fallback 到 member-local
  │     g. ★ C3: PolicyEnforcer.Enforce()
  │           - 默认锁定 build.out_dir / build.cache_dir
  │           - 显式 [policy] 段（WS011 fuzzy 建议）
  │           - 仅检查 member 显式声明的字段（origin = MemberDirect/IncludePreset）
  │           - 一致 → Origins 标 PolicyLocked；冲突 → WS010
  │     h. 输出 ResolvedManifest + Origins
  └─ 5. orphan 检测（WS007 warning）
```

### 字段来源链（Origins）

`ResolvedManifest.Origins` 是 `Dictionary<string, FieldOrigin>`，记录每个字段的最终来源：

| OriginKind | 含义 | C 阶段 |
|---|---|---|
| `MemberDirect` | 由 member 自身 manifest 直接声明 | C1 |
| `WorkspaceProject` | 通过 `xxx.workspace = true` 引用 `[workspace.project]` | C1 |
| `WorkspaceDependency` | 通过 `xxx.workspace = true` 引用 `[workspace.dependencies]` | C1 |
| `IncludePreset` | 由 include 链中某 preset 提供 | C2 |
| `PolicyLocked` | 被 workspace `[policy]` 锁定 | C3 |

C4 的 `z42c info --resolved` 直接消费此字段输出"字段 + 来源 + 🔒 标记"。

### 与现有 ProjectManifest 的关系

- 现有 `ProjectManifest`（单工程 `.z42.toml` 路径）保留**不动**，行为与之前一致
- 新增 workspace 路径并行存在：CLI 入口判断"是否含 `z42.workspace.toml`"决定走哪条
- `ManifestException` 复用现有类型；C1 的工厂方法 `Z42Errors.*` 在 message 中携带 `WSxxx` 码 + 上下文，不破坏现有调用方

### 设计决策

详见 `spec/archive/2026-04-26-extend-workspace-manifest/design.md`：

- **D1**：workspace 根文件名固定 `z42.workspace.toml`
- **D2**：virtual manifest 强制（root 不可兼任 member）
- **D3**：依赖语法对齐 Cargo（`xxx.workspace = true`）
- **D4**：members 支持 glob + exclude
- **D5**：`[workspace.project]` 仅 4 字段可共享（`version` / `authors` / `license` / `description`）
- **D6**：路径字段支持有限模板变量（4 内置只读，禁止用户自定义）

---

## TSIG 与跨包符号导入

### TSIG（Type Signature）section

zpkg 的 `TSIG` section 存储**包内所有 public 类型的结构化元数据**（类 / 接口 / 枚举 / 函数签名 / 约束），
供其他包 **编译期** 消费。运行时不使用 TSIG —— 运行时走 zbc 里的 `Function` + `TypeDesc`。

**形状（简化）：**

```csharp
sealed record ExportedModule(
    string                          Namespace,
    List<ExportedClassDef>          Classes,
    List<ExportedInterfaceDef>      Interfaces,
    List<ExportedEnumDef>           Enums,
    List<ExportedFunctionDef>       Functions
);
```

一个 zpkg 可含多个 `ExportedModule`（每个源文件一个）；同一包内不同 namespace 的源文件分别成组。

### TsigCache

位置：`src/compiler/z42.Pipeline/PackageCompiler.cs`

**职责**：编译器加载依赖包的 TSIG 元数据，按需供给 `ImportedSymbolLoader`。

**核心数据结构：**

```csharp
// namespace → list of zpkg full paths (2026-04-25 起改为 List<string>)
private readonly Dictionary<string, List<string>> _nsToPaths;
// zpkg path → 已加载 TSIG modules（懒加载 + 缓存）
private readonly Dictionary<string, List<ExportedModule>> _cache;
```

**加载流程：**

1. `ScanLibsForNamespaces`（在 `PackageCompiler.BuildTarget` 启动时）：扫描 `libs/` 下所有 `.zpkg`，
   读取每个 zpkg 的 `NSPC` section（namespaces 列表），对每个 namespace 调 `RegisterNamespace(ns, path)`。
2. `RegisterNamespace(ns, path)`：追加到 `_nsToPaths[ns]` 列表；**允许同一 namespace 多 zpkg 共享**。
3. `LoadForUsings(usings)` / `LoadAll()`：根据 `using` 声明（或全部已注册）聚合所有相关 zpkg 路径，
   首次访问时 `LoadZpkg` 解码 `TSIG` section，结果进 `_cache` 复用。

### 设计决策：namespace 可跨 zpkg（2026-04-25 vm-zpkg-dependency-loading）

**为什么**：对齐 C# assembly 模型 —— `System.Collections.Generic` 在 C# 里物理分布在 `System.Private.CoreLib`
和扩展 assembly。z42 stdlib 同样：`Std.Collections` 下的 `List<T>` / `Dictionary<K,V>` 驻留
`z42.core.zpkg`（实现 prelude 体验），而 `Queue<T>` / `Stack<T>` 驻留 `z42.collections.zpkg`。

**实现关键**：`_nsToPaths` 必须是 `Dictionary<string, List<string>>`（不是
`Dictionary<string, string>` + first-wins）；否则后来的 zpkg 被静默丢弃，导致
`QualifyClassName` 在用户代码引用 Stack / Queue 时找不到 namespace 映射、
IR 生成 bare 名 `Stack` 而非 FQ 名 `Std.Collections.Stack`，运行时 VCall 失败。

**对称性**：VM 侧 `LazyLoader` 同步支持多 zpkg 共享 namespace —— 两端必须一致。

---

## 符号解析与 QualifyClassName

### ImportedSymbolLoader

位置：`src/compiler/z42.Semantics/TypeCheck/ImportedSymbolLoader.cs`

**职责**：把 TSIG `ExportedModule` 列表合并为一组 `ImportedSymbols`，
供 `SymbolCollector` / `TypeChecker` 消费。

**关键字段：**
- `classes: Dictionary<string, Z42ClassType>` — 类名 → 重建后的类类型
- `classNs: Dictionary<string, string>` — 类名 → **简名到 namespace 的映射**（QualifyClassName 靠它）
- `interfaces`, `enumConsts`, `enumTypes`
- `classConstraints`, `funcConstraints` — L3-G3d 泛型约束（延迟解析到 bundle）

**冲突策略（first-wins）**：`if (!classes.ContainsKey(cls.Name))` —— 不同 zpkg 同名类保留先加载的。
预期同名冲突不应发生；若出现只在编译器层面 silent，运行时仍按 zbc func name 走。

### 两阶段加载（2026-04-26 fix-imported-type-two-phase-resolution）

`ImportedSymbolLoader.Load` 内部分两阶段处理类与接口，消除 self / forward
reference 的"未知名 → Z42PrimType 降级"。

```
Phase 1 — 骨架登记
  对每个 ExportedClass / ExportedInterface 创建空成员 Z42ClassType /
  Z42InterfaceType，仅填 Name + TypeParams + BaseClassName；登记进
  classes / interfaces 字典。Phase 1 内部不调 ResolveTypeName，
  没有跨类型 reference 解析。

Phase 2 — 成员填充（in-place mutate）
  对每个骨架，调 FillClassMembersInPlace / FillInterfaceMembersInPlace，
  解析 Fields / Methods 签名时通过 ResolveTypeName 在 Phase 1 完整字典里
  lookup —— 命中 → 返回真实 Z42ClassType / Z42InterfaceType；未命中 →
  Z42PrimType（真未知，TypeChecker 后续报错）。

  关键点：成员字典是 mutable Dictionary，Phase 2 直接 Add 进同一字典实例，
  不替换 Z42ClassType record。Phase 2 内 ResolveTypeName 拿到的 ClassType
  与最终 ImportedSymbols 输出的 ClassType 是同一引用 —— 字段填充后所有持有
  该引用的位置都看到完整 Fields / Methods（C# record 的 immutability 不
  影响 dict 内容）。
```

**为什么需要两阶段？**

旧实现单阶段：`RebuildClassType(cls)` 解析 cls.Fields 时，classes 字典还
没填到 cls 自己。`Exception.InnerException: Exception` 字段类型在
ResolveTypeName 里走 `_ => new Z42PrimType(name)` fallback，被降级。
下游 IsAssignableTo 比较 `Z42ClassType vs Z42PrimType` same-name 不通过 →
用户代码 `outer.InnerException = inner` 报 E0402。

按 [.claude/rules/workflow.md "修复必须从根因出发"](.claude/rules/workflow.md#修复必须从根因出发-2026-04-26-强化)，
**禁止在 IsAssignableTo 加 PrimType↔ClassType 同名兼容分支**（症状级补丁）。
两阶段加载是经典 C# / Java 编译器的"先建骨架再填字段"做法，从源头物理消除降级。

**ResolveTypeName 签名**：

```csharp
internal static Z42Type ResolveTypeName(
    string                                         name,
    HashSet<string>?                               genericParams = null,
    IReadOnlyDictionary<string, Z42ClassType>?     classes       = null,  // Phase 2 必传
    IReadOnlyDictionary<string, Z42InterfaceType>? interfaces    = null); // Phase 2 必传
```

Lookup 优先级：suffix (`[]` / `?`) > generic param > 内置 prim > classes > interfaces > Z42PrimType fallback。

### QualifyClassName

位置：`src/compiler/z42.Semantics/Codegen/IrGen.cs:58`

```csharp
string QualifyClassName(string className) {
    // local class shadows imported
    if (sem.Classes.ContainsKey(className) && !sem.ImportedClassNames.Contains(className))
        return QualifyName(className);
    return sem.ImportedClassNamespaces.TryGetValue(className, out var ns)
        ? $"{ns}.{className}" : QualifyName(className);
}
```

**作用**：ObjNew / Call / VCall 生成 IR 时把简名（用户代码写 `new Stack<int>()`）
resolve 到 FQ 名 `Std.Collections.Stack`，让 VM 能正确查 `func_index`。

**失败模式**：若 `sem.ImportedClassNamespaces` 没有该类（TsigCache 漏载、
TSIG 解码失败、namespace 过滤不含其 namespace 等），fallback 到
`QualifyName(className)`（用当前文件 namespace）→ 生成 bare 名 → 运行时
`function not found`。

---

## pseudo-class 策略与迁移

历史上 `Console` / `Math` / `Assert` / `Convert` / `String.*` 方法、`List<T>` / `Dictionary<K,V>`
由编译器的 **pseudo-class 机制**（`BuiltinTable.cs`）直接解析到 VM builtin，绕过 stdlib 加载。

**现状（2026-04-26）**：
- `Console` / `Math` / `Assert` / `Convert` / `String.*`：已迁移到 stdlib `.z42` 源码（L2 M7）
- `List<T>` / `Dictionary<K,V>`：已迁移到 `z42.core/src/Collections/`（L3-G4h step3）
- `StringBuilder`：已迁移到 `z42.text/StringBuilder.z42` 纯脚本实现
  （2026-04-26 script-first-stringbuilder）— 移除 IrGen `case "StringBuilder"`
  特例 + VM `__sb_*` 6 个 builtins + `NativeData::StringBuilder` 变体
- **残留兜底**：`SymbolCollector.cs:208-209` 仍有 `"List" => Z42PrimType("List")` 和
  `"Dictionary" => Z42PrimType("Dictionary")` 硬编码映射 —— 作为"TypeEnv.BuiltinClasses
  动态注入"未完成前的 bridge，计划在 L3-G 泛型类型表示扩展时清理（roadmap L2 backlog）
- **仅剩 pseudo-class**：`Array`（VM 原生 Value variant，不是 z42 class，不计入迁移目标）

---

## 跨 zpkg `impl` 块传播 — IMPL section + Phase 3 merge（2026-04-26 cross-zpkg-impl-propagation）

### 背景

L3-Impl1 让 `impl Trait for Type { ... }` 在同 CU 内工作（SymbolCollector 把
impl 方法合并进 target class 的 `Methods` 字典 + trait 加到
`_classInterfaces[target]`），但**跨 zpkg 不可见**：z42.numerics 给 z42.core
的 `int` 实现 `INumber<int>`，下游消费者读 z42.core TSIG 看不到这个 trait
→ `where T: INumber<int>` + `int` 类型实参编译报错。

### IMPL section（zpkg v0.8）

zpkg 加新 section `IMPL`：每个 ExportedModule 携带本 CU 的 `impl Trait for Type`
列表（仅 declarations，方法 body 仍走 MODS section）。

```
IMPL section layout
─────────────────────
[Module Count: u16]
For each module:
    [Namespace pool idx: u32]   ← 与 TSIG 模块顺序一致（positional matching）
    [Impl Count: u16]
    For each impl:
        [Target FQ name pool idx: u32]   ← 例如 "Std.int"
        [Trait FQ name pool idx: u32]    ← 例如 "Std.INumber"
        [Trait TypeArg Count: u8]
        For each trait type arg: [Type string pool idx: u32]
        [Method Count: u16]
        For each method: WriteMethodDef(...)   ← 复用 TSIG 的 MethodDef 序列化
```

**重要**：IMPL 模块顺序与 TSIG 模块顺序一致（positional matching），不能按
namespace 索引 —— 一个包内多个 .z42 文件可能共享 namespace（z42.core 的所有
文件都用 `Std`），按 namespace 唯一索引会撞键。

### ImportedSymbolLoader Phase 3

`ImportedSymbolLoader.Load` 由两阶段扩展为三阶段：

```
Phase 1 — 骨架登记                ← 已有
Phase 2 — 成员填充                ← 已有
Phase 3 — impl merge (NEW)
  foreach module.Impls:
    targetClass = classes[short_name(impl.TargetFqName)]
    foreach method in impl.Methods:
      targetClass.Methods.TryAdd(method.Name, method.Sig)   // first-wins
    classInterfaces[short_name].Add(impl.TraitName)         // dedupe
```

冲突策略：first-wins（与 SymbolCollector.MergeImported `TryAdd` 一致）。
`target` FQ 名通过 `SplitFqName` 拆 `Std.int` → namespace `Std` + short `int`，
仅在 namespace 匹配 `classNs[short]` 时才合并（避免不同包同名类污染）。

### IrGen — QualifyClassName 对齐 imported target

`IrGen.cs` 早先用 `QualifyName(targetNt.Name)` 给 impl 方法注册 funcParams
和生成方法 body 的 IR 函数符号。当 target 是 imported（如 z42.numerics 给
z42.core `int` 加方法），这会把方法生成到错误命名空间（`numerics.int.op_Add`
而非 `Std.int.op_Add`），导致 VM `func_index` 注册符号与消费者 VCall 期望
不一致。修复：改用 `((IEmitterContext)this).QualifyClassName(...)`，imported
target 走 source namespace，local target 行为不变（等同 QualifyName）。

### VM 端零改动

方法 body 走 z42.numerics 自己的 MODS section，函数符号 `Std.int.op_Add`。
当用户代码 `using z42.numerics`，lazy loader 注册该 zpkg 所有函数到 `func_index`，
VCall(int_obj, "op_Add") 通过 `primitive_class_name(I64) = "Std.int"` + method
拼出 `Std.int.op_Add` → 命中 z42.numerics body。VM decoder 不需要解析 IMPL
section（基于 tag 查找天然跳过未识别 section）。

### 兼容性

zbc version 0.7 → 0.8。pre-1.0 规则：旧 zbc 不可读，需要 `regen-golden-tests.sh`
重生。

---

## 泛型接口 dispatch — Z42InterfaceType.TypeParams（2026-04-26 fix-generic-interface-dispatch）

### 背景

调用泛型接口方法时（`IEquatable<int>.Equals(T other)`），TypeChecker 必须
把方法签名里的 type-param `T` 替换成具体 TypeArg `int`，否则 arg 类型检查
会用未替换的 `T` 与实参 `int` 比较失败 → 报"argument type mismatch"。

C#/Java 的做法：泛型接口持有 TypeParams（声明列表）+ TypeArgs（实例化值），
dispatch 时构造 `TypeParams[i] → TypeArgs[i]` map，对方法签名做
`SubstituteTypeParams`。

### 关键结构

```csharp
public sealed record Z42InterfaceType(
    string Name,
    IReadOnlyDictionary<string, Z42FuncType> Methods,
    IReadOnlyList<Z42Type>? TypeArgs = null,            // 实例化时填
    IReadOnlyDictionary<string, Z42StaticMember>? StaticMembers = null,
    IReadOnlyList<string>? TypeParams = null);          // 声明列表
```

`TypeParams` 在所有构造点都必须从源头填入：

- `SymbolCollector.CollectInterfaces` — 从 `InterfaceDecl.TypeParams`
- `ImportedSymbolLoader.BuildInterfaceSkeleton` — 从 `ExportedInterfaceDef.TypeParams`
- `ExportedTypeExtractor.ExtractInterfaces` — 从 `Z42InterfaceType.TypeParams` 反向写入 TSIG
- `SymbolTable.ResolveGenericType` — 实例化时**保留 def.TypeParams**

### dispatch substitute

- `TypeChecker.BuildInterfaceSubstitutionMap(ifaceType)` — 由 TypeParams ↔
  TypeArgs 构造 substitution map
- `TypeChecker.Calls.cs` 接口方法调用分支：`imt → SubstituteTypeParams(imt, subMap)`
  再做 arg/ret 类型检查
- `TypeChecker.Exprs.cs` 接口属性 getter 同步替换返回类型

### 赋值兼容（TypeArgs-aware）

`Z42ClassType / Z42InstantiatedType → Z42InterfaceType` 路径不能仅按
**接口名** 比较 —— 必须比较 TypeArgs。`ClassImplementsInterfaceWithArgs`
通过 `_classInterfaces` 取出 class 实现的具体 `Z42InterfaceType`（含
TypeArgs），再用 `InterfacesEqual` 名 + 全部 TypeArg 类型比对。这避免
`class Foo : IEquatable<int>` 错误地被当作 `IEquatable<string>`。

---

## 多 CU 包内 symbol 共享（2026-04-26 fix-package-compiler-cross-file）

### 背景

`PackageCompiler` 编译一个 zpkg 时通常涉及多个源文件（CU）。同包内的 CU
互相引用（`z42.core/src/Exceptions/ArgumentNullException.z42` 继承
`Exceptions/ArgumentException.z42`，再继承 `Exception.z42`）。早期实现中
每个 CU 独立 `CompileFile`，`MergeImported` 仅加载 **外部已 build 的
zpkg**（`tsigCache.LoadAll()`），同包内未编译完的文件互不可见。开发者本地
有残留 zpkg cache 时偶然能跑通；清空 cache 后 stdlib 自启动失败：

```
ArgumentNullException.z42(8,44): error E0402:
  type `ArgumentNullException` has no member `Message`
```

### 两阶段编译

`PackageCompiler.TryCompileSourceFiles`（`src/compiler/z42.Pipeline/PackageCompiler.cs`）：

```
Phase 1 — 同包 declarations 预收集
  externalImported = ImportedSymbolLoader.Load(tsigCache.LoadAll(), allNs)
  sharedCollector  = new SymbolCollector(...)
  foreach cu in parsedCus:
    sharedCollector.Collect(cu, externalImported)   // Pass-0 only，无 body bind
  sharedCollector.FinalizeInheritance()             // 全局拓扑合并继承字段/方法
  intraSymbols = sharedSymbols.ExtractIntraSymbols(ns)  // 仅本包 declarations

Phase 2 — 完整编译
  combined = ImportedSymbolLoader.Combine(externalImported, intraSymbols)
             // intraSymbols 优先（本包覆盖外部同名）
  foreach cu in parsedCus:
    CompileFile(cu, combined)   // TypeCheck + IR + zbc，body binding 全做
```

### 关键不变量

- 每个 CU 的 sem 仍独立（body binding / IR 各自做）
- intraSymbols 仅含 declarations（class shape、interface methods sig、enum、
  free function sig），不含具体方法 body 或 IR
- 跨 CU 同名声明：first-wins（与现有 ImportedSymbols 合并语义一致）
- intraSymbols 不写入 zpkg TSIG（仅本次 build 使用，next build 从 zpkg 加载）
- 本包优先：`Combine(externalImported, intraSymbols)` 时 intraSymbols 覆盖
  externalImported 的同名条目（防止 stale zpkg 提供旧版 declaration）

### SymbolCollector.FinalizeInheritance

`Collect` 的 per-CU 第二阶段（`SymbolCollector.Classes.cs`）只能合并已经在
`_classes` 中的 base class 字段；当 CU 处理顺序与继承顺序不一致（按文件名
字母序：`Exceptions/ArgumentNullException.z42` 早于 `Exception.z42`），
派生类的 base 还没注册 → 合并被静默跳过 → intraSymbols 中派生类的 Fields
不含继承字段。多级继承（`ArgumentNullException → ArgumentException →
Exception`）会因任意一级断链而丢失 `Message` 字段。

`FinalizeInheritance()` 在 Phase 1 全部 CU 收集完成后做一次 **全局拓扑
合并**：递归 `FinalizeInheritanceOne(name)` 先处理 base 再合并 self，用
`done` HashSet 保证幂等。运行后 `_classes` 中每个 class 的 Fields/Methods
都已展开完整继承链，与单 CU + cached zpkg 路径一致。

### SymbolTable.ExtractIntraSymbols

`SymbolCollector` / `SymbolTable` 跟踪 `ImportedClassNames` /
`ImportedInterfaceNames` / `ImportedFuncNames` / `ImportedEnumNames`
（`MergeImported` 时 TryAdd 成功即记入），`ExtractIntraSymbols(ns, classNamespaces?)`
据此过滤掉外部 imported 的条目，只输出本包 declarations。返回的
`ImportedSymbols` 直接喂给 Phase 2 的 `Combine`。

**Per-class namespace（fix-generic-type-roundtrip 2026-04-28 引入）：**
同一个 package 可以包含多个 namespace（例：`z42.core` 同时含 `Std`、
`Std.Collections`、`Std.IO`）。`ExtractIntraSymbols` 接受可选
`classNamespaces` map（class 短名 → 该 class 实际声明所在 namespace），
按 class 单独写入 `ImportedSymbols.ClassNamespaces`；缺省时回退到
`ns` 参数（向后兼容单 namespace package）。

PackageCompiler 在 Phase 1 收集所有 CU 后扫描 `cu.Classes` 构建该 map：

```csharp
var classNamespaces = new Dictionary<string, string>(...);
foreach (var (_, _, cu, ns) in parsedCus)
    foreach (var cls in cu.Classes)
        classNamespaces.TryAdd(cls.Name, ns);
intraSymbols = sharedSymbols.ExtractIntraSymbols(firstNs, classNamespaces);
```

**为何重要**：`IrGen.QualifyClassName` 用 `ImportedClassNamespaces[name]`
拼 obj.new / vcall 的 fully-qualified target。错误的 namespace prefix（例：
`Std.KeyValuePair` vs 正确的 `Std.Collections.KeyValuePair`）会让 runtime
找不到类型，构造器静默不写字段、`.Value` 全是 null。修复前 z42.core
内 `Dictionary.Entries()` 调 `new KeyValuePair<K,V>(...)` 即触发该 bug；
修复后跨 namespace 同包引用走对前缀。

### 兼容性

- 仅在 PackageCompiler 路径生效；`SingleFileCompiler` 保持原样
- Cross-package 引用仍走 `tsigCache` 加载 zpkg（外部依赖不变）
- `intraSymbols` 不写盘，每次 build 重新计算

---

## DependencyIndex 与 Import 解析

位置：`src/compiler/z42.IR/DependencyIndex.cs`

**职责**：给定 `using Std.Collections;` 一条语句，TypeChecker 需要：
1. 确认该 namespace 存在（不存在 → 错误 Z1xxx）
2. 把该 namespace 标为"可见" → `ImportedSymbolLoader` 按此过滤 TSIG

`DepIndex`（`TypeChecker` 的构造参数）映射 namespace → 是否已知可用。
由 `ScanLibsForNamespaces` / `ScanZbcForNamespaces` 构建。

---

## Pratt 表达式解析

位置：`src/compiler/z42.Syntax/Parser/ExprParser.cs`

手写组合子（参考 Datalust/Superpower 设计），不引入外部 parser combinator 库
（为自举保留）。核心：

- **NudTable**（null denotation）：从哪个 token **开始** 表达式 —— 字面量、前缀运算符、`(`、`new`、lambda 等
- **LedTable**（left denotation）：在已有表达式后接什么 token —— 二元运算符、`.`（member）、`[`（index）、`(`（call）、`?:`、`switch`、postfix `++`/`--`

**优先级**（binding power）：见 `.claude/rules/compiler-csharp.md` 的 Pratt 表。

### Z42Type record 结构 equality（2026-05-03 fix-z42type-structural-equality）

C# `record` 默认 `Equals` 对 `IReadOnlyList<T>` 字段做**引用比较**而非元素级。
对于持 `IReadOnlyList<Z42Type>` 的类型 record 这是 bug —— 同结构两个不同
list 对象的实例报"不相等"，污染 `==`、HashSet/Dict key、`IsAssignableTo`
触底比较。

修复：[Z42Type.cs](../../src/compiler/z42.Semantics/TypeCheck/Z42Type.cs)
三个 record (`Z42InstantiatedType` / `Z42InterfaceType` / `Z42FuncType`)
override `Equals(SameType?)` + `GetHashCode()`，list 字段元素级递归
`Z42Type.Equals`。提供 `Z42Type.ListEquals<T>` 静态助手统一逻辑。

`Z42InterfaceType.Methods` / `StaticMembers` 字典字段仍走默认引用比较 ——
实践中 interface name 唯一确定其方法集，同名两次构造往往共享字典对象。
未发现 bug 前不引入字典深比成本。

`IsAssignableTo` 中既有的 `Z42FuncType` / `Z42InstantiatedType` element-wise
workaround 分支（line 63-74 / 82-86）保留作防御性 + 子类型放宽（`IsAssignableTo`
而非 `Equals` 递归）；删除评估留独立 cleanup spec。

### 嵌套 generic `>>` 拆分（2026-05-03 fix-nested-generic-parsing）

Lexer 把 `>>` 词法化为单一 `GtGt` token（shift-right operator）。`TypeParser`
解析嵌套 generic 如 `Foo<Bar<T>>` 时不能"两次 `Gt` 关闭"，需在 parser 端拆分。

**算法**（[TypeParser.cs](../../src/compiler/z42.Syntax/Parser/TypeParser.cs)）：
内部 `ParseInternal()` 返回一个 `ExtraClose: bool` flag。当 generic 关闭检查
遇到 `GtGt` 时 consume 整个 token，置 `ExtraClose=true` 上传；调用方收到则
视自己的关闭已被吸收（GtGt 用 1 个 `>` 关闭自己 + 1 个 `>` 关闭上层），不再
推进 cursor 也不处理 `[]` `?` 后缀（它们属于上层）。

**Depth-scan 站点**（5 处 lookahead helper：`SkipGenericParams` / `IsFieldDecl`
/ `IsLocalFunctionDecl` / 索引器扫描 / 局部变量扫描）用 `case GtGt: depth -= 2;`
计为两个关闭。

**为什么不在 lexer 拆分**：cursor 是 `readonly struct` + `IReadOnlyList<Token>`
的 immutable 设计（lookahead/backtracking 依赖此不变性）；由 parser 上下文
驱动 lexer mode 切换会污染分层。Roslyn 也用 parser-side split。

---

## 错误处理：Diagnostics vs Exceptions

z42 编译器同时使用**诊断收集**（`DiagnosticBag`）与**异常**（`ParseException` /
`CompilationException`），但二者各司其职 —— 不是冗余设计。新接手者最容易困
惑的就是"什么时候 `_diags.Error(...)` 后继续、什么时候 `throw`"，本节把
规则一次说清。

### 三个核心类型

| 类型 | 定义位置 | 作用 |
|------|---------|------|
| `DiagnosticBag` | `z42.Core/Diagnostics/DiagnosticBag.cs` | 收集多条诊断（错误 + 警告 + 信息），允许"先报告再继续"模式 |
| `ParseException` | `z42.Syntax/Parser/Parser.cs` | Parser 在**无法继续解析当前结构**时抛出（panic-mode）；携带 `Span` 与 `code` |
| `CompilationException` | `z42.Core/Diagnostics/DiagnosticBag.cs` | 由 `DiagnosticBag.ThrowIfErrors()` 抛出；表示"已收集到错误，停止当前 unit" |

### 使用规则

#### ① Parser 用 `ParseException`（强制中断）

**场景**：当 Parser 已经消费了一些 token，但接下来的 token 序列既不满足
任何已知文法规则、也无法智能猜测意图（例如 `int x = ;` 这种）→ 当前声明
/ 表达式必须放弃。

```csharp
// z42.Syntax/Parser/TopLevelParser.Helpers.cs
private static Token ExpectKind(ref TokenCursor cursor, TokenKind kind)
{
    if (cursor.Current.Kind != kind)
        throw new ParseException(
            $"expected `{Combinators.KindDisplay(kind)}`, got `{cursor.Current.Text}`",
            cursor.Current.Span,
            DiagnosticCodes.ExpectedToken);
    ...
}
```

**为什么不 `_diags.Error()` 后继续**：parser 是有状态的（cursor 位置），
错误 token 上"装作没事"会让后续解析被错误位置误导，产生雪崩式假错误。

#### ② Parser 顶层用 `catch (ParseException) when (diags != null)`（panic 恢复）

**只在顶层声明边界 catch**：`ParseCompilationUnit` 的 top-item 循环、
`ParseClassDecl` 的成员循环、`StmtParser` 的语句循环 —— 这些点适合
"放弃当前声明，跳到下一个边界继续 parse"，能在一次 build 里报告多个语法
错误。

```csharp
// z42.Syntax/Parser/TopLevelParser.cs
while (!cursor.IsEnd)
{
    try
    {
        // ... parse one top-item ...
    }
    catch (ParseException ex) when (diags != null)
    {
        diags.Error(ex.Code ?? DiagnosticCodes.UnexpectedToken, ex.Message, ex.Span);
        cursor = SkipToNextDeclaration(cursor);  // panic-mode: 找下一个 `class` / `void` / `using` 等开始 token
    }
}
```

**`when (diags != null)` 的意义**：API 暴露给"只 parse 不报错"的内部用法
（如表达式 fragment 解析）时，diags 为 null → 异常直接传播，调用方决定
是否兜底。这是有意的双模式接口。

#### ③ Semantic / Codegen 用 `_diags.Error()`（继续收集）

**场景**：类型检查、Codegen 阶段每个错误都不影响其他独立子树（例如方法 A
的类型错误不影响方法 B），所以全部走 diag 收集，跑完整个 CU 一次性报告。

```csharp
// z42.Semantics/TypeCheck/TypeChecker.Generics.cs
if (bundle.RequiresEnum && !IsEnumArg(typeArg))
    _diags.Error(DiagnosticCodes.TypeMismatch,
        $"type argument `{typeArg}` for `{typeParams[i]}` does not satisfy constraint `enum` on `{declName}`",
        callSpan);
// 注意：不 throw —— 继续检查下一个 type arg
```

**为什么不 `throw`**：让用户一次拿到所有错误，避免 fix-one-rebuild-find-next 循环。

#### ④ Body 绑定用 `try { ... } catch (CompilationException)`（函数级隔离）

**场景**：当一个函数 body 因深层错误彻底崩溃（不一定是 `CompilationException`，
也可能是任意 `Exception` 即 ICE），需要保护**其他函数继续被检查**。

```csharp
// z42.Semantics/TypeCheck/TypeChecker.cs
private void TryBindClassMethods(ClassDecl cls)
{
    try { BindClassMethods(cls); }
    catch (CompilationException) { /* 诊断已记录，继续下一个类 */ }
    catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
    {
        _diags.Error(DiagnosticCodes.InternalCompilerError,
            $"ICE while checking class `{cls.Name}`: [{ex.GetType().Name}] {ex.Message}", cls.Span);
    }
}
```

**`CompilationException` 不是错误**：它只是一个"我已经把错误塞进 diag 了，
请把我作为信号回到外层"。catch 时**不再追加诊断**（已经在 diag 里了）。

#### ⑤ Pipeline 用 `diags.HasErrors` 决策（不依赖异常控制流）

**场景**：`PipelineCore.Compile` 在调用 TypeChecker 后，先检查 `diags.HasErrors`
决定是否进入 IrGen；CompilationException 只是 IrGen 内部 `ThrowIfErrors()`
的兜底，正常路径都走 `if (diags.HasErrors)`。

```csharp
// z42.Pipeline/PipelineCore.cs
var sem = new TypeChecker(diags, feats, depIndex).Check(cu, imported);
if (diags.HasErrors)
    return new(null, diags, ...);   // 先看 diag，不进 IrGen
try
{
    var ir = new IrGen(...).Generate(cu);
    ...
}
catch (CompilationException)
{
    // 仅当 IrGen 内部 ThrowIfErrors 时触发，diag 已收集
    return new(null, diags, ...);
}
// 其他异常 = ICE，直接传播让 stack trace 暴露
```

### 决策表（速查）

| 场景 | 用什么 | 例子 |
|------|--------|------|
| Parser 期望 token X 但拿到 Y | `throw new ParseException` | `ExpectKind(LParen)` 失败 |
| Parser 表达式 / 语句中递归失败 | `throw new ParseException` | `Combinators.Unwrap(ref cursor)` |
| 顶层声明边界 / 成员循环 | `catch (ParseException) when (diags != null)` + 跳过 | `ParseCompilationUnit` top loop |
| TypeChecker 检测到类型不匹配 | `_diags.Error(...)` 继续 | `RequireAssignable` 失败 |
| TypeChecker 检测到未定义符号 | `_diags.Error(...)` 继续 | `BindIdent` 找不到 |
| Codegen 发现 unreachable 状态 | `_diags.Error(...)` 继续（让其他函数继续生成） | `IrGen` 内部检查 |
| Body 绑定包裹（保护其他函数） | `try / catch (CompilationException) / catch (Exception → ICE)` | `TryBindClassMethods` |
| Pipeline 阶段切换 | `if (diags.HasErrors) return` | `Compile` TypeChecker → IrGen 间 |
| 真正的内部 bug（NullRef 等） | **不 catch**，让它传播 | 任何未预期 `Exception` |

### 反模式（违规即修）

- ❌ Parser 内部 `_diags.Error()` 后继续 parsing —— 错误 cursor 位置会污染后续
- ❌ TypeChecker `throw new SomeException` 中断检查 —— 用户拿不到完整错误列表
- ❌ Pipeline 用 `try { ... } catch (Exception)` 吞掉 ICE —— 失去 stack trace 调试线索
- ❌ catch `CompilationException` 后再 `_diags.Error("...")` 加错误 —— 重复报告
- ❌ `_diags.Error(..., span)` 用了 `Span.Empty` / `(0,0)` —— 用户定位不到位置（`EmitImportDiagnostics` 之类的 cross-file 诊断除外，但应注释说明）

---

## Class field initializer pipeline（2026-05-02 fix-class-field-default-init）

实例字段 `int n = 5;` / 静态字段 `static int s = 1;` 的 init 表达式
统一在 TypeChecker 阶段绑定，按字段种类分流到 SemanticModel 的两本字典：

| 字段种类 | SemanticModel 字段 | 消费者（IrGen / FunctionEmitter） |
|---------|------------------|---------------------------------|
| `static` field with init | `BoundStaticInits` | `EmitStaticInit` 在 `__static_init__` 函数体中按拓扑序发射 `StaticSet` |
| 实例 field with init | `BoundInstanceInits` | 每个 ctor 入口（base ctor call 之后、用户 body 之前）按字段声明顺序注入 `FieldSet` |

**关键决策**：

- `TypeChecker.BindFieldInits` 同时绑定两类初始化器，类型检查时把字段类型作为 expected
  type（驱动数值字面量 / lambda / 重载选择），并在不可赋值时报错。
- `FunctionEmitter.EmitMethod` 接受 `IReadOnlyList<FieldDecl>? instanceFieldInits` 参数 —
  IrGen 在调用时仅对 ctor（`!isStatic && method.Name == className`）传入非空列表。
- 类没有显式 ctor 但本类或任一**本地祖先**有字段 init → IrGen 调
  `EmitImplicitCtor`，构造一个空 body 的 synth `FunctionDecl(name=cls.Name)`，
  通过 `CollectChainFieldInits` 沿祖先链 top-down 拼出整条链的 FieldDecl 列表，
  作为 `instanceFieldInits` 传给 `FunctionEmitter.EmitMethod`。
- 合成 ctor 不调用 base ctor —— 与 z42 现有"只有 `: base(...)` 才生成 base call"
  行为一致。跨 zpkg 祖先字段 init 不在合成范围内（FieldDecl AST 不可见），
  目前是已知限制。
- 未带显式初始化器的字段由 VM 端 `metadata::default_value_for(type_tag)` 在 `ObjNew`
  分配时填默认值，详见 `docs/design/vm-architecture.md` §ObjNew dispatch。

## 关键设计权衡

### 为什么 AST 节点是 `sealed record`

- **不可变 + 值相等**：便于并行分析、避免副作用
- **sealed**：匹配时 switch exhaustive，不漏 case
- **record**：自动合成 ctor / ToString / Equals，减少样板代码

### 为什么 TypeChecker 不直接写 IR

分离原因：
- TypeCheck 是**纯前端**（只看 AST + imports，产出 SemanticModel）
- Codegen 是**后端映射**（SemanticModel → IR）
- 两者通过 SemanticModel 接口解耦，便于将来加 LSP / incremental compilation

### 为什么不用 incremental parsing

z42 当前每次全量 parse。L3 后计划引入 LSP 时会考虑 incremental，但
bootstrap 阶段不做 —— 复杂度远超收益。

---

## 延伸阅读

- `docs/design/compilation.md` — 构建流程（manifest → zpkg 的用户视角）
- `docs/design/zbc.md` — `.zbc` 二进制格式
- `docs/design/namespace-using.md` — namespace / using 的语言规则
- `.claude/rules/compiler-csharp.md` — C# 编译器开发规范（代码风格 + AST / Parser / Lexer 约定）
