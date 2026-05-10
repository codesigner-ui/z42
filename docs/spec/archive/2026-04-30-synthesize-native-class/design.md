# Design: Native class synthesis from manifest (C11b — Path B1)

## Architecture

```
CompilationUnit (post-Parser)
       │
       │  cu.NativeImports = [{Name="Counter", LibName="numz42"}, ...]
       │  cu.Classes       = [user-written classes]
       ▼
┌─────────────────────────────────────────────────────────────────────┐
│ NativeImportSynthesizer.Run(cu, locator)                            │
│   for each NativeTypeImport ni in cu.NativeImports:                 │
│     manifestPath = locator.Locate(ni.LibName, sourceDir)            │
│     manifest     = NativeManifest.Read(manifestPath)         // E0909│
│     typeEntry    = manifest.Types.find(t => t.Name == ni.Name)      │
│         else throw NativeImportException(E0916, "type not in mfst") │
│     class        = SynthesizeClass(typeEntry, manifest, ni.Span)    │
│     cu.Classes.Add(class)                                           │
└─────────────────────────────────────────────────────────────────────┘
       │
       ▼
TypeChecker (无改动) → Codegen (无改动) → IR / VM 既有 CallNative dispatch
```

合成流程**只读 manifest 数据**，不查文件系统其他内容；不动 VM；不动 IR；不引入新 IR 指令。

## Path B 内部权衡（已锁定 B1）

| 维度 | B1 — handle 风味（本 spec） | B2 — VM-owned 风味（C11c 后续） |
|---|---|---|
| 合成类的字段 | 无脚本可见字段（数据藏在 native handle 后） | 真实字段（VM Object 直接持有） |
| Native ABI 改动 | **无**（C2–C10 保持原状） | 加 `z42_obj_get/set_field` etc. |
| 适配场景 | 包 sqlite/curl/openssl/regex_t 等 opaque-handle 库 | 自定义 native 类型，希望脚本端可读字段 / 调试器可见 |
| 复杂度 | 编译器一个 pass + 一个签名解析器 | 上述 + Rust 端 ABI 扩展 + VM 端字段访问路径 |
| 字段访问性能 | native 内自由 / z42 看不到 | z42 端经 VM API 走一跳 callback |
| **C11b 选择** | ✅ 落地 | ⏭️ 推后 |

> **用户提议保留**：脚本端 `class` 上 `[Repr(C)]` 让用户在脚本里声明布局，与 native struct memory layout 对齐——这对 B1 是无影响的扩展（C11d 范围）。

## Decisions

### Decision 1: 合成位置——Parser 之后，TypeChecker 之前的独立 pass

**选项**：
- A：嵌进 `Parser`——parser 直接调用 `NativeManifest.Read` 把 manifest 数据写进 AST
- B：嵌进 `TypeChecker.Check`——symbol collection 阶段动态合成
- **C：独立 pass（选）**——`NativeImportSynthesizer` 跑在 Parser 与 TypeChecker 之间

**理由**：
- Parser 应保持只做语法（不接触文件 I/O / manifest schema），符合现有架构
- TypeChecker 不应承担 manifest 加载（职责拆分），合成产物应是普通 `ClassDecl`，TypeChecker 一视同仁
- 独立 pass 让合成可单测、可注入 mock locator、错误归因清晰

### Decision 2: Manifest 路径解析——`INativeManifestLocator` 注入式

```csharp
public interface INativeManifestLocator
{
    /// 返回 manifest 绝对路径；找不到时抛 NativeImportException(E0916)
    string Locate(string libName, string? sourceDir);
}
```

默认实现 `DefaultNativeManifestLocator`：
1. 若 `sourceDir != null`：检查 `<sourceDir>/<libName>.z42abi`
2. 检查每条 `Z42_NATIVE_LIBS_PATH`（`:` 分隔）下的 `<libName>.z42abi`
3. 都找不到 → E0916 with 详细搜索路径列表

测试用 `InMemoryManifestLocator(Dictionary<string, string>)` 注入预先准备好的 manifest 文本路径，不走文件系统。

### Decision 3: Manifest 签名解析器白名单

支持的 manifest type strings（C11b 范围）：

