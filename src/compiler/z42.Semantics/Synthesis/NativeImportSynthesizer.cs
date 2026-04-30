using Z42.Core.Diagnostics;
using Z42.Core.Text;
using Z42.Project;
using Z42.Syntax.Parser;

namespace Z42.Semantics.Synthesis;

/// <summary>
/// Spec C11b (Path B1) — translates each <c>NativeTypeImport</c> in
/// <see cref="CompilationUnit.NativeImports"/> into a synthesized
/// <see cref="ClassDecl"/> appended to <see cref="CompilationUnit.Classes"/>.
///
/// VM zero-change: synthesized classes carry
/// <see cref="Tier1NativeBinding"/> on the class (lib + type) and on each
/// method (entry symbol). C9 stitching + C6 codegen pick it up unchanged.
///
/// Errors:
///   • <see cref="DiagnosticCodes.ManifestParseError"/> (E0909) bubbles up
///     from <see cref="NativeManifest.Read"/>.
///   • <see cref="DiagnosticCodes.NativeImportSynthesisFailure"/> (E0916)
///     is raised for: type-not-in-manifest, unsupported signature, and
///     conflicting same-name imports targeting different libraries.
/// </summary>
public static class NativeImportSynthesizer
{
    /// <summary>
    /// Mutates <paramref name="cu"/> by appending synthesized class
    /// declarations. No-op when <c>NativeImports</c> is empty / null.
    /// </summary>
    /// <param name="cu">Compilation unit produced by the parser.</param>
    /// <param name="locator">Resolves a <c>libName</c> to a manifest path.</param>
    /// <param name="sourceDir">Directory of the source file (used by
    /// <see cref="DefaultNativeManifestLocator"/>); may be null.</param>
    public static void Run(
        CompilationUnit cu,
        INativeManifestLocator locator,
        string? sourceDir)
    {
        var imports = cu.NativeImports;
        if (imports is null || imports.Count == 0)
            return;

        // Conflict pre-scan: same name + different lib → E0916.
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var imp in imports)
        {
            if (seen.TryGetValue(imp.Name, out var prevLib))
            {
                if (!string.Equals(prevLib, imp.LibName, StringComparison.Ordinal))
                    throw new NativeImportException(
                        DiagnosticCodes.NativeImportSynthesisFailure,
                        $"native import name `{imp.Name}` declared from both " +
                        $"`{prevLib}` and `{imp.LibName}` — type-name conflict",
                        imp.Span);
            }
            else
            {
                seen[imp.Name] = imp.LibName;
            }
        }

        // Cache manifest reads so multiple imports of the same lib hit disk once.
        var manifestCache = new Dictionary<string, ManifestData>(StringComparer.Ordinal);
        var alreadySynthesized = new HashSet<string>(StringComparer.Ordinal);

        foreach (var imp in imports)
        {
            // Same-lib duplicate import of same name: skip (already covered).
            if (!alreadySynthesized.Add(imp.Name))
                continue;

            if (!manifestCache.TryGetValue(imp.LibName, out var manifest))
            {
                var path = locator.Locate(imp.LibName, sourceDir, imp.Span);
                manifest = NativeManifest.Read(path);
                manifestCache[imp.LibName] = manifest;
            }

            var typeEntry = manifest.Types.FirstOrDefault(t => t.Name == imp.Name);
            if (typeEntry is null)
                throw new NativeImportException(
                    DiagnosticCodes.NativeImportSynthesisFailure,
                    $"manifest for `{imp.LibName}` does not declare type `{imp.Name}`",
                    imp.Span);

            cu.Classes.Add(SynthesizeClass(typeEntry, manifest, imp.Span));
        }
    }

    private static ClassDecl SynthesizeClass(TypeEntry type, ManifestData manifest, Span span)
    {
        var methods = new List<FunctionDecl>(type.Methods.Count);
        foreach (var m in type.Methods)
            methods.Add(SynthesizeMethod(m, type.Name, span));

        var classDefaults = new Tier1NativeBinding(
            Lib:      manifest.LibraryName,
            TypeName: type.Name,
            Entry:    null);

        return new ClassDecl(
            Name:       type.Name,
            IsStruct:   false,
            IsRecord:   false,
            IsAbstract: false,
            IsSealed:   true,
            Visibility: Visibility.Internal,
            BaseClass:  null,
            Interfaces: new List<TypeExpr>(),
            Fields:     new List<FieldDecl>(),
            Methods:    methods,
            Span:       span,
            TypeParams: null,
            Where:      null,
            ClassNativeDefaults: classDefaults);
    }

    private static FunctionDecl SynthesizeMethod(MethodEntry m, string selfTypeName, Span span)
    {
        var (parms, returnType) = TranslateSignature(m, selfTypeName, span);

        var modifiers = FunctionModifiers.Extern;
        if (m.Kind == "static")
            modifiers |= FunctionModifiers.Static;

        // Constructor convention: name == enclosing class name, void return type.
        var isCtor = m.Kind == "ctor";
        var name   = isCtor ? selfTypeName : m.Name;
        var ret    = isCtor ? new VoidType(span) : returnType;

        var binding = new Tier1NativeBinding(
            Lib:      null,
            TypeName: null,
            Entry:    m.Symbol);

        return new FunctionDecl(
            Name:            name,
            Params:          parms,
            ReturnType:      ret,
            Body:            new BlockStmt(new List<Stmt>(), span),
            Visibility:      Visibility.Public,
            Modifiers:       modifiers,
            NativeIntrinsic: null,
            Span:            span,
            BaseCtorArgs:    null,
            TypeParams:      null,
            Where:           null,
            Tier1Binding:    binding,
            TestAttributes:  null);
    }

    private static (List<Param> Params, TypeExpr ReturnType) TranslateSignature(
        MethodEntry m, string selfTypeName, Span span)
    {
        var ret  = ManifestSignatureParser.ParseReturn(m.Ret, selfTypeName, span);
        var outs = new List<Param>(m.Params.Count);

        for (int i = 0; i < m.Params.Count; i++)
        {
            var p = m.Params[i];
            var (isReceiver, type) = ManifestSignatureParser.ParseParam(
                p.Type, selfTypeName, firstParam: i == 0, span);
            if (isReceiver)
                continue;
            outs.Add(new Param(p.Name, type!, Default: null, Span: span));
        }

        // method (instance, non-ctor, non-static) without a receiver param ⇒ malformed.
        if (m.Kind == "method" && (m.Params.Count == 0 ||
            !IsReceiverShape(m.Params[0].Type)))
        {
            throw new NativeImportException(
                DiagnosticCodes.NativeImportSynthesisFailure,
                $"manifest method `{selfTypeName}.{m.Name}` (kind=\"method\") " +
                "must have `*mut Self` or `*const Self` as its first parameter",
                span);
        }

        return (outs, ret);
    }

    private static bool IsReceiverShape(string sig)
    {
        var s = sig.Trim();
        return s == "*mut Self" || s == "*const Self";
    }
}
