namespace Z42.Core.Diagnostics;

/// <summary>
/// High-level diagnostic category derived from the code prefix.
///
/// <para>Mirrors the E0nxx grouping documented on <see cref="DiagnosticCodes"/>:
/// E01xx Lexer / E02xx Parser / E03xx FeatureGate / E04xx TypeCheck / E05xx
/// IrGen / E06xx Package / E09xx Native (with E0900 ICE + E0911-15 Test
/// carve-outs) / E10xx Argument binding. Use
/// <see cref="DiagnosticCategories.Of(string)"/> to classify an arbitrary
/// code string (covers both built-in <see cref="DiagnosticCodes"/> and
/// externally registered codes via <see cref="DiagnosticCatalog"/>).</para>
///
/// <para>Use cases: <c>z42c explain --category=parser</c> to list all parser
/// errors; IDE error-pane grouping; analyzers filtering by category.
/// docs/review.md Part 6 F5 #2 (2026-05-25).</para>
/// </summary>
public enum DiagnosticCategory
{
    Unknown      = 0,
    Lexer        = 1,   // E01xx
    Parser       = 2,   // E02xx
    FeatureGate  = 3,   // E03xx
    TypeCheck    = 4,   // E04xx
    IrGen        = 5,   // E05xx
    Package      = 6,   // E06xx / W06xx
    InternalCompilerError = 9,  // E0900
    Native       = 10,  // E09xx (except E0900 / E0911-15)
    Test         = 11,  // E0911-E0915
    ArgumentBind = 12,  // E10xx
    Workspace    = 20,  // WS### (external registration)
    User         = 30,  // Z### (external registration)
}

/// <summary>
/// Static classification helper for diagnostic codes.
/// </summary>
public static class DiagnosticCategories
{
    /// <summary>
    /// Classify a diagnostic code string into a category. Returns
    /// <see cref="DiagnosticCategory.Unknown"/> for malformed / unrecognized codes.
    ///
    /// <para>Recognized shapes:</para>
    /// <list type="bullet">
    ///   <item><c>E0nxx</c> / <c>W0nxx</c> — built-in code (n = bucket digit)</item>
    ///   <item><c>E10xx</c> — argument-binding (E1001..E1005)</item>
    ///   <item><c>WS###</c> — external workspace registration</item>
    ///   <item><c>Z###</c> — external user-defined</item>
    /// </list>
    /// </summary>
    public static DiagnosticCategory Of(string code)
    {
        if (string.IsNullOrEmpty(code) || code.Length < 3) return DiagnosticCategory.Unknown;

        // External prefixes
        if (code.StartsWith("WS", StringComparison.Ordinal)) return DiagnosticCategory.Workspace;
        if (code[0] == 'Z')                                   return DiagnosticCategory.User;

        if (code[0] is not ('E' or 'W')) return DiagnosticCategory.Unknown;

        // E10xx — argument binding (E1001..E1005)
        if (code.Length >= 4 && code[1] == '1' && code[2] == '0')
            return DiagnosticCategory.ArgumentBind;

        // [EW]0nxx — bucket on the n digit
        if (code[1] != '0' || !char.IsDigit(code[2])) return DiagnosticCategory.Unknown;

        // Special single-code carve-outs inside the E09xx range.
        if (code == DiagnosticCodes.InternalCompilerError) return DiagnosticCategory.InternalCompilerError;
        if (code is "E0911" or "E0912" or "E0913" or "E0914" or "E0915")
            return DiagnosticCategory.Test;

        return code[2] switch
        {
            '1' => DiagnosticCategory.Lexer,
            '2' => DiagnosticCategory.Parser,
            '3' => DiagnosticCategory.FeatureGate,
            '4' => DiagnosticCategory.TypeCheck,
            '5' => DiagnosticCategory.IrGen,
            '6' => DiagnosticCategory.Package,
            '9' => DiagnosticCategory.Native,
            _   => DiagnosticCategory.Unknown,
        };
    }
}
