using System.Reflection;
using FluentAssertions;
using Z42.Core.Diagnostics;
using Xunit;

namespace Z42.Tests;

/// <summary>
/// Ensures that all diagnostic codes have catalog entries.
/// Catches omissions at compile-test time instead of at runtime.
/// </summary>
public class RegistrationTests
{
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
