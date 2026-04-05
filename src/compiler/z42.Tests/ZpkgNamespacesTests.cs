using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Xunit;
using Z42.Project;

namespace Z42.Tests;

/// <summary>
/// Tests that ZpkgFile correctly serializes and deserializes the namespaces field
/// for both indexed and packed modes.
/// </summary>
public class ZpkgNamespacesTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters             = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Fact]
    public void ZpkgFile_IndexedMode_NamespacesPopulated()
    {
        // Arrange: build a ZpkgFile as BuildTarget would in indexed mode
        var namespaces = new List<string> { "Demo.Greet", "Demo.Math" };
        var exports    = new List<ZpkgExport>
        {
            new ZpkgExport("Demo.Greet.greet", "func"),
            new ZpkgExport("Demo.Math.add",    "func"),
        };
        var files = new List<ZpkgFileEntry>
        {
            new ZpkgFileEntry("src/greet.z42", ".cache/src/greet.zbc", "sha256:abc", ["Demo.Greet.greet"]),
            new ZpkgFileEntry("src/math.z42",  ".cache/src/math.zbc",  "sha256:def", ["Demo.Math.add"]),
        };

        var zpkg = new ZpkgFile(
            Name:         "mylib",
            Version:      "0.1.0",
            Kind:         ZpkgKind.Lib,
            Mode:         ZpkgMode.Indexed,
            Namespaces:   namespaces,
            Exports:      exports,
            Dependencies: [],
            Files:        files,
            Modules:      [],
            Entry:        null
        );

        // Act: round-trip through JSON
        var json        = JsonSerializer.Serialize(zpkg, JsonOpts);
        var deserialized = JsonSerializer.Deserialize<ZpkgFile>(json, JsonOpts);

        // Assert: namespaces field survives serialization round-trip
        deserialized.Should().NotBeNull();
        deserialized!.Namespaces.Should().BeEquivalentTo(namespaces);
        deserialized.Mode.Should().Be(ZpkgMode.Indexed);
        deserialized.Namespaces.Should().Contain("Demo.Greet");
        deserialized.Namespaces.Should().Contain("Demo.Math");
        deserialized.Namespaces.Should().HaveCount(2);
    }

    [Fact]
    public void ZpkgFile_PackedMode_NamespacesPopulated()
    {
        // Arrange: build a ZpkgFile as BuildTarget would in packed mode
        var namespaces = new List<string> { "App.Core", "App.Utils" };
        var exports    = new List<ZpkgExport>
        {
            new ZpkgExport("App.Core.main",       "func"),
            new ZpkgExport("App.Utils.formatDate", "func"),
        };

        var zpkg = new ZpkgFile(
            Name:         "myapp",
            Version:      "1.0.0",
            Kind:         ZpkgKind.Exe,
            Mode:         ZpkgMode.Packed,
            Namespaces:   namespaces,
            Exports:      exports,
            Dependencies: [],
            Files:        [],
            Modules:      [],  // no actual ZbcFiles needed for this test
            Entry:        "App.Core.main"
        );

        // Act: round-trip through JSON
        var json         = JsonSerializer.Serialize(zpkg, JsonOpts);
        var deserialized = JsonSerializer.Deserialize<ZpkgFile>(json, JsonOpts);

        // Assert: namespaces field survives serialization round-trip
        deserialized.Should().NotBeNull();
        deserialized!.Namespaces.Should().BeEquivalentTo(namespaces);
        deserialized.Mode.Should().Be(ZpkgMode.Packed);
        deserialized.Namespaces.Should().Contain("App.Core");
        deserialized.Namespaces.Should().Contain("App.Utils");
        deserialized.Namespaces.Should().HaveCount(2);
        deserialized.Entry.Should().Be("App.Core.main");
    }
}
