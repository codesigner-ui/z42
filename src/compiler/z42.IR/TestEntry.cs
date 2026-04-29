namespace Z42.IR;

/// <summary>
/// Spec R1 (add-test-metadata-section) — compile-time test metadata stored in
/// the zbc <c>TIDX</c> section. One <c>TestEntry</c> per function decorated
/// with a <c>z42.test.*</c> attribute.
///
/// Mirrored 1:1 by the Rust runtime side (<c>z42_vm::metadata::TestEntry</c>).
/// Cross-language contract: see <c>src/runtime/tests/zbc_compat.rs</c>.
///
/// All string / type indices are <b>1-based into <see cref="IrModule.StringPool"/></b>.
/// A value of <c>0</c> means "no value" (nullable surface).
/// </summary>
public sealed record TestEntry(
    /// <summary>Index into <see cref="IrModule.Functions"/>.</summary>
    int                    MethodId,
    TestEntryKind          Kind,
    TestFlags              Flags,
    /// <summary>0 = none; otherwise 1-based string-pool index for [Skip(reason)].</summary>
    int                    SkipReasonStrIdx,
    /// <summary>0 = none; otherwise 1-based pool index for [ShouldThrow&lt;E&gt;] (R4 fills).</summary>
    int                    ExpectedThrowTypeIdx,
    /// <summary>Empty for non-parameterized methods; one entry per [TestCase(...)].</summary>
    IReadOnlyList<TestCase> TestCases);

/// <summary>One <c>[TestCase(args)]</c> instance. R1 stores args as a single
/// string-pool index (textual representation); R4 will replace with typed encoding.</summary>
public sealed record TestCase(
    /// <summary>1-based index into <see cref="IrModule.StringPool"/>.</summary>
    int ArgReprStrIdx);

/// <summary>Test-method classification, mirrored from Rust <c>TestEntryKind</c>.
/// Discriminant values are <b>part of the zbc TIDX binary contract</b>.</summary>
public enum TestEntryKind : byte
{
    /// <summary>[Test] — regular test method.</summary>
    Test      = 1,
    /// <summary>[Benchmark] — measurement method (runner uses different scheduler).</summary>
    Benchmark = 2,
    /// <summary>[Setup] — runs before each [Test] in the same module.</summary>
    Setup     = 3,
    /// <summary>[Teardown] — runs after each [Test] in the same module.</summary>
    Teardown  = 4,
    /// <summary>[Doctest] — extracted from <c>///</c> doc comment; reserved (v0.2).</summary>
    Doctest   = 5,
}

/// <summary>Boolean flags on a <see cref="TestEntry"/>. Reserved bits (4-15) must be zero;
/// readers reject set reserved bits to surface forward-compat issues at load time.</summary>
[Flags]
public enum TestFlags : ushort
{
    None        = 0,
    /// <summary>Has [Skip(reason)]. <see cref="TestEntry.SkipReasonStrIdx"/> references the reason.</summary>
    Skipped     = 1 << 0,
    /// <summary>Has [Ignore]. Runner does not list this entry.</summary>
    Ignored     = 1 << 1,
    /// <summary>Has [ShouldThrow&lt;E&gt;]. <see cref="TestEntry.ExpectedThrowTypeIdx"/>
    /// references the expected exception type (reserved for R4).</summary>
    ShouldThrow = 1 << 2,
    /// <summary>Reserved (v0.2 doctest pipeline).</summary>
    Doctest     = 1 << 3,
}
