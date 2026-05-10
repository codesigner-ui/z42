# Design: Extend manifest signature whitelist (C11e)

## Architecture

```
C11b whitelist:                     C11e adds (param + return unless noted):
  void                              *const c_char  →  string  (param-only)
  i8/i16/i32/i64                    *mut c_char    →  string  (param-only)
  u8/u16/u32/u64                    *const Other   →  NamedType("Other")
  f32/f64                           *mut Other     →  NamedType("Other")
  bool
  Self
  *mut Self / *const Self (recv)
```

`Other` 必须是当前 `CompilationUnit` 中其他 `import` 的 type 名。Synthesizer 在 sig parser 调用前先扫一遍 `cu.NativeImports`，把所有名字（含正在合成的 type）收成一个 `IReadOnlySet<string>`，下传给 `ManifestSignatureParser`。

## Decisions

### Decision 1: API 形态——附加 `knownNativeTypes` 参数

```csharp
public static class ManifestSignatureParser
{
    public static TypeExpr ParseReturn(
        string sig, string selfTypeName,
        IReadOnlySet<string> knownNativeTypes,  // ← C11e 新增
        Span span);

    public static (bool IsReceiver, TypeExpr? Type) ParseParam(
        string sig, string selfTypeName,
        IReadOnlySet<string> knownNativeTypes,  // ← C11e 新增
        bool firstParam, Span span);
}
```

`knownNativeTypes` 中**始终包含 `selfTypeName`**——这样 `*mut Self` 已被 receiver 分支处理后，剩下场合（如返回 `Self` ）走通用类型路径就不会失败。

### Decision 2: c_char 仅 param 位置接受

理由：
- **c_char param** = z42 `string` 走 C8 marshal arena 把 `Value::Str` 借出为 NUL-terminated `*const c_char`，arena 在 CallNative 退出时释放 CString。**机制就绪、零 IR 改动**。
- **c_char return** 触发的是"谁来释放这个 C 串？"——native 端 `malloc` 给 z42 后 z42 必须负责 free；或 native 端持有静态指针，z42 不能 free。这是 ownership 协议问题，不是签名翻译问题。**留 C11f 单独决定**。

`ParseReturn` 遇到 `*const c_char` / `*mut c_char` 报 E0916，错误信息明确："c_char return value requires ownership protocol; tracked in C11f"。

### Decision 3: `*const T` 与 `*mut T` 等价（除 Self receiver 外）

z42 端无 const 概念。manifest 里的 `*mut` / `*const` 表示 native 内部约束，不影响 z42 类型——除了 Self 在第一参数时 receiver 分支已处理。

### Decision 4: 错误信息分类升级

C11b 的 E0916 把所有"白名单外"打包进同一句。C11e 区分两种：
- **unknown-type**：`*mut Foo` 但 `Foo` 不在 `knownNativeTypes` —— "type `Foo` is not imported in this compilation unit; add `import Foo from \"<lib>\";`"
- **unsupported-shape**：`Box<T>` / `Array<T>` / `[T; 10]` 等结构 —— "manifest type `Box<T>` is not supported by C11e synthesizer (whitelist: primitives, `Self`, `*mut/const Self`, `*const c_char`, `*mut/const <OtherImported>`)"

### Decision 5: Synthesizer 收集 `knownNativeTypes` 的时机

```csharp
// in NativeImportSynthesizer.Run, after conflict pre-scan:
var knownTypes = new HashSet<string>(StringComparer.Ordinal);
foreach (var imp in imports) knownTypes.Add(imp.Name);
// 然后下传给 SynthesizeMethod → ManifestSignatureParser
```

**注意**：`knownTypes` 是 import name 集合，不含手写 z42 class——后者由 TypeChecker 解决。Sig parser 只看 native 类之间的关系。

## Implementation Notes

### c_char 分支位置

