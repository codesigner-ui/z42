# z42 项目代码优化计划

> **分析日期**：2026-04-14  
> **分析范围**：`src/compiler`（C# 编译器）、`src/runtime`（Rust 运行时）、`src/libraries`（标准库）、`src/compiler/z42.Tests`（测试）  
> **关联文档**：[code-architecture-review.md](code-architecture-review.md)（架构级分析）

---

## 一、项目整体架构概述

z42 是一个完整的自制编程语言工具链，架构分为以下层次：

```
┌─────────────────────────────────────────────────┐
│              z42 源代码 (.z42)                   │
└────────────────────┬────────────────────────────┘
                     │
┌────────────────────▼────────────────────────────┐
│           编译器前端（C# / .NET）                 │
│  z42.Syntax   → Lexer + Parser → AST             │
│  z42.Semantics → TypeChecker + IrGen → IrModule  │
│  z42.IR       → BinaryFormat (ZBC / ZPKG)        │
│  z42.Pipeline → 单文件 / 包级别编译入口           │
│  z42.Project  → 项目清单 (TOML) + 包管理          │
│  z42.Driver   → CLI 驱动（build / check / emit） │
└────────────────────┬────────────────────────────┘
                     │ .zbc / .zpkg 二进制
┌────────────────────▼────────────────────────────┐
│            运行时（Rust）                         │
│  metadata   → 二进制格式解析 + 模块合并           │
│  interp     → 字节码解释执行                     │
│  jit        → 即时编译（进行中）                  │
│  gc         → 垃圾回收                           │
│  corelib    → 原生标准库实现                     │
│  thread     → 线程模型                           │
└─────────────────────────────────────────────────┘
```

整体架构设计清晰，分层合理。以下是系统分析后发现的可改进点，按优先级分类列出。

---

## 二、问题清单与详细分析

### 🔴 HIGH — 架构级问题

#### H1：`Sha256Hex` 方法重复定义

| 属性 | 内容 |
|------|------|
| **位置** | `z42.Pipeline/PackageCompiler.cs:385` 和 `z42.Pipeline/SingleFileCompiler.cs:144` |
| **问题** | 完全相同的私有静态方法在两个类中各自定义，违反 DRY 原则 |
| **关联** | 独立问题 |

**现状代码（两处相同）：**
```csharp
static string Sha256Hex(string text)
{
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
    return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant();
}
```

**建议方案：**  
在 `z42.Pipeline` 或 `z42.Core` 中新建 `CompilerUtils` 静态工具类，统一放置此类共享辅助方法。

---

#### H2：`ParseException` 未走 `DiagnosticBag` 流程，且错误码错误

| 属性 | 内容 |
|------|------|
| **位置** | `PackageCompiler.cs:296-300`、`SingleFileCompiler.cs:43`、`GoldenTests.cs:176` |
| **问题** | 语法错误通过 `catch (ParseException)` 单独处理，使用了硬编码字符串 `"E0001"`（错误码），而实际应用 `DiagnosticCodes.UnexpectedToken`（`E0201`）；同时与其他阶段错误走的 `DiagnosticBag` 流程完全割裂 |
| **关联** | 架构问题 A5（Parser 单错误停止）的近期可修复子集 |

**现状代码：**
```csharp
catch (ParseException ex)
{
    // 错误：使用了错误的 error code E0001，应为 E0201
    Console.Error.WriteLine(
        $"error[E0001]: {Path.GetFileName(sourceFile)}:{ex.Span.Line}:{ex.Span.Column}: {ex.Message}");
    return null;
}
```

**建议方案：**  
让 `Parser` 在捕获异常后将错误写入 `DiagnosticBag`（`GoldenTests` 中已正确实现此模式，应统一），使全链路诊断通过同一个 bag 流出。

> **注意**：本项仅修正错误码和统一 DiagnosticBag 流程。完整的 Parser 错误恢复（多错误报告）属于架构问题 A5，应独立规划。

