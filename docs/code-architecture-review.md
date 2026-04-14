# z42 代码框架结构设计深度分析报告

> **分析日期**：2026-04-14  
> **分析层次**：架构设计 / 职责边界 / 数据流 / 抽象层次 / 扩展性  
> **分析范围**：编译器前端（C#）+ 运行时（Rust）全链路

---

## 一、当前架构总体评价

z42 工具链整体分层清晰，编译器 → IR → 运行时的大方向是正确的。  
但在深入阅读代码后，发现若干**结构性设计问题**，它们不是局部 Bug，而是影响整个系统可演化性、正确性边界和模块独立性的架构决策问题。

---

## 二、核心架构问题

---

### 【A1】TypeChecker 与 IrGen 存在隐式的知识重复——"语义层分裂"

**问题等级**：⚠️ 严重

#### 现象

`TypeChecker`（语义分析）和 `IrGen`（代码生成）是两个独立的 `partial class`，但它们都在**各自内部重新构建了一套相同的符号表**：

| 信息 | TypeChecker 中的结构 | IrGen 中的结构 |
|------|------|------|
| 类的方法集合 | `_classes[name].Methods` | `_classMethods[qualName]` |
| 静态方法集合 | `_classes[name].StaticMethods` | `_classStaticMethods[qualName]` |
| 实例字段集合 | `_classes[name].Fields` | `_classInstanceFields[qualName]` |
| 静态字段初始化 | 在 TypeChecker 中验证 | `_classStaticFieldInits[qualName]` 在 IrGen 中重建 |
| 重载方法判断 | `methodNameCount[(name, isStatic)] > 1` | `_overloadedInstanceMethods`, `_overloadedStaticMethods` |
| 基类名称 | `Z42ClassType.BaseClassName` | `_classBaseNames[qualName]` |
| 枚举常量 | `_globalEnumConstants` | `_enumConstants` |

**两个阶段各自从 `CompilationUnit`（AST）重新遍历并建立符号表，完全没有传递语义分析的结果给代码生成器。**

#### 根本原因

`TypeChecker.Check(CompilationUnit cu)` 返回 `void`，它的所有分析结果（`_classes`, `_funcs`, `_interfaces` 等）在方法返回后全部丢弃。`IrGen` 随后从零重新解析同一个 AST。

```csharp
// 当前流程
var diags = new DiagnosticBag();
new TypeChecker(diags).Check(cu);        // 分析结果丢弃
if (diags.HasErrors) return 1;

var irModule = new IrGen(stdlibIndex).Generate(cu); // 重新分析 AST
```

#### 架构影响

1. **AST 被遍历两次**，每次都做大量字典构建。
2. **重载解析逻辑不一致**：TypeChecker 用 `$N` 后缀，IrGen 也用 `$N` 后缀，但逻辑分散，任意一处改动可能导致两者不同步。
3. **IrGen 无法利用 TypeChecker 的类型推断结果**，例如 `EmitExpr` 无法直接知道一个表达式的类型，只能靠命名启发式（`_classInstanceVars`、`_mutableVars`）来推断。

#### 建议方案

引入 `SemanticModel`（或 `BoundTree`）作为 TypeChecker 的输出，让 IrGen 消费它而非重新解析 AST：

```
AST (CompilationUnit)
  └─► TypeChecker  ──produces──► SemanticModel
                                    ├── ClassTable: name → ClassSymbol
                                    ├── FuncTable:  name → FuncSymbol  
                                    ├── EnumTable:  name → EnumSymbol
                                    └── ExprTypes:  Expr → Z42Type  (可选)
                                  └─► IrGen(SemanticModel).Generate()
```

这是 Roslyn / GCC / LLVM 的标准做法。`SemanticModel` 是两阶段之间的合约，双方都不再直接依赖原始 AST 的重新遍历。

---

### 【A2】IR 指令集与类型系统之间存在"阻抗失配"——IR 无类型

**问题等级**：⚠️ 严重

#### 现象

`IrInstr` 的所有指令都是**无类型**的，寄存器只有编号（`int Dst`），没有类型信息：