```csharp
public static (bool IsReceiver, TypeExpr? Type) ParseParam(
    string sig, string selfTypeName, IReadOnlySet<string> knownNativeTypes,
    bool firstParam, Span span)
{
    var s = sig.Trim();

    if (firstParam && (s == "*mut Self" || s == "*const Self"))
        return (IsReceiver: true, Type: null);

    if (s == "*const c_char" || s == "*mut c_char")
        return (IsReceiver: false, Type: new NamedType("string", span));

    if (TryParsePointerToOther(s, knownNativeTypes, selfTypeName, out var named, span))
        return (IsReceiver: false, Type: named);

    if (s == "Self") return (IsReceiver: false, Type: new NamedType(selfTypeName, span));
    if (s_primitives.Contains(s))
        return (IsReceiver: false, Type: new NamedType(s, span));

    throw Unsupported(sig, span, position: "parameter", knownNativeTypes);
}

private static bool TryParsePointerToOther(
    string s, IReadOnlySet<string> known, string selfName,
    out NamedType? named, Span span)
{
    named = null;
    string? targetName = null;
    if (s.StartsWith("*mut "))   targetName = s["*mut ".Length..].Trim();
    if (s.StartsWith("*const ")) targetName = s["*const ".Length..].Trim();
    if (targetName is null || targetName == "Self") return false;

    if (!known.Contains(targetName))
        throw Unknown(targetName, span);

    named = new NamedType(targetName, span);
    return true;
}
```

### 错误信息构造

```csharp
private static NativeImportException Unknown(string typeName, Span span) => new(
    DiagnosticCodes.NativeImportSynthesisFailure,
    $"manifest references native type `{typeName}` but no matching " +
    $"`import {typeName} from \"...\";` is in scope",
    span);

private static NativeImportException Unsupported(
    string sig, Span span, string position, IReadOnlySet<string> known)
{
    var importedList = known.Count == 0 ? "(none)" : string.Join(", ", known);
    return new(
        DiagnosticCodes.NativeImportSynthesisFailure,
        $"manifest {position} type `{sig}` is not supported by C11e synthesizer " +
        $"(whitelist: primitives, `Self`, `*mut/const Self`, `*const c_char`, " +
        $"`*mut/const <Imported>`; currently-imported native types: {importedList})",
        span);
}
```

### Synthesizer 收集

```csharp
public static void Run(CompilationUnit cu, INativeManifestLocator locator, string? sourceDir)
{
    var imports = cu.NativeImports;
    if (imports is null || imports.Count == 0) return;

    // ... 既有 conflict 预扫描 ...

    var knownTypes = new HashSet<string>(StringComparer.Ordinal);
    foreach (var imp in imports) knownTypes.Add(imp.Name);

    foreach (var imp in imports)
    {
        // ... locate / read manifest / find type ...
        cu.Classes.Add(SynthesizeClass(typeEntry, manifest, imp.Span, knownTypes));
    }
}
```

`SynthesizeClass` / `SynthesizeMethod` / `TranslateSignature` 都加 `knownTypes` 参数串下去。

## Testing

| Test | 内容 |
|---|---|
| `Sig_Parses_CChar_Param_AsString` | `*const c_char` / `*mut c_char` 在 param 位置 → `NamedType("string")` |
| `Sig_Rejects_CChar_Return_E0916` | `*const c_char` 在 ret 位置报 E0916，message 含 "C11f" |
| `Sig_Parses_PointerToOtherImported_Type` | `*mut Regex`，`knownNativeTypes={Regex}` → `NamedType("Regex")` |
| `Sig_Rejects_PointerToUnknownType_E0916` | `*mut Foo`，knownNativeTypes 不含 Foo → E0916 with "import Foo from" hint |
| `Sig_Const_And_Mut_Equivalent_For_Other` | `*mut Regex` 和 `*const Regex` 都解析为同一 NamedType |
| `Synth_Method_With_CCharParam_Compiles` | `import Counter from "numz42"; Counter 含一个 `set_name(name: *const c_char) -> void` method` |
| `Synth_Method_Returning_OtherImportedType` | manifest type `Match` 含 `regex_match(*mut Self) -> *mut Regex` 与 `Regex` 一起 import → 合成成功，返回类型为 `Regex` |
| `Synth_Method_Param_OtherType_NotImported_E0916` | `*mut Regex` 但用户没 `import Regex` → E0916 with hint |

## Risk

- **风险**：c_char-as-string 在 IrGen → CallNative 时实际 marshal 是否真的走 C8 路径？需在测试中端到端跑一遍，否则 spec 通过但运行时 panic
  - 缓解：测试覆盖到 IrGen 阶段（构造 IrModule + 看 emit 出来的 CallNativeInstr 含 SigType::CStr）
- **风险**：用户类型循环引用（A 的方法返回 B，B 的方法返回 A）—— Synthesizer 是按 import 顺序合成 ClassDecl，TypeChecker 后置；NamedType("B") 在合成 A 时还没有 ClassDecl，但 TypeChecker 见到时已就绪。**不阻塞**
- **回滚**：单 commit revert