---

#### H3：`TypeEnv` 中内置类名列表硬编码

| 属性 | 内容 |
|------|------|
| **位置** | `z42.Semantics/TypeCheck/TypeEnv.cs:12-18` |
| **问题** | `BuiltinClasses` 是一个 `static readonly HashSet<string>` 硬编码集合，每新增标准库类型都必须手动维护 |
| **关联** | 架构问题 A1（语义层分裂）的表层症状之一 |

**现状代码：**
```csharp
private static readonly HashSet<string> BuiltinClasses = new(StringComparer.Ordinal)
{
    "Console", "Math", "String", "Convert", "Environment",
    "StringBuilder", "Enumerable", "List", "Array", "Dictionary",
    "File", "Path", "Directory", "Stream", "Regex",
    "GC", "Thread", "Task", "DateTime", "TimeSpan",
};
```

**建议方案：**  
从标准库包元数据（`StdlibCallIndex` 或 ZPKG 中已有的类名表）动态注入已知类名，而不是静态硬编码。长期来看，`TypeEnv` 的 root 构造函数应接收一个可由外部注入的内置名称集合。

---

#### H4：`CompileFile` 与 `CheckFile` 大量重复代码

| 属性 | 内容 |
|------|------|
| **位置** | `PackageCompiler.cs:286-340` |
| **问题** | 两个方法前半段（词法分析 → 解析 → 类型检查）逻辑几乎完全相同（~95%），违反 DRY 原则；未来修改错误处理逻辑时需同步修改两处 |
| **关联** | 与 H2（ParseException 处理）同在 PackageCompiler 中，可一起重构 |

**建议方案：**  
提取公共私有方法 `TryCompileToIr(string sourceFile, StdlibCallIndex? idx, out CompilationUnit? cu, out IrModule? ir, out DiagnosticBag diags)`，`CompileFile` 和 `CheckFile` 均调用此方法。

---

#### H5：运行时 `find_block` / `find_handler` 每次调用线性扫描

| 属性 | 内容 |
|------|------|
| **位置** | `src/runtime/src/interp/mod.rs:178-183` |
| **问题** | 每次基本块跳转都通过 `iter().position(|b| b.label == label)` 进行 O(n) 线性搜索（被调用 4 次）；异常处理查找同样是 O(n×m)。对于有大量基本块的函数（如展开后的循环），这会显著拖慢执行速度 |
| **关联** | 与 M2（Frame HashMap）同属运行时性能，可合并优化 |

**现状代码：**
```rust
fn find_block(func: &Function, label: &str) -> Result<usize> {
    func.blocks
        .iter()
        .position(|b| b.label == label)   // O(n) 每次跳转都扫描
        .with_context(|| format!("undefined block `{label}`"))
}
```

**建议方案：**  
在 `Function` 加载（或首次调用前）时，构建 `label → block_index` 的 `HashMap<&str, usize>`，将跳转查找降至 O(1)。

---

### 🟠 MEDIUM — 设计缺陷

#### M1：`Z42Type.IsReferenceType` 硬编码集合类型名

| 属性 | 内容 |
|------|------|
| **位置** | `Z42Type.cs:92-97` |
| **问题** | `"List"` / `"Dictionary"` 被硬编码在语义类型系统中，绕过了 `Z42ClassType` 的统一路径 |
| **关联** | 架构问题 A4（类型层次镜像）的局部表现 |

```csharp
public static bool IsReferenceType(Z42Type t) =>
    t is Z42PrimType { Name: "string" or "object" or "List" or "Dictionary" }
    // ...
```

**建议方案：**  
`List` / `Dictionary` 作为用户可见类，其类型表示应为 `Z42ClassType`（或泛型版本），而非特殊 `Z42PrimType`；`IsReferenceType` 对所有 `Z42ClassType` 实例均返回 `true` 即可涵盖它们。