| Manifest 形式 | z42 TypeExpr | 备注 |
|---|---|---|
| `void` | `VoidType` | |
| `i8` / `i16` / `i32` / `i64` | `NamedType("i8"/...)` | 直接用显式宽度别名 |
| `u8` / `u16` / `u32` / `u64` | `NamedType("u8"/...)` | |
| `f32` / `f64` | `NamedType("f32"/"f64")` | |
| `bool` | `NamedType("bool")` | |
| `Self` | `NamedType(<typeEntry.Name>)` | 仅出现在 `ret` |
| `*mut Self` / `*const Self` | （receiver — 移出 params） | 仅出现在 first param |
| **其他**（`*const c_char` / `String` / `Box<T>` / 用户类型 / ...） | **报 E0916** | 留 C11e 扩展 |

签名解析器是个**纯字符串 → AST 的小递归下降**，~50 行；绝不引用 z42 自己的 TypeParser（避免循环依赖）。

### Decision 4: 合成 ClassDecl 的形状

```csharp
ClassDecl {
    Name             = typeEntry.Name,
    IsStruct         = false,    // C11b 一律走 reference type
    IsRecord         = false,
    IsAbstract       = false,
    IsSealed         = true,     // C11b 不允许继承 native type
    Visibility       = Internal,
    BaseClass        = null,
    Interfaces       = [],
    Fields           = [],       // B1 — 无脚本可见字段
    Methods          = [ <为每个 MethodEntry 合成的 FunctionDecl> ],
    Span             = ni.Span,  // import 语句的 Span，错误指回用户代码
    TypeParams       = null,
    Where            = null,
    ClassNativeDefaults = new Tier1NativeBinding(
        Lib      = manifest.LibraryName,
        TypeName = typeEntry.Name,
        Entry    = null  // 方法级补全
    ),
}
```

每个 `MethodEntry` 合成的 `FunctionDecl`：

```csharp
FunctionDecl {
    Name         = methodEntry.Name,
    Params       = <signature.Params 翻译，剔除 *mut Self / *const Self 接收者>,
    ReturnType   = <signature.Ret 翻译>,
    Body         = <empty BlockStmt>,    // extern, no body
    Visibility   = Public,
    Modifiers    = Extern | (Static if kind=="static" else None),
    NativeIntrinsic = null,
    Tier1Binding = new Tier1NativeBinding(
        Lib      = null,   // 走类级 default
        TypeName = null,   // 走类级 default
        Entry    = methodEntry.Symbol
    ),
    Span = ni.Span,
}
```

构造函数（kind="ctor"）特殊：
- `Name = typeEntry.Name`（z42 ctor 命名约定）
- `ReturnType = VoidType`（z42 ctor 不显式声明 return type）
- `Tier1Binding.Entry = methodEntry.Symbol`
- TypeChecker 既有 ctor 检测（`returnType is VoidType && nameMatchesClass`）会自然识别

> Tier1 binding 留 method-level partial（仅 Entry 非空），由 IrGen 既有的 stitching 逻辑（C9 落地）补齐 Lib + TypeName。**不动 IrGen**。

### Decision 5: 错误路径

| 错误 | Code | 触发 |
|---|---|---|
| manifest 文件找不到 / IO 失败 / JSON 失败 / abi_version / 必需字段 | E0909（C11a 已启用，复用） | `NativeManifest.Read` |
| import 的 type name 在 manifest types[] 中找不到 | **E0916** | Synthesizer |
| 签名串无法解析（白名单外的类型） | **E0916** | ManifestSignatureParser |
| 同 import name 在两条 `import` 中、lib 不一致 | **E0916** | Synthesizer pre-scan |
| 同 import name 但 lib 一致 | warning（重复声明，不报错）| 后续 spec 决定要不要 |

E0916 错误信息总是包含：(a) 失败的类型名 / 签名串；(b) 出问题的 manifest 路径；(c) 用户 import 语句的 Span。

## Implementation Notes

### 接入 Pipeline

`PackageCompiler` 大致流程是：`Source files → Parser → TypeChecker → IrGen → ZpkgWriter`。在 `Parser → TypeChecker` 之间加一个 fixed pass（无 phase 概念）：