```csharp
public sealed record AddInstr(int Dst, int A, int B) : IrInstr;
public sealed record ConstI32Instr(int Dst, int Val)  : IrInstr;
public sealed record ConstI64Instr(int Dst, long Val) : IrInstr;
```

同时，`IrGen` 中对字面量的处理是：

```csharp
case LitIntExpr n:
    Emit(new ConstI64Instr(dst, n.Value));  // 所有整型字面量统一 emit 为 i64
    return dst;
```

但 `Value` 枚举在运行时有 `I32`/`I64`/`F32`/`F64` 等多种变体，运算时必须匹配类型。

#### 架构影响

1. **运行时 `int_binop` 需要在每次运算时检查两个操作数的实际 Value 变体**，无法提前优化。
2. **IrGen 产生的 IR 丢失了 TypeChecker 费力推导的类型信息**，运行时只能从值本身反推类型，等于退化到动态类型语言的执行模式。
3. **后续加 JIT/AOT 时无法利用静态类型**，必须插入大量运行时类型检查守卫。
4. `ConstI64Instr` 用于所有整型字面量意味着即使是 `int x = 5;` 也会产生 i64 常量，与 `i32` 算术混用时运行时需要隐式转换。

#### 建议方案

在 IR 寄存器或指令级别引入类型标注：

```csharp
// 方案一：typed register（推荐，类似 LLVM IR）
public sealed record AddInstr(TypedReg Dst, TypedReg A, TypedReg B) : IrInstr;
public record TypedReg(int Id, IrType Type);

// 方案二：指令携带操作类型（类似 JVM bytecode）
public sealed record AddInstr(int Dst, int A, int B, IrNumericKind Kind) : IrInstr;
// Kind = I32 | I64 | F32 | F64
```

IR 带类型后，运行时不再需要 `int_binop` 这种"先检查再分派"的运行时 dispatch，可直接执行对应类型的操作。

---

### 【A3】`IrGen` 是一个有大量可变状态的单例——难以并行、难以复用

**问题等级**：⚠️ 严重

#### 现象

`IrGen` 累积了大量函数级别的可变字段：

```csharp
private int _nextReg;
private int _nextLabelId;
private Dictionary<string, int>    _locals;
private HashSet<string>            _mutableVars;
private HashSet<string>            _classInstanceVars;
private HashSet<string>            _instanceFields;
private List<IrBlock>              _blocks;
private List<IrExceptionEntry>     _exceptionTable;
private Stack<(string, string)>    _loopStack;
private string                     _curLabel;
private List<IrInstr>              _curInstrs;
private bool                       _blockEnded;
private string?                    _currentClassName;
```

这 13 个字段在 `EmitMethod` / `EmitFunction` 开始时被手动重置：

```csharp
_nextReg        = method.Params.Count + paramOffset;
_nextLabelId    = 0;
_locals         = isStatic ? new() : new() { ["this"] = 0 };
_mutableVars    = new HashSet<string>();
// ... 等等，每次都重置
```

#### 架构影响

1. **忘记重置任何一个字段会导致跨函数的状态污染**，而编译器不会报错，只会产生错误的 IR。
2. **无法对多个函数并行代码生成**，因为所有状态都在 `IrGen` 实例上。
3. **`IrGen` 既保存模块级状态（`_classMethods`、`_strings`）又保存函数级状态**，两类状态的生命周期完全不同，混在一起违反单一职责。

#### 建议方案

将函数级状态提取为独立的 `FunctionEmitter`（或 `FunctionBuilder`），每次生成一个函数时创建新实例：

```csharp
// IrGen 只持有模块级状态
public sealed class IrGen
{
    private readonly ModuleSymbolTable _symbols;   // 模块级，只读
    private readonly List<string>      _strings;   // 模块级，可追加

    public IrFunction EmitFunction(FunctionDecl fn)
    {
        // 每次创建新的 emitter，天然隔离
        var emitter = new FunctionEmitter(_symbols, _strings, fn);
        return emitter.Emit();
    }
}

// FunctionEmitter 持有函数级状态，生命周期与一次 Emit() 相同
internal sealed class FunctionEmitter
{
    private int                     _nextReg;
    private Dictionary<string, int> _locals;
    // ...
}
```

---