---

#### M2：`Frame` 寄存器使用 `HashMap<u32, Value>` 性能欠佳

| 属性 | 内容 |
|------|------|
| **位置** | `src/runtime/src/interp/mod.rs:73-85`、`ops.rs` |
| **问题** | IR 寄存器编号为密集整数（从 0 开始分配），使用 `HashMap` 有额外哈希计算和堆分配开销；对解释执行的热路径影响显著 |
| **关联** | 与 H5（find_block）同属运行时性能，可合并优化 |

**建议方案：**  
将 `regs: HashMap<u32, Value>` 改为 `regs: Vec<Value>`（预分配函数最大寄存器数），直接下标访问，避免哈希计算。`IrFunction` 可在序列化时记录 `max_reg` 字段以支持预分配。

---

#### M3：stdlib 命名空间提取逻辑三处重复

| 属性 | 内容 |
|------|------|
| **位置** | `SingleFileCompiler.cs:91-97`、`PackageCompiler.cs:249-255`、`GoldenTests.cs:342-351` |
| **问题** | "从 `CallInstr` 指令集中提取 stdlib 命名空间" 的逻辑在三个不同位置各自实现，且细节不一致（前两处匹配 `"z42."` 前缀，后者匹配 `"Std."` 前缀） |
| **关联** | 与 M4（BuildStdlibIndex 重复）同属 stdlib 相关重复，建议一起清理 |

**建议方案：**  
在 `z42.IR` 项目中为 `IrModule` 添加扩展方法 `GetUsedNamespaces()`，统一该逻辑，所有调用方改用此方法。

---

#### M4：`GoldenTests` 中完整复制了 `BuildStdlibIndex` 逻辑

| 属性 | 内容 |
|------|------|
| **位置** | `GoldenTests.cs:85-101` vs `PackageCompiler.cs:393-413`，另 `ZbcRoundTripTests.cs:47+` 也有类似副本 |
| **问题** | 测试代码中有 `BuildIndexFromDir` 和 `BuildStdlibIndexFromDir` 两个方法，都是 `PackageCompiler.BuildStdlibIndex` 的副本，而后者已是 `public static` 可直接调用 |

**建议方案：**  
直接调用 `PackageCompiler.BuildStdlibIndex(new[] { libsDir })`，删除测试中的副本方法。

---

#### M5：`IrModule.StringPool` 对外暴露为可变 `List<string>`

| 属性 | 内容 |
|------|------|
| **位置** | `IrModule.cs:10` |
| **问题** | `IrModule` 是编译产物（数据模型），其 `StringPool` 暴露为可变 `List<string>`，使任何持有 `IrModule` 引用的代码均可在代码生成完成后继续修改字符串池，封装性差 |

**建议方案：**  
`IrGen.Generate()` 内部使用可变 builder，完成后返回带 `IReadOnlyList<string>` 的不可变 `IrModule`，防止外部意外修改。

---

#### M6：`IsObjectClass` 判断逻辑在 `TypeChecker` 和 `IrGen` 中各自定义

| 属性 | 内容 |
|------|------|
| **位置** | `TypeChecker.Classes.cs:12-13`、`IrGen.cs:171` |
| **问题** | 同一个概念（"哪些名字代表 Object 基类"）在两个不同类中各自定义，若一处修改而另一处遗漏则产生不一致行为 |
| **关联** | 架构问题 A1（TypeChecker/IrGen 知识重复）的局部表现 |

```csharp
// TypeChecker.Classes.cs
private static bool IsObjectClass(string name) => name is "Object" or "Std.Object";

// IrGen.cs
private static bool IsObjectClass(string name) => name is "Object" or "Std.Object";
```

**建议方案：**  
将此方法提升至 `z42.IR` 或 `z42.Core` 中的共享静态类（如 `WellKnownNames`），两处均引用同一定义。

---

### 🟡 LOW — 可维护性改进

