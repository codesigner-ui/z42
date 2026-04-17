using System.Reflection;
using System.Text.Json.Serialization;
using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.IR;
using Xunit;

namespace Z42.Tests;

/// <summary>
/// Ensures that all sealed subtypes of polymorphic base classes are registered
/// via [JsonDerivedType] and that all diagnostic codes have catalog entries.
/// Catches omissions at compile-test time instead of at runtime.
/// </summary>
public class RegistrationTests
{
    // ── IrInstr: every sealed subclass must have a [JsonDerivedType] on the base ──

    [Fact]
    public void AllIrInstrSubclassesAreRegistered()
    {
        var registered = typeof(IrInstr)
            .GetCustomAttributes<JsonDerivedTypeAttribute>()
            .Select(a => a.DerivedType)
            .ToHashSet();

        var allSubclasses = typeof(IrInstr).Assembly
            .GetTypes()
            .Where(t => t.IsSealed && !t.IsAbstract && t.IsSubclassOf(typeof(IrInstr)))
            .ToList();

        allSubclasses.Should().NotBeEmpty("there should be IrInstr subclasses");

        var missing = allSubclasses
            .Where(t => !registered.Contains(t))
            .Select(t => t.Name)
            .ToList();

        missing.Should().BeEmpty(
            $"these IrInstr subclasses are missing [JsonDerivedType] registration on IrInstr: " +
            string.Join(", ", missing));
    }

    // ── IrTerminator: every sealed subclass must have a [JsonDerivedType] on the base ──

    [Fact]
    public void AllIrTerminatorSubclassesAreRegistered()
    {
        var registered = typeof(IrTerminator)
            .GetCustomAttributes<JsonDerivedTypeAttribute>()
            .Select(a => a.DerivedType)
            .ToHashSet();

        var allSubclasses = typeof(IrTerminator).Assembly
            .GetTypes()
            .Where(t => t.IsSealed && !t.IsAbstract && t.IsSubclassOf(typeof(IrTerminator)))
            .ToList();

        allSubclasses.Should().NotBeEmpty("there should be IrTerminator subclasses");

        var missing = allSubclasses
            .Where(t => !registered.Contains(t))
            .Select(t => t.Name)
            .ToList();

        missing.Should().BeEmpty(
            $"these IrTerminator subclasses are missing [JsonDerivedType] registration: " +
            string.Join(", ", missing));
    }

    // ── DiagnosticCodes: every code constant must have a DiagnosticCatalog entry ──

    [Fact]
    public void AllDiagnosticCodesHaveCatalogEntries()
    {
        var codeFields = typeof(DiagnosticCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        codeFields.Should().NotBeEmpty("there should be diagnostic code constants");

        var missing = codeFields
            .Where(code => DiagnosticCatalog.TryGet(code) == null)
            .ToList();

        missing.Should().BeEmpty(
            $"these diagnostic codes have no DiagnosticCatalog entry: " +
            string.Join(", ", missing));
    }
}
