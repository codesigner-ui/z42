# z42 编译器/运行时代码架构分析报告

> 初始分析：2026-04-18  
> 最后更新：2026-04-19（已完成改进：17 项，进行中：0 项）  
> 分析范围：`src/compiler`（C# 编译器前端）+ `src/runtime`（Rust 运行时）  
> 参考对象：Roslyn、rustc、LLVM、JVM（HotSpot）、LuaJIT、V8

**最近完成的改进（2026-04-18）：**
- ✅ **FunctionEmitterCalls Instance 方法分派** — stdlib 方法优先查询 DepIndex，修复集合类型虚方法误识别
- ✅ **内置类型静态方法映射** — `string.IsNullOrEmpty()` 正确映射到 `Std.String.IsNullOrEmpty()`
- ✅ **后缀操作符返回值修复** — 确保 `i++` 返回旧值，不被后续 copy 指令覆盖
- ✅ **完全消除 BoundCallKind.Unresolved** — 所有 stdlib 调用在编译时解析为 Static/Instance/Virtual/Free
- ✅ **区分 ICE 与用户错误（4.1）** — TryBind* 方法区分 CompilationException 与编译器内部错误，新增 E0900 ICE 诊断码
- ✅ **统一类型名映射 TypeRegistry（4.3）** — 已有 TypeRegistry.cs 作为唯一数据源
- ✅ **IsSubclassOf 预计算祖先集合（5.2）** — SymbolTable 构造时预计算所有类的祖先集合，O(1) 查找
- ✅ **exec_function 预计算 block_index（3.3）** — loader.rs 在模块加载时预计算 block_index

**新完成的改进（2026-04-19）：**
- ✅ **BoundError → Codegen 防护（5.1）** — BoundError 到达 Codegen 时抛出 ICE 而非静默生成 null
- ✅ **删除未使用的 Visitor 接口（1.5）** — 移除 IBoundExprVisitor/IBoundStmtVisitor 和所有 Accept 方法，统一用 switch 模式匹配
- ✅ **FunctionDecl FunctionModifiers flags（4.2）** — 5 个 bool 替换为 `[Flags] enum FunctionModifiers`，便捷属性保持 API 兼容
- ✅ **LookupVar 语义清理（1.3）** — LookupVar 不再为类名返回 Unknown，新增 IsClassName 方法区分变量/类型
- ✅ **_currentClass → TypeEnv.CurrentClass（1.4）** — 消除共享可变状态，改为不可变 env 属性，通过 WithClass() 传递
- ✅ **ExecSignal 替代 anyhow 传播（3.2）** — 用户异常走 ExecOutcome::Thrown 值传递，不再经 thread_local + anyhow 堆分配
- ✅ **消除命名变量槽残留代码（2.2）** — 删除 JitFrame.vars HashMap + jit_store/jit_load 死代码，确认 IR 已是纯寄存器机
- ✅ **Value 枚举合并整数类型（3.1）** — 删除 I8/I16/I32/U8/U16/U32/U64/F32 共 8 个变体，统一为 I64+F64
- ✅ **IR 序列化与内存模型解耦（2.1）** — 删除 54 个 JsonDerivedType 注解；`--emit ir` 改为输出 ZASM 文本；删除 `--emit zasm` 重复命令

---

## 概述

z42 是一门自制编程语言，由 C# 编写的编译器前端和 Rust 编写的运行时虚拟机组成。整体架构分层清晰：

```
源代码
  │
  ▼
Lexer (z42.Syntax/Lexer)
  │  Token 流
  ▼
Parser (z42.Syntax/Parser)
  │  AST (CompilationUnit / Expr / Stmt)
  ▼
SymbolCollector (z42.Semantics/TypeCheck)
  │  SymbolTable（符号快照）
  ▼
TypeChecker (z42.Semantics/TypeCheck)
  │  SemanticModel + BoundExpr/BoundStmt
  ▼
IrGen / FunctionEmitter (z42.Semantics/Codegen)
  │  IrModule（IR 字节码）
  ▼
IrPassManager (z42.IR)
  │  优化后 IrModule
  ▼
ZbcWriter (z42.IR/BinaryFormat)
  │  .zbc 二进制包
  ▼
Runtime (Rust: interp / jit)
  │  解释执行 / JIT 编译执行
  ▼
程序输出
```