#### L1：`IrInstr` 的 `JsonPolymorphic` 注解手动维护成本高

| 属性 | 内容 |
|------|------|
| **位置** | `IrModule.cs:53-102`（50 个 IrInstr 注解）、`IrModule.cs:173-177`（4 个 Terminator 注解） |
| **问题** | 共 54 个 `[JsonDerivedType(...)]` 注解，每新增一种指令类型都需手动注册，容易遗漏 |

**建议方案（长期）：**  
考虑使用 Source Generator 自动扫描所有 `: IrInstr` 的子类并生成注册代码；或改用自定义 `JsonConverter` 按 discriminator 字段反射分发，去除逐一注册的硬编码。

---

#### L2：`_currentClass` 状态字段缺乏作用域保护

| 属性 | 内容 |
|------|------|
| **位置** | `TypeChecker.cs:42`，被引用于 `Exprs.cs:51,349,398-399` |
| **问题** | `_currentClass` 在进入类方法检查时被设置，若异常路径或未来代码改动导致未及时清除，会污染后续全局函数的类型检查上下文 |

**建议方案：**  
使用 RAII Scope 模式保护：
```csharp
private IDisposable EnterClass(string name)
{
    _currentClass = name;
    return Disposable.Create(() => _currentClass = null);
}
// 使用方：using var _ = EnterClass(cls.Name);
```

---

#### L3：`exec_instr.rs` 是一个超大 `match` 语句

| 属性 | 内容 |
|------|------|
| **位置** | `src/runtime/src/interp/exec_instr.rs`（362 行） |
| **问题** | 单一函数 `exec_instr` 处理所有指令类型；接近 300 行软限制。继续增长将触发硬限制（500 行） |

**建议方案：**  
在保持单一 `match` 结构的前提下，按指令类别提取私有辅助函数（如 `exec_arithmetic`、`exec_object_ops`），减少 arm 内联代码量，提高可读性。

---

#### L4：Golden Test 测试类别依赖目录路径字符串判断

| 属性 | 内容 |
|------|------|
| **位置** | `GoldenTests.cs:124-126` |
| **问题** | 通过判断路径中是否包含 `/errors/` 或 `/run/` 字符串来区分测试类别，是隐式约定；新建测试时若目录命名不符合规则则静默跳过 |

**建议方案：**  
在测试目录下使用显式 `test.toml` 声明测试类型（`kind = "error"` / `kind = "run"` / `kind = "codegen"`），测试发现逻辑读取该文件而非依赖路径名。

---

#### L5：`DiagnosticBag.PrintAll()` 同时承担输出副作用和返回布尔值

| 属性 | 内容 |
|------|------|
| **位置** | `DiagnosticBag.cs:49-55` |
| **问题** | 一个方法同时做"打印到 stderr"和"返回是否有错误"两件事；调用方常写 `if (diags.PrintAll()) return 1;`，语义模糊，且副作用隐蔽 |

**建议方案：**  
拆为两个职责：
```csharp
// 查询（无副作用）
bool HasErrors => ...

// 输出（副作用，返回 void）
void PrintAll(TextWriter? writer = null) { ... }
```
调用方改为：`diags.PrintAll(); if (diags.HasErrors) return 1;`

---

#### L6：`PackageCompiler.BuildTarget` 方法过长

| 属性 | 内容 |
|------|------|
| **位置** | `PackageCompiler.cs:144-282`（约 139 行） |
| **问题** | 单一方法包含：库目录扫描 → nsMap 构建 → ZBC 扫描 → 文件编译 → 依赖分析 → 打包写出，逻辑阶段过多，难以单独测试某一阶段 |
| **关联** | 架构问题 A10（PackageCompiler 职责过重） |

**建议方案：**  
拆分为独立私有方法：
- `ScanLibsForNamespaces(libsDirs)` → `Dictionary<string, string>`
- `ScanZbcForNamespaces(dirs, nsMap)`
- `CompileSourceFiles(files, stdlibIndex)` → `List<CompiledUnit>?`
- `BuildDependencyMap(units, nsMap)` → `List<ZpkgDep>`

