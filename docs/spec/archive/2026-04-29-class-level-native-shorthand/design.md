# Design: Class-level `[Native]` defaults (C9)

## Architecture

```
Source:
  [Native(lib="numz42", type="Counter")]            ← class-level defaults
  public static class NumZ42 {
      [Native(entry="inc")]                          ← method partial (entry only)
      public static extern long Inc(long ptr);
  }

Parser → AST:
  ClassDecl {
      ClassNativeDefaults = Tier1NativeBinding(Lib="numz42", Type="Counter", Entry=null)
      Methods = [
          FunctionDecl { Tier1Binding = Tier1NativeBinding(Lib=null, Type=null, Entry="inc") }
      ]
  }

TypeChecker / IrGen — stitched at codegen:
  effective = Tier1NativeBinding(
      Lib   = method.Tier1Binding.Lib   ?? class.ClassNativeDefaults.Lib,
      Type  = method.Tier1Binding.Type  ?? class.ClassNativeDefaults.Type,
      Entry = method.Tier1Binding.Entry ?? class.ClassNativeDefaults.Entry,
  )
  // require all three non-null else E0907

IR Codegen:
  CallNativeInstr(dst, effective.Lib, effective.Type, effective.Entry, args)
```

## Decisions

### Decision 1: Make `Tier1NativeBinding` fields nullable

```csharp
public sealed record Tier1NativeBinding(string? Lib, string? TypeName, string? Entry);
```

Trade-off: existing C6 callers assumed non-null. They become `null!`-bang or careful checks. But the alternative (separate Partial type + conversion) doubles surface area.

Document: **after typecheck, IR codegen sees only fully-stitched bindings**. Pre-typecheck consumers must handle null.

### Decision 2: Method-level overrides class-level

```
effective.Lib = method.Lib ?? class.Lib
```

A method specifying a key always wins. This matches C# `[DllImport]` convention.

### Decision 3: Class-level requires no `entry`

Class defaults are `(lib, type)` pair. If user writes `[Native(lib=, type=, entry=)]` at class level (3 keys), we permit (extra entry is ignored or used as fallback). If user writes `[Native(entry=)]` only at class level (no lib/type), it's vacuous → reject (E0907 with "class-level [Native] needs at least lib + type").

Actually simpler: any class-level [Native] must have at least lib AND type. Entry is optional (acts as default for methods that lack entry).

### Decision 4: Stitch at IrGen

The stitch happens in `EmitNativeStub` when we have both class context and method binding. TypeChecker just validates the stitched result is complete.

```csharp
private static IrFunction EmitNativeStub(
    string qualifiedName, int totalParams, int paramOffset,
    string? intrinsicName,
    Tier1NativeBinding? methodTier1,
    Tier1NativeBinding? classDefaults,    // ← new
    bool isVoid)
{
    var effective = StitchTier1(methodTier1, classDefaults);
    ...
}
```

Invariant: by IrGen time, stitch never returns incomplete (TypeChecker guarantees).

## Implementation Notes

### Parser updates

`TryParseNativeAttribute` currently strict on Tier1 (requires all 3 keys). Relax to accept any non-empty subset of `{lib, type, entry}` and let the caller validate.

```csharp
return new NativeAttribute(null, new Tier1NativeBinding(
    Lib: lib,        // string? — nullable now
    TypeName: type,
    Entry: entry));
```

If all three are null → not a valid Tier1 form → fall back to old "skip balanced brackets" path or error.

### Validation rules

| Class-level | Method-level | Outcome |
|-------------|--------------|---------|
| (none) | (lib, type, entry) | C6 path, no class needed |
| (lib, type) | (entry) | stitch → full binding |
| (lib, type) | (lib, type, entry) | method overrides class lib/type → still full |
| (lib, type) | (none) | E0907 "method missing entry" |
| (none) | (entry) | E0907 "method partial without class defaults" |
| (lib only) | (entry) | E0907 "class defaults missing type" |
| (entry only at class) | * | E0907 "class [Native] must have at least lib+type" |

## Testing

| Test | Verifies |
|------|----------|
| `Parse_ClassLevelNative_StashedOnClassDecl` | ClassDecl.ClassNativeDefaults populated when class precedes [Native(lib=, type=)] |
| `Parse_MethodEntryOnly_PopulatesPartialTier1` | FunctionDecl.Tier1Binding = (null, null, "inc") |
| `Codegen_ClassDefaultsStitched` | IR含 CallNativeInstr with full lib/type/entry combined from class + method |
| `Codegen_MethodOverridesClassLib` | method's lib wins over class's |
| `TypeCheck_PartialMethodNoClassDefaults_E0907` | method (entry only) without class defaults reports E0907 |
| `TypeCheck_ClassEntryOnly_E0907` | class with only entry= rejects |
| `Codegen_MethodFullForm_NoStitchNeeded_NoRegression` | C6 form continues working independently of class defaults |