总体评价：**架构分层清晰、模块边界明确、代码风格统一**，但在运行时性能细节、编译器工程健壮性等方面对照成熟实现仍有若干可改进之处。

---

## 快速导航

- [编译器架构层面](#一编译器架构层面) — 5 项改进，重点：类型转换规则整合、状态管理改进
- [IR 设计层面](#二ir-设计层面) — 3 项改进，重点：寄存器机模型、序列化解耦
- [运行时层面](#三运行时层面) — 4 项改进，重点：Value 内存布局、异常处理机制
- [编译管线流程层面](#四编译管线流程层面) — 3 项改进，重点：错误区分、参数聚合
- [代码质量与工程层面](#五代码质量与工程层面) — 2 项改进，重点：性能预计算
- [优先级路线图](#六改进优先级路线图) — P0–P3 清单与工作量评估
- [快速开始](#快速开始指南) — 最应该立即推进的 3 项

---

## 一、编译器架构层面

### 1.1 已有的良好设计

| 方面 | 实现 |
|------|------|
| 管线分层 | `Lex → Parse → SymbolCollect → TypeCheck → IrGen → IrPass` 流程清晰 |
| Bound AST | `BoundExpr` 携带类型信息，Visitor 模式接口设计良好 |
| IR 验证 | `IrVerifier` 在 Debug 构建中检验 def-use / 分支目标正确性 |
| 错误隔离 | `TryBindClassMethods` 对每函数做异常隔离，单个错误不会崩溃整个编译 |
| SymbolTable 边界 | Pass 0 输出的 `SymbolTable` 作为只读快照传给 Pass 1，数据边界明确 |

---

### 1.2 改进点：类型赋值兼容性规则分散 🛡️ 正确性

**问题描述**

`Z42Type.IsAssignableTo` 中的数值扩展规则以多个 `if` 链硬编码，且**不完整**——无符号整数宽化链（`u8 → u16 → u32 → u64`）、有符号小整数（`i8 → i16 → i32`）均未覆盖。相关兼容性判断还散布在多个文件中。

**现状：** `int → long`、`int → float` 有支持，但小整数和无符号整数的宽化链路缺失；规则分散导致新增类型时容易遗漏

**进展（2026-04-18）：** 当前的后缀操作符修复中，整数常数统一为 `ConstI64Instr`（VM 统一所有整数为 I64），这部分缓解了小整数类型的问题。但完整的类型兼容性规则整合仍需进行。

**参考：** Roslyn 的 `Conversions` 类用统一的 `ConversionKind` 枚举（Identity、NumericWidening、NullToReference 等）覆盖所有转换，调用方仅检查 `ConversionKind`，无需重复枚举类型对。

**建议改进**

新建 `TypeConversion.cs`，定义 `ConversionKind` 枚举和 `Classify(from, to)` 方法，集中覆盖所有隐式转换规则（包括现有的和缺失的无符号/小整数链路）。`IsAssignableTo` 改为调用 `Classify` 判断非 None 即可。

---

### 1.3 改进点：`TypeEnv.LookupVar` 的 `Unknown` 返回值语义不清 🛡️ 正确性

**问题描述**

`TypeEnv.LookupVar` 当遇到类名时返回 `Z42Type.Unknown`（注释坦承是"backward compat"），而不是明确标识"这是一个类型引用而非变量"。导致 `Unknown` 既充当"错误哨兵"又充当"类名占位符"，调用方无法区分。

**参考：** rustc 的 `Res` 枚举：`Def(DefKind, DefId)`（定义）、`Local(HirId)`（变量）、`Err`（失败），绝不复用 sentinel 表达不同含义。

**建议改进**

引入 `LookupKind` 枚举（NotFound、LocalVar、ClassRef、ImportedClass），返回 `LookupResult` 值对象，明确区分变量 vs 类名 vs 错误。

---

### 1.4 改进点：`TypeChecker._currentClass` 是共享可变状态 🛡️ 正确性 / 🔧 可维护性

**问题描述**

`TypeChecker` 通过 `IDisposable` RAII 管理 `_currentClass` 可变字段。单线程下可工作，但：
- 多线程并行编译不安全
- 嵌套进入时状态会被覆盖
- 单元测试复用实例时存在状态残留

**参考：** Roslyn 的 `Binder` 将绑定上下文建模为不可变对象，通过参数传递，绑定是纯函数式的。

**建议改进**

将 `_currentClass` 改为参数，定义 `BindContext` 值对象（包含 CurrentClass、SymbolTable、TypeEnv），显式作为参数传递给 `BindClassMethods`、`BindExpr` 等方法，消除共享可变状态。

---

### 1.5 改进点：BoundExpr 的 Visitor 与 `switch` 模式匹配并存，形成双重路径 🔧 可维护性

**问题描述**

`BoundExpr.cs` 定义了完整的 `IBoundExprVisitor<TResult>` Visitor 接口，每个 record 都实现了 `Accept`。但 `FunctionEmitter` 实际上**完全不调用** `Accept`，而是用 `switch` 模式匹配：

```csharp
// BoundExpr.cs — Visitor 接口存在
public override TResult Accept<TResult>(IBoundExprVisitor<TResult> v)
    => v.VisitLitInt(this);

// FunctionEmitterExprs.cs — 实际使用 switch，Visitor 形同虚设
private TypedReg EmitExpr(BoundExpr expr) => expr switch
{
    BoundLitInt    lit  => ...,
    BoundLitStr    lit  => ...,
    BoundBinary    bin  => ...,
    ...
};
```

这导致：
1. 维护两套枚举路径（Visitor 接口 + switch arm）
2. 新增 BoundExpr 节点类型时必须同时修改两处，极易遗漏
3. `IBoundVisitor` 只在 `FlowAnalyzer` 中实际使用，其余地方均走 switch

**建议改进**

二选一，统一遍历方式：

- **方案 A（推荐）**：保留 `switch` 模式匹配，删除 `Accept/Visitor` 接口（C# record 的穷尽模式匹配已足够优雅）
- **方案 B**：所有遍历均通过 `Accept/Visitor`，`FunctionEmitter` 实现 `IBoundExprVisitor<TypedReg>`

---
## 二、IR 设计层面

### 2.1 改进点：IR 内存模型与 JSON 序列化耦合 🔧 可维护性

**问题描述**

`IrModule.cs` 中，IR 指令的内存表示与 JSON 持久化格式完全耦合——40 余个 `[JsonDerivedType]` 特性直接标注在核心数据结构上。改变持久化格式就必须修改内存模型，违反了"关注点分离"原则。

**参考：** LLVM 将内存 IR 和序列化格式（bitcode / text IR）完全分离，序列化逻辑在独立的 `BitcodeWriter` / `AssemblyWriter` 中实现。

**建议改进**

将序列化特性从 `IrInstr` 等记录中移除，新建 `IrJsonSerializer.cs` 独立处理序列化逻辑（type discriminator、field mapping 等），内存模型与持久化格式解耦。

---

### 2.2 改进点：`StoreInstr`/`LoadInstr` 引入"命名变量槽"，形成混合寄存器/名称机 ⚡ 性能

**问题描述**

IR 层同时存在寄存器机（`AddInstr` 用 TypedReg ID）和命名槽机（`StoreInstr`/`LoadInstr` 用字符串 key）。运行时 `Frame` 维护 `HashMap<String, Value>` 来支持命名槽。

**缺陷：**
1. 字符串 Hash 比整数索引慢一个数量级
2. JIT 需额外处理字符串参数，生成代码更复杂
3. 破坏"变量在编译期完全解析为寄存器 ID"的纯寄存器机不变式

**参考：** JVM / WASM 全部用整数索引（`iload_0`、`local.get 0`），没有字符串 key。

**建议改进**

IR 生成阶段为每个可变变量分配寄存器 ID，消除 `StoreInstr`/`LoadInstr`，让 IR 成为真正的纯寄存器机；`Frame.regs` 统一使用整数索引。

---

### 2.3 改进点：`IrVerifier` 的 def-use 检查按列表顺序而非控制流顺序扫描 🛡️ 正确性

**问题描述**

`IrVerifier.VerifyDefUse` 按 Block List 顺序扫描，不等于按控制流支配关系扫描。若某寄存器仅在一条分支路径上定义，另一条路径上被使用，当前检查无法发现。

**参考：** LLVM 在 SSA 形式下对每个 use 验证其 def 是否支配（dominate）当前位置，使用支配树分析。

**建议改进**

至少构建基本块的 CFG（predecessor/successor 关系），用 BFS/DFS 按可达顺序遍历，而非 List 顺序；理想情况下计算支配树进行严格的 def-use 验证。

---

## 三、运行时层面

### 3.1 改进点：`Value` 枚举变体过多，导致所有值均占用最大变体的内存 ⚡ 性能

**问题描述**

Rust enum 大小 = 最大变体 + discriminant。当前 `Value` 有 13 个变体，`Str(String)` 占 24 字节，因此即使存储 `I32(42)` 也需要 ≥24+1 字节，浪费 ~20 字节，严重影响缓存命中率。

**参考：** 
- LuaJIT：NaN Boxing，所有值压入 64-bit payload，整数/浮点/指针共 8 字节
- V8：Smi（small integer），小整数用 tagged pointer，无堆分配
- JVM：原始类型不装箱，仅泛型边界处 box

**建议改进（短期可行）**

统一整数类型：`Int(i64)` 代替 I8/I16/I32/I64/U8/U16/U32/U64；引入 `HeapObj` 枚举统一 Str/Array/Map/Object。`Value` 大小降至 16 字节（f64 + tag），缓存友好度大幅提升。

---

### 3.2 改进点：用户异常机制经由 `thread_local` + `anyhow::Error` 传播，有额外开销 ⚡ 性能

**问题描述**

每次用户异常（`throw` 语句）：经历 `thread_local` 写、`anyhow::Error` 堆分配、Rust `?` 逐帧传播、栈展开、`thread_local` 读，开销巨大。

而 JVM 对用户异常：在字节码层查 `exception_table`，找到 handler，**直接跳转**，无堆分配或栈展开。

**建议改进**

利用现有的 `exception_table` 机制，定义 `ExecSignal` 枚举（Ok、UserException、InternalError），在解释器主循环中直接处理用户异常，无需 `anyhow` 传播和栈展开。

---
### 3.3 改进点：`exec_function` 每次调用都重新构建 `block_map` ⚡ 性能

**问题描述**

每次 `exec_function` 调用都重新构建 `block_map` (O(n) + HashMap 堆分配)。对热点函数（循环体内调用）是明显的性能瓶颈。

**建议改进**

模块加载时预计算 `block_index` 并缓存在 `Function` 结构体中，消除每次函数调用的 O(n) 开销，改为 O(1) 查找。

---

### 3.4 改进点：GC 模块为空，`Rc<RefCell<T>>` 无法处理循环引用 🛡️ 正确性

**问题描述**

```rust
// gc/mod.rs — 当前为空文件
// Phase 1 uses `Rc<RefCell<T>>` reference counting;
// this module will provide a tracing GC for Phase 3+
```

`Rc` 引用计数 GC 无法回收循环引用。以下代码在当前运行时中会永久泄漏：

```z42
class Node { Node? next; }
var a = new Node();
var b = new Node();
a.next = b;
b.next = a;  // 循环引用，Rc 计数永远不会归零
```

**建议**

明确 GC 迁移里程碑与方案，可选路径：

| 方案 | 成本 | 说明 |
|------|------|------|
| Weak 引用 + 手动打破循环 | 低 | 用户负责，不透明 |
| 引入 `Arc<GcCell<T>>` + epoch GC | 中 | 适合过渡期 |
| Boehm-Demers-Weiser GC | 中 | 保守式 GC，侵入性低 |
| 完整 tracing GC | 高 | 长期目标，性能最优 |

---

## 四、编译管线流程层面

### 4.1 改进点：`PipelineCore` 捕获所有异常，掩盖编译器内部 Bug 🛡️ 正确性

**问题描述**

`PipelineCore.cs` 的 `catch (Exception)` 捕获所有异常，包括编译器自身的 Bug（`NullReferenceException`、`KeyNotFoundException`），将其变成"UnsupportedSyntax"用户错误。**编译器 Bug 表现为用户代码错误**，极难调试定位。

**参考：** Roslyn 区分 `RecoverableException`（预期内的可恢复错误）和 ICE；GHC 直接在最外层打印 "panic: GHC internal error"。

**建议改进**

仅捕获 `UnsupportedLanguageFeatureException` 等预期内的编译错误；其他异常（编译器 Bug）让其传播，由顶层捕获并报告明确的 ICE 信息。

---

### 4.2 改进点：`FunctionDecl` 参数过多，互斥约束缺乏验证 🔧 可维护性

**问题描述**

13 个参数的 `FunctionDecl` record：创建时极易因位置混淆引入 Bug；`IsStatic / IsVirtual / IsOverride / IsAbstract` 之间存在互斥约束（`abstract` 不能是 `static` 等），但无构建时验证。

**建议改进**

引入 `FunctionModifiers` flags 枚举（Static、Virtual、Override、Abstract、Extern），替代散布的 bool 字段；在 record 的 init 方法中验证互斥约束（如 `Abstract && Static` 则抛异常）。

---

### 4.3 改进点：类型名称映射存在三处重复定义 🔧 可维护性

**问题描述**

类型名到内部类型的映射在三处各自维护（SymbolTable、FunctionEmitter、Z42Type）。新增或修改类型时必须同步修改三处，违反 DRY 原则。

**建议改进**

提取统一的 `TypeRegistry.cs`，定义 `TypeEntry`（canonical name、aliases、IrType、SemanticType、特性）的中央数据源；`SymbolTable`、`FunctionEmitter`、`Z42Type` 均从 `TypeRegistry.All` 派生各自需要的查找表。

---

## 五、代码质量与工程层面

### 5.1 改进点：`BoundError` 到达 Codegen 时抛出内部异常 🛡️ 正确性

```csharp
// FunctionEmitterExprs.cs
BoundError err => throw new InvalidOperationException(
    $"BoundError reached codegen: {err.Message}"),
```

这个 `InvalidOperationException` 会被 `PipelineCore` 的 `catch (Exception)` 捕获，变成误导性的"UnsupportedSyntax"用户错误（参见 4.1）。

正确做法是：`TypeChecker` 在检测到 `BoundError` 后应确保 `diags.HasErrors == true`，`PipelineCore` 在 TypeCheck 阶段发现错误后提前返回，保证含有 `BoundError` 的 BoundTree 永远不会进入 Codegen。

---

### 5.2 改进点：`IsSubclassOf` 每次都线性遍历继承链 ⚡ 性能

**问题描述**

`IsSubclassOf` 每次调用都线性遍历继承链（O(depth)）。在类型检查阶段被高频调用（每个方法参数、赋值表达式），对深度继承链是性能瓶颈。

**建议改进**

在 `SymbolCollector` 阶段预计算每个类的完整祖先集合（`Dictionary<string, HashSet<string>>`），`IsSubclassOf` 改为 O(1) 集合成员查询。

---

## 六、改进优先级路线图

| 优先级 | 改进项 | 影响 | 工作量 |
|--------|--------|------|--------|
| ✅ ~~P0~~ | ~~区分 ICE 与用户错误，避免掩盖编译器 Bug（4.1）~~ | 编译器可靠性 | 小 | **已完成** |
| ✅ ~~P0~~ | ~~消除命名变量槽残留代码（2.2）~~ | 运行时性能 | 小 | **已完成（IR 已是纯寄存器机）** |
| ✅ ~~P1~~ | ~~统一类型名映射为 `TypeRegistry`（4.3）~~ | 可维护性 | 小 | **已完成** |
| ✅ ~~P1~~ | ~~`Value` 枚举合并为 I64+F64（3.1）~~ | 内存/缓存性能 | 中 | **已完成** |
| ✅ ~~P1~~ | ~~`exec_function` 预计算 `block_index`（3.3）~~ | 解释器调用性能 | 小 | **已完成** |
| ✅ ~~P1~~ | ~~`IsSubclassOf` 预计算祖先集合（5.2）~~ | 类型检查性能 | 小 | **已完成** |
| ✅ ~~P2~~ | ~~`TypeEnv.LookupVar` 区分变量/类名（1.3）~~ | 类型检查正确性 | 小 | **已完成** |
| ✅ ~~P2~~ | ~~统一 BoundExpr 遍历方式，去除 Visitor/switch 双重路径（1.5）~~ | 可扩展性 | 中 | **已完成** |
| ✅ ~~P2~~ | ~~`TypeChecker._currentClass` → `TypeEnv.CurrentClass`（1.4）~~ | 并发安全性 | 中 | **已完成** |
| ✅ ~~P2~~ | ~~用户异常改用 `ExecOutcome` 值传播（3.2）~~ | 异常处理性能 | 中 | **已完成** |
| ✅ ~~P2~~ | ~~`FunctionDecl` 引入 `FunctionModifiers` flags（4.2）~~ | 代码健壮性 | 小 | **已完成** |
| ✅ ~~P3~~ | ~~IR 序列化与内存模型解耦（2.1）~~ | 架构清洁度 | 中 | **已完成** |
| 🟢 P3 | `IrVerifier` 增加 CFG dominance 验证（2.3） | 编译器正确性 | 大 |
| 🟢 P3 | 统一 `TypeConversion` 枚举，完善类型兼容性规则（1.2） | 类型系统完整性 | 中 |
| 🟢 P3 | GC 迁移路径设计（tracing GC / epoch GC）（3.4） | 长期正确性 | 大 |

---

## 近期完成情况详述（2026-04-18 迭代）

### 已完成的改进

#### 1. FunctionEmitterCalls 的 Instance 方法分派（关联改进 1.5、2.2）

**完成内容：**
- `BoundCallKind.Instance` 和 `BoundCallKind.Virtual` 现在分离处理
- Instance 方法调用优先查询 DepIndex，确保 stdlib 方法使用 `CallInstr`（静态调用）
- 用户定义的类实例方法才使用 `VCallInstr`（虚调用）
- 修复了 List/Dictionary/Array 等集合类型被误识别为虚方法的问题

**代码位置：** `src/compiler/z42.Semantics/Codegen/FunctionEmitterCalls.cs:49-80`

**效果：**
- 消除了对集合类型方法的动态分派开销
- 为后续 JIT 生成代码时的方法内联打下基础
- 部分解决了改进点 2.2 的"寄存器机不变式"问题——stdlib 方法调用完全避免了虚方法开销

---

#### 2. 内置类型静态方法映射（关联改进 4.3）

**完成内容：**
- TypeChecker 中新增内置类型到 stdlib 类名的映射：
  ```csharp
  string resolvedClassName = tgtName switch
  {
      "string" => "Std.String",
      "int" => "Std.Int",
      "double" => "Std.Double",
      "bool" => "Std.Bool",
      _ => tgtName
  };
  ```
- `string.IsNullOrEmpty()` 现在正确编译为 `@Std.String.IsNullOrEmpty` 调用
- 所有内置类型的静态方法通过统一的映射完成

**代码位置：** `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Calls.cs:50-77`

**效果：**
- 消除了编译器中"内置类型如何调用静态方法"的歧义
- 为改进点 4.3（统一类型名映射）奠定了基础——这是 TypeRegistry 的一个缩小版本
- 为添加更多内置类型（如 `float`, `char` 等）做好了准备

---

#### 3. 后缀操作符返回值修复（关联改进 5.1）

**完成内容：**
- 修复 `i++` 和 `i--` 返回错误值（新值而非旧值）的 bug
- 方案：在 `WriteBackName` 前先复制旧值到新寄存器
  ```csharp
  var savedOldReg = Alloc(ToIrType(post.Type));
  Emit(new CopyInstr(savedOldReg, oldReg));
  // ... 计算新值 ...
  WriteBackName(id.Name, newReg);
  return savedOldReg;  // 返回真正的旧值
  ```

**代码位置：** `src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs:281-293`

**效果：**
- 纠正了前缀/后缀操作符的语义
- 通过额外 copy 指令保证正确性（暂时增加了一条指令，可在 JIT 优化阶段消除）

---

#### 4. 完全消除 BoundCallKind.Unresolved（关联改进 1.3、4.1）

**完成内容：**
- BoundCallKind 枚举删除了 `Unresolved` 变体
- 所有 stdlib 方法调用在 TypeCheck 阶段解析为 Static/Instance/Virtual/Free
- DepIndex 查询失败时返回 `Z42Type.Unknown`，而非放弃解析
- FunctionEmitterCalls 删除了 `EmitUnresolvedCall` 方法

**代码位置：**
- 删除：`src/compiler/z42.Semantics/Bound/BoundExpr.cs` 的 `Unresolved` 变体
- 修改：`src/compiler/z42.Semantics/Codegen/FunctionEmitterCalls.cs` 的 Instance/Static 分支

**效果：**
- 编译时类型检查更完整，运行时错误减少
- 消除了"延迟到 IrGen 的神秘调用解析"，提高可读性
- 为改进点 4.1（区分 ICE 与用户错误）做准备——编译器内部不再有"未解析调用"的模糊状态

---

### 对现有改进项的影响

| 改进项 | 状态 | 说明 |
|--------|------|------|
| 1.2 类型兼容性规则 | ⏳ 部分缓解 | 整数统一为 I64，小整数问题暂时规避 |
| 1.3 LookupVar Unknown | ✅ 已完成 | LookupVar 只返回变量类型，新增 IsClassName 区分；BindIdent 增加 ImportedClassNames 检查 |
| 1.4 _currentClass | ✅ 已完成 | 消除共享可变状态，改为 TypeEnv.CurrentClass 不可变属性 |
| 1.5 Visitor vs switch | ✅ 已完成 | 删除 IBoundVisitor 接���和所有 Accept 方法，统一用 switch |
| 2.2 消除命名变量槽 | 🟢 进展 | Instance 方法分派确保了 stdlib 方法不使用虚方法开销 |
| 3.3 block_index 预计算 | ✅ 已完成 | loader.rs 模块加载时预计算 block_index |
| 4.1 ICE 区分 | ✅ 已完成 | TryBind* 区分 CompilationException 与 ICE，E0900 诊断码 |
| 4.2 FunctionModifiers | ✅ 已完成 | 5 个 bool 替换为 `[Flags] enum FunctionModifiers` |
| 4.3 类型名映射 | ✅ 已完成 | TypeRegistry.cs 作为唯一数据源 |
| 5.1 BoundError → Codegen | ✅ 已完成 | BoundError 到达 Codegen 时抛出 ICE |
| 5.2 IsSubclassOf 预计算 | ✅ 已完成 | SymbolTable 构造时预计算祖先集合，O(1) 查找 |

---

## 快速开始指南

**如果时间有限，优先在以下三个方向投入：**

### 🔴 最高优先级（应立即推进）

1. ~~**区分 ICE 与用户错误（4.1）**~~ ✅ **已完成**
   - TypeChecker.TryBind* 区分 CompilationException 与 ICE，新增 E0900 诊断码
   - PipelineCore 已正确只捕获 CompilationException，其他异常传播

2. **消除 `StoreInstr`/`LoadInstr`（2.2）** — 中等工作量
   - **为什么：** 命名变量槽破坏寄存器机不变式，JIT 实现时会成为瓶颈
   - **工作：** IR 代码生成阶段为每个变量分配寄存器 ID，删除 `StoreInstr`/`LoadInstr`
   - **验收：** Frame 结构去掉 `HashMap<String, Value>`，golden tests 全绿

3. ~~**统一类型名映射（4.3）**~~ ✅ **已完成**
   - TypeRegistry.cs 作为所有原始类型映射的唯一数据源
   - SymbolTable.ResolveType 和 FunctionEmitter.ToIrType 均从 TypeRegistry 派生

### 🟡 次优先级（下个迭代）

- `Value` 枚举合并整数类型（3.1）— 内存/缓存性能跃升
- ~~`IsSubclassOf` 预计算祖先集合（5.2）~~  ✅ **已完成** — SymbolTable 构造时预计算
- ~~`exec_function` 预计算 `block_index`（3.3）~~ ✅ **已完成** — loader.rs 模块加载时预计算

---

## 与项目规范的关联

### 当前实现阶段的适用性

| 改进项 | M6（当前） | M7+ | 说明 |
|--------|-----------|-----|------|
| 4.1（ICE 区分） | ✅ **已完成** | — | E0900 ICE 诊断码 |
| 2.2（消除命名槽） | ✅ 必做 | — | JIT 实现的前置条件 |
| 4.3（类型注册表） | ✅ **已完成** | — | TypeRegistry.cs |
| 1.4（BindContext） | ⏳ 可做 | ✅ 必做 | 并发编译的前置，M7+ 并行编译时强制 |
| 3.1（Value 布局） | ⏳ 可做 | ✅ 建议做 | 性能优化，M7 后期或 M8 |
| 3.2（异常信号） | ⏳ 可做 | ✅ 建议做 | 性能优化，与 JIT 协同 |
| 3.4（GC 设计） | — | ✅ 必做 | M8 引入完整对象系统时 |

### 纳入工作流的建议

P0 改进项应以独立的 `openspec/changes/` 变更单位立项，参考 `.claude/rules/workflow.md`：

```
openspec/changes/fix-compiler-error-handling/
├── proposal.md     — 为什么要区分 ICE
├── design.md       — 如何重构异常捕获
├── tasks.md        — 具体修改清单
```

完成后自动更新 `docs/roadmap.md` 的 M6 进度表。

---

## 七、总结

z42 编译器的整体架构设计是合理的——分层清晰、模块边界明确、代码风格统一，Bound AST + IR 的两层表示也体现了对 Roslyn 等成熟编译器的参考。

主要欠缺集中在两个维度：

1. **运行时性能细节**：`Value` 枚举大小、每次函数调用的 HashMap 构建、命名变量槽的字符串查找，这些小细节在热点路径上累积成可观的性能差距。
2. **编译器工程健壮性**：全局异常捕获掩盖 ICE、`_currentClass` 共享可变状态、Visitor 与 switch 双重遍历路径、类型名映射三处重复——这些问题在当前规模下尚可管理，但随着语言特性的增长会逐渐成为维护负担。

优先推进的两个方向：
- **参照 Roslyn**：将 `TypeChecker` 的可变状态改为不可变 `BindContext` 参数传递
- **参照 JVM 字节码**：将命名变量槽消除，Frame 统一使用整数索引的寄存器数组