---

## 三、问题依赖关系

```
┌─────────────────────────────────────────────────────────────────┐
│ 代码级问题之间的依赖                                              │
│                                                                  │
│  H2 (ParseException) ─┬─► H4 (CompileFile/CheckFile)            │
│                       │   同在 PackageCompiler，合并重构更高效     │
│                       │                                          │
│  M3 (命名空间提取) ────┼─► M4 (BuildStdlibIndex 重复)             │
│                       │   同属 stdlib 基础设施，一起清理           │
│                       │                                          │
│  H5 (find_block) ─────┼─► M2 (Frame HashMap)                    │
│                       │   同属运行时热路径，一起优化效果最佳        │
│                       │                                          │
│  M6 (IsObjectClass) ──┤                                          │
│  H3 (BuiltinClasses) ─┴─► 长期解法都指向架构 A1 (SemanticModel)  │
│  M1 (IsReferenceType) ─┘                                        │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│ 与架构问题的关联                                                  │
│                                                                  │
│  A1 (语义层分裂)                                                 │
│    ├── M6 (IsObjectClass 重复) ← 症状                            │
│    ├── H3 (BuiltinClasses 硬编码) ← 症状                         │
│    └── M1 (IsReferenceType 硬编码) ← 症状                        │
│                                                                  │
│  A5 (Parser 单错误停止)                                          │
│    └── H2 (ParseException 处理) ← 近期可修复子集                  │
│                                                                  │
│  A10 (PackageCompiler 职责过重)                                   │
│    └── L6 (BuildTarget 过长) ← 局部症状                           │
└─────────────────────────────────────────────────────────────────┘
```

---

## 四、优化执行计划

按"依赖关系 + 改动收益比"排列，同一批次内的任务可并行。

### 批次 1：快速消除代码重复（低风险，高确定性）

| 任务 | 改动难度 | 改动范围 |
|------|---------|---------|
| **[H1]** 抽取 `Sha256Hex` 到 `CompilerUtils` 工具类 | 简单 | 2 文件 |
| **[M6]** 将 `IsObjectClass` 提升至共享静态类 `WellKnownNames` | 简单 | 2 文件 |
| **[M4]** 删除测试中的 `BuildStdlibIndex` 副本，直接调用 `PackageCompiler` 版本 | 简单 | 2 文件 |
| **[M3]** stdlib 命名空间提取统一为 `IrModule.GetUsedNamespaces()` | 简单 | 3 文件 |

### 批次 2：PackageCompiler 统一重构（H2 + H4 合并）

| 任务 | 改动难度 | 改动范围 |
|------|---------|---------|
| **[H4]** 提取 `TryCompileToIr` 公共方法，消除 `CompileFile`/`CheckFile` 重复 | 中等 | 1 文件 |
| **[H2]** 在 `TryCompileToIr` 中统一 `ParseException` → `DiagnosticBag` 流程，修正错误码 | 中等 | 3 文件 |
| **[L6]** 拆分 `BuildTarget` 为多个内聚子方法 | 简单 | 1 文件 |

> H2 和 H4 涉及同一段代码（PackageCompiler 的编译流程），合并重构避免二次修改。

### 批次 3：API 清洁度提升

| 任务 | 改动难度 | 改动范围 |
|------|---------|---------|
| **[L5]** 拆分 `DiagnosticBag.PrintAll()` 为查询 + 输出 | 简单 | 多个调用点 |
| **[M5]** `IrModule.StringPool` 暴露为 `IReadOnlyList<string>` | 简单 | 1 文件 + 调用点 |
| **[L2]** 用 RAII Scope 保护 `_currentClass` | 简单 | 1 文件 |

### 批次 4：运行时性能优化（H5 + M2 合并）

