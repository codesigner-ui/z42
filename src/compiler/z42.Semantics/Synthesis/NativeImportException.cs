using Z42.Core.Diagnostics;
using Z42.Core.Text;

namespace Z42.Semantics.Synthesis;

/// <summary>
/// Thrown by <see cref="NativeImportSynthesizer"/> /
/// <see cref="ManifestSignatureParser"/> when an
/// <c>import T from "lib";</c> declaration cannot be turned into a synthesized
/// <see cref="Z42.Syntax.Parser.ClassDecl"/>. Always carries
/// <see cref="DiagnosticCodes.NativeImportSynthesisFailure"/>
/// (<c>E0916</c>) and the source span of the originating <c>import</c>
/// statement so the driver can render the error against user code.
/// </summary>
public sealed class NativeImportException : Exception
{
    public string Code { get; }
    public Span   Span { get; }

    public NativeImportException(string code, string message, Span span)
        : base(message)
    {
        Code = code;
        Span = span;
    }
}
