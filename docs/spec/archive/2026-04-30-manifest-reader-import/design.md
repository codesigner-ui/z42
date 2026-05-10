# Design: `import T from "lib"` + manifest reader (C11a)

## Architecture

```
Source: import Counter from "numz42";

Lexer:
  Import + Identifier("Counter") + Identifier("from") + StringLiteral("\"numz42\"") + Semi

Parser → AST:
  CompilationUnit.NativeImports.Add(
      NativeTypeImport(Name="Counter", LibName="numz42", span))

Z42.Project.NativeManifest:
  static ManifestData Read(string path)
      throws NativeManifestException(E0909, msg, path) on:
          - file not found / unreadable
          - JSON parse error
          - missing required fields (abi_version, module, version, library_name, types)
          - abi_version != 1

ManifestData {
    int   AbiVersion       (== 1)
    string Module           ("numz42")
    string Version          (semver)
    string LibraryName     ("numz42")
    List<TypeEntry> Types
}

TypeEntry {
    string Name
    long   Size
    long   Align
    List<string> Flags
    List<FieldEntry> Fields
    List<MethodEntry> Methods
    List<TraitImplEntry> TraitImpls
}
```

C11b 后续把 NativeImports 转化为合成 ClassDecl，本 spec 仅提供数据通路。

## Decisions

### Decision 1: `import` 是新 Phase1 关键字；`from` 走 contextual identifier

`import` 太通用，必须给关键字身份；否则普通函数名 `import` 会冲突。`from` 只在 `import IDENT` 之后出现一次，contextual 识别足够。

Parser 流程：
```csharp
[TokenKind.Import] = new(ParseImport),

private static ParseResult<...> ParseImport(cursor, kw, feat) {
    var name = ExpectKind(ref cursor, TokenKind.Identifier).Text;
    var fromTok = ExpectKind(ref cursor, TokenKind.Identifier);
    if (fromTok.Text != "from")
        throw new ParseException("expected `from` after import name", fromTok.Span,
            DiagnosticCodes.UnexpectedToken);
    var libLit = ExpectKind(ref cursor, TokenKind.StringLiteral).Text;
    var libName = libLit.Length >= 2 ? libLit[1..^1] : libLit;
    ExpectKind(ref cursor, TokenKind.Semicolon);
    // record on CompilationUnit; for now just accumulate into a list passed by caller
}
```

Top-level integration: `import` 与 `using` / `namespace` 同级，在 ParseCompilationUnit 主循环中识别（作为 stmt-like top-level item）。

### Decision 2: AST shape

```csharp
public sealed record NativeTypeImport(
    string Name,
    string LibName,
    Span Span);

// CompilationUnit gains:
public sealed record CompilationUnit(
    string? Namespace,
    List<string> Usings,
    List<ClassDecl> Classes,
    List<FunctionDecl> Functions,
    List<EnumDecl> Enums,
    List<InterfaceDecl> Interfaces,
    List<ImplDecl> Impls,
    Span Span,
    List<NativeTypeImport>? NativeImports = null);  // ← new (default null preserves existing constructors)
```

### Decision 3: Manifest reader uses System.Text.Json

```csharp
public sealed class ManifestData {
    public int AbiVersion { get; init; }
    public string Module { get; init; } = "";
    public string Version { get; init; } = "";
    public string LibraryName { get; init; } = "";
    public List<TypeEntry> Types { get; init; } = new();
}

public sealed record TypeEntry(
    string Name, long Size, long Align,
    List<string> Flags,
    List<FieldEntry> Fields,
    List<MethodEntry> Methods,
    List<TraitImplEntry> TraitImpls);
// ... FieldEntry / MethodEntry / TraitImplEntry / ParamEntry mirror schema
```

System.Text.Json 已是 .NET BCL 的一部分；不引入新依赖。属性命名用 `JsonPropertyName` 标注（snake_case ↔ PascalCase）。

### Decision 4: Path resolution (deferred)

C11a 不引入复杂路径解析；`NativeManifest.Read(absolutePath)` 接绝对路径，调用方负责定位。C11b 接到 import 流程时再加 `Z42_NATIVE_LIBS_PATH` 搜索。

### Decision 5: Validation depth

C11a 只做最小校验：
- 文件存在 + 可读 + JSON 合法
- `abi_version == 1` 否则 E0909
- 必需字段（module / library_name / types）存在

完整 JSON Schema 校验（数组元素、字段类型）留给 build infra（manifest 生成方负责正确性）。

## Implementation Notes

### Test fixture manifest

测试期间需要一份固定 manifest 文件。最简单：在 `src/compiler/z42.Tests/Fixtures/numz42-manifest.json` 放一份固定 JSON（与 docs/design/manifest-schema.json 例子对齐）。每个 reader 测试用 absolute path 访问。

### Empty NativeImports 默认

`NativeImports = null` vs `NativeImports = []`：用 nullable 默认 null，访问处用 `?? []` fallback，保持与现有 `TypeParams` 等字段同模式。

## Testing

| Test | Verifies |
|------|----------|
| `Lexer_ImportKeyword_Tokenizes` | `import` → TokenKind.Import |
| `Parser_ImportFromString_BasicForm` | `import Counter from "numz42";` → NativeTypeImport |
| `Parser_ImportMissingFrom_Error` | `import Counter "numz42";`（缺 from）→ ParseException |
| `Parser_ImportMissingSemi_Error` | 无 `;` → ParseException |
| `Parser_MultipleImports_All_Captured` | 多个 import → CompilationUnit.NativeImports 含全部 |
| `Reader_ValidManifest_Parses` | example-manifest.json → ManifestData with module/types correctly |
| `Reader_MissingFile_E0909` | 不存在的 path → ManifestException |
| `Reader_MalformedJson_E0909` | 损坏 JSON → ManifestException |
| `Reader_AbiVersionMismatch_E0909` | abi_version=2 → ManifestException |

## Risk

- **风险**：`from` contextual 识别失败若用户写错语法可能给出难懂错误
  - 缓解：parser 错误信息明确指出 "expected `from`"
- **回滚**：单 commit revert 即可；NativeImports 默认 null 不影响现有路径
