using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;
using Z42.Semantics.TypeCheck;

namespace Z42.Tests;

public partial class TypeCheckerTests
{
    // ── Integer literal range checking ────────────────────────────────────────

    [Fact]
    public void I8_LiteralInRange_NoError()
    {
        CheckStmts("i8 x = 127;").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void I8_LiteralMinBound_NoError()
    {
        CheckStmts("i8 x = -128;").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void I8_LiteralOverflow_ReportsError()
    {
        var diags = CheckStmts("i8 x = 128;");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.IntLiteralOutOfRange);
    }

    [Fact]
    public void I8_LiteralUnderflow_ReportsError()
    {
        var diags = CheckStmts("i8 x = -129;");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.IntLiteralOutOfRange);
    }

    [Fact]
    public void U8_LiteralInRange_NoError()
    {
        CheckStmts("u8 x = 255;").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void U8_LiteralOverflow_ReportsError()
    {
        var diags = CheckStmts("u8 x = 256;");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.IntLiteralOutOfRange);
    }

    [Fact]
    public void U8_NegativeLiteral_ReportsError()
    {
        var diags = CheckStmts("u8 x = -1;");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.IntLiteralOutOfRange);
    }

    [Fact]
    public void I16_LiteralInRange_NoError()
    {
        CheckStmts("i16 x = 32767;").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void I16_LiteralOverflow_ReportsError()
    {
        var diags = CheckStmts("i16 x = 32768;");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.IntLiteralOutOfRange);
    }

    [Fact]
    public void U32_LiteralInRange_NoError()
    {
        CheckStmts("u32 x = 4294967295;").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void U32_LiteralOverflow_ReportsError()
    {
        // 4294967296 = uint.MaxValue + 1; stored as long in AST, fits in long but not u32
        var diags = CheckStmts("u32 x = 4294967296;");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.IntLiteralOutOfRange);
    }

    [Fact]
    public void Int_LargePositiveLiteral_TreatedAsLong_RequiresLong()
    {
        // 2147483648 > int.MaxValue → literal typed as Long → int target → TypeMismatch via range check
        var diags = CheckStmts("int x = 2147483648;");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.IntLiteralOutOfRange || d.Code == DiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void I8_AssignLiteralInRange_NoError()
    {
        CheckStmts("i8 x = 0; x = 50;").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void I8_AssignLiteralOverflow_ReportsError()
    {
        var diags = CheckStmts("i8 x = 0; x = 200;");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.IntLiteralOutOfRange);
    }

    // ── Default parameter values ──────────────────────────────────────────────

    [Fact]
    public void DefaultParam_DeclarationAndCall_NoErrors()
    {
        Check("void Greet(string name, string greeting = \"Hello\") {} void Main() { Greet(\"Alice\"); }")
            .HasErrors.Should().BeFalse();
    }

    [Fact]
    public void DefaultParam_ExplicitOverride_NoErrors()
    {
        Check("void Greet(string name, string greeting = \"Hello\") {} void Main() { Greet(\"Alice\", \"Hi\"); }")
            .HasErrors.Should().BeFalse();
    }

    [Fact]
    public void DefaultParam_AllDefaults_NoErrors()
    {
        Check("void Reset(int x = 0, int y = 0) {} void Main() { Reset(); }")
            .HasErrors.Should().BeFalse();
    }

    [Fact]
    public void DefaultParam_MissingRequired_ReportsError()
    {
        Check("void Greet(string name, string greeting = \"Hello\") {} void Main() { Greet(); }")
            .HasErrors.Should().BeTrue();
    }

    [Fact]
    public void DefaultParam_WrongDefaultType_ReportsError()
    {
        Check("void Foo(int n = \"bad\") {}")
            .HasErrors.Should().BeTrue();
    }

    [Fact]
    public void DefaultParam_NullableParam_NoErrors()
    {
        Check("void Foo(int x, string? label = null) {} void Main() { Foo(1); }")
            .HasErrors.Should().BeFalse();
    }

    // ── C# type aliases ───────────────────────────────────────────────────────

    [Fact]
    public void SbyteAlias_LiteralAssignment_NoErrors()
    {
        CheckStmts("sbyte sb = -128;").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void ShortAlias_LiteralAssignment_NoErrors()
    {
        CheckStmts("short sh = 32000;").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void ByteAlias_LiteralAssignment_NoErrors()
    {
        CheckStmts("byte b = 255;").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void FloatLiteral_TypeIsFloat_NoErrors()
    {
        CheckStmts("float f = 1.5f;").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void NullableString_StringAssignment_NoErrors()
    {
        CheckStmts("string? s = \"hello\";").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void NullCoalesce_UnwrapsOptional()
    {
        // string? ?? string → string; assigning to string must succeed
        CheckStmts("string? opt = null; string s = opt ?? \"default\";")
            .HasErrors.Should().BeFalse();
    }

}