| 任务 | 改动难度 | 改动范围 |
|------|---------|---------|
| **[H5]** 为 `Function` 构建 `label → index` 的 `HashMap`，消除线性搜索 | 中等 | interp 模块 |
| **[M2]** `Frame.regs` 从 `HashMap<u32, Value>` 改为 `Vec<Value>` | 中等 | interp + ops 模块 |

> 两者同属解释器热路径，合并优化可一次性调整 `Frame` 结构和相关接口。需在 IR 层添加 `max_reg` 字段，涉及编译器 + 运行时两端改动。

### 批次 5：长期类型系统改进（依赖架构决策）

| 任务 | 改动难度 | 前置条件 |
|------|---------|---------|
| **[H3]** `BuiltinClasses` 从标准库元数据动态注入 | 复杂 | 需确定元数据注入接口 |
| **[M1]** `List`/`Dictionary` 迁移至 `Z42ClassType` | 复杂 | 需设计泛型类型表示 |

> 这两项的完整解决方案指向架构问题 A1（引入 SemanticModel）。在 A1 未实施前，可做局部改进（如 H3 改为构造函数注入），但不应过度投入。

### 持续改进（无前置依赖，按需执行）

| 任务 | 改动难度 | 备注 |
|------|---------|------|
| **[L3]** `exec_instr.rs` 按指令类别提取辅助函数 | 中等 | 当文件增长接近 500 行时触发 |
| **[L4]** Golden Test 改用 `test.toml` 声明类别 | 中等 | 当测试目录结构变复杂时触发 |
| **[L1]** 评估 Source Generator 自动注册 `IrInstr` | 复杂 | 当指令数超过 60 个时考虑 |

---

## 五、优先级总览

| 问题 | 优先级 | 主要影响 | 改动难度 | 批次 |
|------|--------|----------|----------|------|
| H1 Sha256Hex 重复 | 🔴 HIGH | 维护性 | 简单 | 1 |
| H2 ParseException 不一致 | 🔴 HIGH | 正确性 | 中等 | 2 |
| H3 BuiltinClasses 硬编码 | 🔴 HIGH | 扩展性 | 复杂 | 5 |
| H4 CompileFile/CheckFile 重复 | 🔴 HIGH | 维护性 | 中等 | 2 |
| H5 find_block 线性搜索 | 🔴 HIGH | 性能 | 中等 | 4 |
| M1 IsReferenceType 硬编码 | 🟠 MEDIUM | 正确性 | 复杂 | 5 |
| M2 Frame HashMap 性能 | 🟠 MEDIUM | 性能 | 中等 | 4 |
| M3 命名空间提取重复 | 🟠 MEDIUM | 维护性 | 简单 | 1 |
| M4 GoldenTests 重复代码 | 🟠 MEDIUM | 维护性 | 简单 | 1 |
| M5 StringPool 可变暴露 | 🟠 MEDIUM | 安全性 | 简单 | 3 |
| M6 IsObjectClass 重复 | 🟠 MEDIUM | 一致性 | 简单 | 1 |
| L1 JsonDerivedType 手动维护 | 🟡 LOW | 维护性 | 复杂 | 持续 |
| L2 _currentClass 作用域 | 🟡 LOW | 稳健性 | 简单 | 3 |
| L3 exec_instr.rs 过大 | 🟡 LOW | 可读性 | 中等 | 持续 |
| L4 Golden Test 路径依赖 | 🟡 LOW | 健壮性 | 中等 | 持续 |
| L5 PrintAll 副作用混杂 | 🟡 LOW | 清晰性 | 简单 | 3 |
| L6 BuildTarget 过长 | 🟡 LOW | 可读性 | 简单 | 2 |

---

*本报告由代码静态分析生成。各问题已与 [code-architecture-review.md](code-architecture-review.md) 中的架构问题交叉关联，建议结合两份报告统筹规划。*
