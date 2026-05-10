using Z42.Core.Text;
using Z42.Core.Features;
using Z42.Semantics.Bound;
using Z42.Syntax.Parser;
using Z42.IR;
using Z42.Semantics.TypeCheck;

namespace Z42.Semantics.Codegen;

public sealed partial class IrGen
{

    private TestEntry BuildTestEntry(int methodId, IReadOnlyList<TestAttribute> attrs)
    {
        var kind         = TestEntryKind.Test;          // default if no primary kind seen
        var flags        = TestFlags.None;
        int reasonIdx    = 0;
        int platformIdx  = 0;
        int featureIdx   = 0;
        int expectedThrowTypeIdx = 0;

        foreach (var attr in attrs)
        {
            switch (attr.Name)
            {
                case "Test":      kind = TestEntryKind.Test;      break;
                case "Benchmark": kind = TestEntryKind.Benchmark; break;
                case "Setup":     kind = TestEntryKind.Setup;     break;
                case "Teardown":  kind = TestEntryKind.Teardown;  break;
                case "Ignore":    flags |= TestFlags.Ignored;     break;
                case "Skip":
                    flags |= TestFlags.Skipped;
                    if (attr.NamedArgs is not null)
                    {
                        if (attr.NamedArgs.TryGetValue("reason", out var reason))
                            reasonIdx = Intern(reason) + 1;     // 1-based
                        if (attr.NamedArgs.TryGetValue("platform", out var platform))
                            platformIdx = Intern(platform) + 1;
                        if (attr.NamedArgs.TryGetValue("feature", out var feature))
                            featureIdx = Intern(feature) + 1;
                    }
                    break;
                case "ShouldThrow":
                    flags |= TestFlags.ShouldThrow;
                    // R4.B — TypeArg null is rejected by TestAttributeValidator
                    // (E0913); guard here defensively for non-validated paths.
                    //
                    // A3 — emit the user-written type plus its ancestor short
                    // names as a `;`-delimited chain ("TestFailure;Exception").
                    // The runner accepts a match against any entry, giving
                    // inheritance-aware ShouldThrow without any TIDX layout
                    // change or cross-module class loading at runtime.
                    if (attr.TypeArg is not null)
                        expectedThrowTypeIdx = Intern(BuildShouldThrowChain(attr.TypeArg)) + 1;
                    break;
            }
        }

        return new TestEntry(
            MethodId:             methodId,
            Kind:                 kind,
            Flags:                flags,
            SkipReasonStrIdx:     reasonIdx,
            SkipPlatformStrIdx:   platformIdx,
            SkipFeatureStrIdx:    featureIdx,
            ExpectedThrowTypeIdx: expectedThrowTypeIdx,          // R4.B
            TestCases:            Array.Empty<TestCase>());     // R4 (TestCase parser pending)
    }

    /// A3 — Build the `;`-delimited expected-throw chain for `[ShouldThrow<E>]`.
    ///
    /// Emits <c>E</c> followed by every visible class whose inheritance chain
    /// passes through <c>E</c> — i.e. <c>E</c> + its descendants. The runner
    /// splits on ';' and matches against any entry, so
    /// <c>[ShouldThrow&lt;Exception&gt;]</c> catching a <c>TestFailure</c> throw
    /// passes because <c>TestFailure</c> appears in the chain (compile-time
    /// inclusion list, not runtime walk).
    ///
    /// Limited to classes visible in this CU's <see cref="SemanticModel.Classes"/>
    /// (which includes imports brought in by <c>using</c> directives). Classes
    /// in non-imported zpkg dependencies are not enumerated; for those the
    /// runner falls back to direct matching.
    private string BuildShouldThrowChain(string typeArg)
    {
        var chain = new List<string> { typeArg };
        if (_semanticModel is null) return typeArg;

        foreach (var (name, cls) in _semanticModel.Classes)
        {
            if (name == typeArg) continue;
            if (IsDescendantOf(cls, typeArg)) chain.Add(name);
        }
        return string.Join(';', chain);
    }

    /// Whether <paramref name="cls"/> derives transitively from
    /// <paramref name="ancestorShortName"/>. Walks <c>BaseClassName</c> chain;
    /// cycle-guarded.
    private bool IsDescendantOf(Z42ClassType cls, string ancestorShortName)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = cls;
        while (current is not null && visited.Add(current.Name))
        {
            if (current.BaseClassName is null) return false;
            var baseFq = current.BaseClassName;
            var baseShort = baseFq.Contains('.')
                ? baseFq[(baseFq.LastIndexOf('.') + 1)..]
                : baseFq;
            if (baseShort == ancestorShortName) return true;
            if (_semanticModel?.Classes.TryGetValue(baseShort, out var next) != true)
                return false;
            current = next;
        }
        return false;
    }
}