### 【A4】`Z42Type` 层次结构与 AST `TypeExpr` 层次结构并行存在——类型解析是单点瓶颈

**问题等级**：⚠️ 中等

#### 现象

AST 中的类型表示（`TypeExpr`）：
```csharp
public abstract record TypeExpr(Span Span);
public sealed record NamedType(string Name, Span Span)       : TypeExpr(Span);
public sealed record OptionType(TypeExpr Inner, Span Span)   : TypeExpr(Span);
public sealed record ArrayType(TypeExpr Element, Span Span)  : TypeExpr(Span);
public sealed record VoidType(Span Span)                     : TypeExpr(Span);
```

语义类型（`Z42Type`）：
```csharp
public abstract record Z42Type;
public sealed record Z42PrimType(string Name)   : Z42Type;
public sealed record Z42ClassType(...)          : Z42Type;
public sealed record Z42ArrayType(Z42Type Elem) : Z42Type;
public sealed record Z42OptionType(Z42Type Inr) : Z42Type;
public sealed record Z42VoidType                : Z42Type;
```

两套层次结构**几乎镜像**，通过 `TypeChecker.ResolveType(TypeExpr)` 在运行时转换。这个转换函数在 TypeChecker 内部被调用，**IrGen 中有一个完全相同功能的私有方法 `TypeName(TypeExpr)` 做类似的工作**（虽然返回字符串而非类型对象）。

#### 建议方案

长期来看，应考虑在 Parse 阶段就产出带语义的节点（Bound AST），或让 `TypeExpr` 携带一个懒解析的 `Z42Type?` 字段，而不是在每个阶段都重新遍历转换。

---

### 【A5】Parser 设计与错误恢复机制的结构性缺陷

**问题等级**：⚠️ 中等

#### 现象

`Parser` 是一个纯函数式 combinator 风格（`TokenCursor` + `ParseResult<T>`），遇到不可恢复的错误时直接抛出 `ParseException`：

```csharp
public sealed class ParseException(string message, Span span) : Exception(message)
{
    public Span Span { get; } = span;
}
```

这意味着**整个编译单元只能报告第一个语法错误就停止**。

对比：`TypeChecker` 使用 `Z42Type.Error` 作为错误哨兵，在遇到错误后继续分析，可以一次报告多个语义错误。

#### 架构影响

1. 用户编辑代码时，修复一个语法错误后才能发现下一个，IDE 体验差。
2. Parser 与 TypeChecker 的**错误处理哲学不一致**，给上层 Pipeline 的统一处理带来困难（见上文 H2 问题）。

#### 建议方案

引入**错误恢复解析**：在遇到意外 token 时，插入一个 `ErrorExpr` / `ErrorStmt` 节点继续解析，而不是直接 throw。这是 Roslyn、rust-analyzer 等现代编译器的标准做法。

```csharp
// 错误恢复节点
public sealed record ErrorExpr(Span Span) : Expr(Span);
public sealed record ErrorStmt(Span Span) : Stmt(Span);

// Parser 遇到错误时
private Expr ParseExprOrError()
{
    try { return ParseExpr(); }
    catch (ParseException ex)
    {
        _diags.Add(Diagnostic.Error(..., ex.Span));
        SkipUntilRecoveryPoint();   // 跳到下一个语句分隔符
        return new ErrorExpr(ex.Span);
    }
}
```

---

### 【A6】运行时 `Value` 枚举的 `Rc<RefCell<...>>` 结构导致克隆开销与并发限制

**问题等级**：⚠️ 中等

#### 现象

```rust
pub enum Value {
    // ...
    Array(Rc<RefCell<Vec<Value>>>),
    Object(Rc<RefCell<ScriptObject>>),
}
```

`Value` 的 `Clone` 实现需要克隆 `Rc`（引用计数 +1）。在解释器热路径中，几乎每次寄存器读取都伴随 `frame.get(reg)?.clone()`：

```rust
Instruction::Copy { dst, src } => frame.set(*dst, frame.get(*src)?.clone()),
```

#### 架构影响