```csharp
foreach (var (path, src) in sources)
{
    var cu = new Parser(...).ParseCompilationUnit();
    NativeImportSynthesizer.Run(cu, locator, sourceDir: Path.GetDirectoryName(path));  // ← 新插入
    typeChecker.Add(cu);
}
```

具体 hook 点在 `PackageCompiler.cs` 的 source-file 循环中（实施时再核准确切方法名）。

### 测试中的 locator 注入

```csharp
var locator = new InMemoryManifestLocator(new()
{
    ["numz42"] = "/* manifest JSON 字符串 */",
});
NativeImportSynthesizer.Run(cu, locator, sourceDir: null);
```

不写真实文件即可端到端测合成器。

### 合成产物的 Span

所有合成节点共享 `ni.Span`（`import` 语句的 token span）。这样 TypeChecker / IrGen 报错时——比如用户调了一个不存在的方法——错误能指回用户代码的 `import` 行。

### IrGen 怎么吃合成结果

零改动。合成出的 `ClassDecl.ClassNativeDefaults` + `FunctionDecl.Tier1Binding` 完全对应 C9 已有的"class-level + method-level partial"模式；C9 的 stitching 就把它们拼成完整 `Tier1NativeBinding(Lib, TypeName, Entry)` 走 `EmitNativeStub` → `CallNativeInstr`。

### Self 类型的 receiver 是什么

C11b 不在 z42 端给合成类增加 `__handle` 字段——构造函数的返回值（`*mut Self` IntPtr，由 native ctor 给出）就是 z42 端 `var c = new Counter()` 里 `c` 的 Value。后续方法调用 `c.inc()` 时，IR 既有的"实例方法调用 = 把 receiver 作为第一参数 + 走 Tier1 dispatch"流程把 c 的 Value 通过 libffi 传回 native——这个机制 C2–C10 已经验证可行（`counter_inc(*mut Counter)` 当前就是这么 dispatch 的）。

如果实施时发现 IR / VM 在传 receiver 上有边角问题（比如 Value 类型不对），那是 C2–C10 留下的 bug，**不归 C11b 解决**——记录到 backlog 单独 spec 修。

## Testing

| Test | 内容 |
|---|---|
| `Sig_Parses_Primitives` | `i64` / `f64` / `bool` / `void` → 对应 NamedType / VoidType |
| `Sig_Parses_Self_And_Pointer_Forms` | `Self` / `*mut Self` / `*const Self` 解析 |
| `Sig_Rejects_Unsupported_Types_E0916` | `*const c_char` / `String` / `Box<T>` 报 E0916 |
| `Synth_Single_Import_Generates_ClassDecl` | numz42 manifest with Counter（inc/get/alloc）→ 合成 ClassDecl 含 3 方法 + ClassNativeDefaults |
| `Synth_Method_Tier1_Entry_Wired` | 合成方法的 `Tier1Binding.Entry == methodEntry.Symbol` |
| `Synth_Ctor_Recognized_As_Constructor` | kind="ctor" → FunctionDecl 名匹配类名 + ReturnType=VoidType |
| `Synth_Static_Method_HasStaticModifier` | kind="static" → Modifiers 含 Static |
| `Synth_Type_Not_In_Manifest_E0916` | import Foo 但 manifest 无 Foo type |
| `Synth_Manifest_Not_Found_E0909` | locator 找不到 manifest |
| `Synth_Conflicting_Imports_Same_Name_Different_Lib_E0916` | 两条 import 同 name 不同 lib |
| `Synth_Multiple_Imports_All_Synthesized_In_Order` | 多个 import → 多个 ClassDecl 顺序保留 |
| `Synth_Empty_NativeImports_NoOp` | `cu.NativeImports == null` 时 pass 不动 cu.Classes |

## Risk

- **风险**：合成 ClassDecl 的 ctor 命名约定与 z42 既有约定不完全匹配 → 实施时遇到 TypeChecker 拒识别再调 spec
- **风险**：实例方法调用时 receiver Value 类型不对（C2–C10 留下的边角）→ 退路：把 hidden `__handle` 字段加回，但这就推翻 B1 简化承诺；先严格 B1，必要时升级 spec
- **回滚**：单 commit revert 即可；新文件全在 `z42.Semantics/Synthesis/` 下；pipeline 一行接入也可摘除