1. **`Rc` 是非 Send 的**，意味着 `Value` 不能跨线程传递，与语言层面的 `threading` 特性目标冲突。
2. **引用计数的 +1/-1 在紧密循环中是显著开销**，尤其是深层嵌套对象访问。
3. 现有的异常机制使用 `thread_local!` 存储飞行中的异常值，也是这个限制的副产品。

#### 建议方案

区分两种引用场景：
- **解释器内部**：保持 `Rc<RefCell<>>` 用于单线程快速路径（当前可接受）。
- **跨线程传值**：引入 `Arc<Mutex<>>` 的 `SharedValue` 包装，或在语言层面通过类型系统区分 `Send`/非`Send` 对象。

长期来看，可以考虑引入对象池（arena allocator），完全替换 `Rc` 的堆分配模式。

---

### 【A7】`StdlibCallIndex` 的模糊消解策略是一个隐藏的语义 Bug 源

**问题等级**：⚠️ 中等

#### 现象

`StdlibCallIndex.Build()` 在构建实例方法索引时，遇到多个标准库类中同名方法（如 `Contains` 同时存在于 `String` 和 `List`）时，选择**静默删除**该 key：

```csharp
// Remove all ambiguous keys.
foreach (var key in ambiguous)
    instanceBuf.Remove(key);
```

这意味着当用户写 `str.Contains("x")` 时，如果 `Contains` 被标记为 ambiguous，则 IrGen 会 fall-through 到 `VCallInstr` 运行时虚分派，而不是直接的静态 `CallInstr`。

#### 架构影响

1. **行为依赖标准库的加载顺序**：如果只加载了 `Std.Text`（包含 String），`Contains` 不会歧义，走静态调用；如果同时加载了 `Std.Collections`（包含 List.Contains），则变成虚分派。相同的代码，不同的库集合，**产生不同的 IR**。
2. **用户感知不到这个降级**：没有任何警告，代码正确执行但走了不同的调用路径。
3. 这实际上是**把方法解析的一部分责任推迟到了运行时**，违背了静态编译的设计目标。

#### 建议方案

在 IrGen 做调用解析时，应结合接收者的**静态类型**（来自 TypeChecker 的类型推断）来精确选择 `CallInstr` 还是 `VCallInstr`，而不是依赖 `StdlibCallIndex` 的简单字符串匹配。这又回归到 A1 问题：TypeChecker 的类型结果必须传递给 IrGen。

---

### 【A8】模块合并（`merge_modules`）仅重映射字符串池，忽略了类描述符冲突

**问题等级**：⚠️ 中等

#### 现象

```rust
pub fn merge_modules(modules: Vec<Module>) -> Result<Module> {
    // ...
    for mut module in modules {
        let offset = string_pool.len() as u32;
        string_pool.extend(module.string_pool);
        classes.extend(module.classes);       // 直接拼接，无去重
        remap_functions(&mut module.functions, offset);
        functions.extend(module.functions);   // 直接拼接，无去重
    }
}
```

注释中也承认：`classes: concatenated (Phase 1 trusts the compiler for no duplicates)`。

#### 架构影响

1. 若两个 `.zpkg` 都包含 `Std.Object` 的 `ClassDesc`（因为它们都链接了 `z42.core`），合并后会有重复的类描述符，运行时的 `type_registry` 构建会注册哪个是不确定的。
2. **没有 deduplication 的函数合并**同样会导致同名函数重复，运行时查找函数时取哪一个？（`iter().find()` 取第一个，但顺序由合并顺序决定）。

#### 建议方案

合并时进行**幂等合并**（idempotent merge）：以 `name` 为 key，相同名字的 `ClassDesc` / `Function` 以最后一个（或第一个）为准，而不是两者都保留。

---

### 【A9】`LanguageFeatures` 的特性门控只在 Parser 层生效，TypeChecker 和 IrGen 不感知

**问题等级**：⚠️ 中等

#### 现象

`LanguageFeatures` 通过 `Parser` 的构造函数注入，控制 AST 层面哪些语法被解析。但 `TypeChecker` 和 `IrGen` 的构造函数都不接收 `LanguageFeatures`：

```csharp
public TypeChecker(DiagnosticBag diags) => _diags = diags;
public IrGen(StdlibCallIndex? stdlibIndex = null) { ... }
```

#### 架构影响

1. 特性门控只是**语法层面的过滤**，不是完整的语义层面特性控制。
2. 如果将来某个特性需要在类型检查阶段启用额外规则（如 `generics` 特性启用泛型约束检查），当前架构无法支持。
3. `PackageCompiler` 在调用 `TypeChecker` 和 `IrGen` 时，无法将 features 传入，导致所有包编译都使用默认特性集。

#### 建议方案

`TypeChecker` 和 `IrGen` 都应接收 `LanguageFeatures` 参数，形成统一的特性门控链：

```
Parser(tokens, features)
  → TypeChecker(diags, features)
    → IrGen(stdlibIndex, features)
```

---

### 【A10】`PackageCompiler` 直接承担了文件系统 I/O、编译逻辑和打包逻辑——职责过重

**问题等级**：ℹ️ 低

#### 现象

`PackageCompiler` 是一个 `static class`，`BuildTarget` 方法约 140 行，内部混合了：
- 文件系统扫描（`Directory.GetFiles`）
- 依赖解析（`nsMap` 构建）
- 每个源文件的完整编译流程
- 输出打包（`ZpkgWriter.Write`）
- 文件写入（`File.WriteAllBytes`）

`static class` 的设计使得整个编译流程**完全不可测试**（无法 mock 文件系统、无法注入依赖）。

#### 建议方案

将 `PackageCompiler` 重构为可实例化的 `BuildPipeline`，接受抽象的文件系统接口（`IFileSystem`）和输出接口（`IOutputSink`），使其可单元测试：

```csharp
public sealed class BuildPipeline(IFileSystem fs, IOutputSink sink)
{
    public BuildResult Build(ProjectManifest manifest) { ... }
}
```

---

## 三、架构改进路线图

```
┌─────────────────────────────────────────────────────────────────┐
│ 近期（影响日常开发正确性）                                        │
│                                                                  │
│  A3 ── IrGen FunctionEmitter 拆分（防止状态污染）                 │
│  A9 ── LanguageFeatures 传入 TypeChecker + IrGen                 │
│  A8 ── merge_modules 增加幂等合并                                 │
└────────────────────────────────┬────────────────────────────────┘
                                 │
┌────────────────────────────────▼────────────────────────────────┐
│ 中期（影响 IDE / 增量编译 / JIT 支持）                            │
│                                                                  │
│  A1 ── 引入 SemanticModel，消除 TypeChecker/IrGen 知识重复        │
│  A5 ── Parser 错误恢复，支持多错误报告                            │
│  A7 ── IrGen 调用解析利用静态类型，替换 StdlibCallIndex 模糊匹配  │
└────────────────────────────────┬────────────────────────────────┘
                                 │
┌────────────────────────────────▼────────────────────────────────┐
│ 远期（影响性能 / 并发 / AOT 编译）                                │
│                                                                  │
│  A2 ── IR 引入类型标注（Typed IR）                               │
│  A4 ── Bound AST / 类型解析懒化，消除两套类型层次                 │
│  A6 ── Value 引用模型重设计，解除 Rc 的 !Send 限制               │
│  A10 ─ BuildPipeline 可注入化，支持单元测试                       │
└─────────────────────────────────────────────────────────────────┘
```

---

## 四、各问题相互关系图

```
A1（语义层分裂）
  ├── 导致 ──► A7（StdlibCallIndex 模糊匹配，因为 IrGen 不知道接收者类型）
  └── 导致 ──► A2（IR 无类型，因为类型信息没有从 TypeChecker 流向 IrGen）

A2（IR 无类型）
  └── 导致 ──► 运行时 int_binop 开销（每次运算都要 match Value 变体）

A3（IrGen 状态混杂）
  └── 掩盖 ──► A1（如果有 FunctionEmitter，模块级/函数级状态自然分离）

A5（Parser 单错误停止）
  └── 关联 ──► A1（如果 Parser 也产出 ErrorNode，TypeChecker 可以跳过错误节点继续）

A9（LanguageFeatures 不传递）
  └── 限制 ──► 未来特性扩展的粒度控制能力
```

---

*本报告聚焦于架构设计层面的结构性问题，建议在功能稳定后逐步重构，优先从近期改动开始。*
