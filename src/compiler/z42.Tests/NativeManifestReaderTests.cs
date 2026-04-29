using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Project;

namespace Z42.Tests;

/// Spec C11a (`manifest-reader-import`) — `NativeManifest.Read`.
/// Each test writes a controlled JSON payload to a temp file then asserts
/// either the deserialized <see cref="ManifestData"/> shape or the
/// <see cref="NativeManifestException"/> diagnostic.
public sealed class NativeManifestReaderTests
{
    private static string ValidManifest => """
        {
          "abi_version": 1,
          "module": "numz42",
          "version": "0.1.0",
          "library_name": "numz42",
          "types": [
            {
              "name": "Counter",
              "size": 8,
              "align": 8,
              "flags": ["sealed"],
              "fields": [],
              "methods": [
                {
                  "name": "inc",
                  "kind": "method",
                  "symbol": "numz42_Counter_inc",
                  "params": [{ "name": "self", "type": "*mut Self" }],
                  "ret": "i64"
                }
              ],
              "trait_impls": []
            }
          ]
        }
        """;

    private static string WriteTemp(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"z42abi-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void Reader_ValidManifest_Parses()
    {
        var path = WriteTemp(ValidManifest);
        try
        {
            var data = NativeManifest.Read(path);
            data.AbiVersion.Should().Be(1);
            data.Module.Should().Be("numz42");
            data.Version.Should().Be("0.1.0");
            data.LibraryName.Should().Be("numz42");
            data.Types.Should().ContainSingle();

            var t = data.Types[0];
            t.Name.Should().Be("Counter");
            t.Size.Should().Be(8);
            t.Methods.Should().ContainSingle();
            t.Methods[0].Name.Should().Be("inc");
            t.Methods[0].Symbol.Should().Be("numz42_Counter_inc");
            t.Methods[0].Ret.Should().Be("i64");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reader_MissingFile_ThrowsE0909()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.z42abi");
        var act  = () => NativeManifest.Read(path);
        var ex   = act.Should().Throw<NativeManifestException>().Which;
        ex.Code.Should().Be(DiagnosticCodes.ManifestParseError);
        ex.Path.Should().Be(path);
        ex.Message.Should().Contain(path);
    }

    [Fact]
    public void Reader_MalformedJson_ThrowsE0909()
    {
        var path = WriteTemp("{ this is not json :: ");
        try
        {
            var act = () => NativeManifest.Read(path);
            act.Should().Throw<NativeManifestException>()
               .Which.Code.Should().Be(DiagnosticCodes.ManifestParseError);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reader_AbiVersionMismatch_ThrowsE0909()
    {
        var bumped = ValidManifest.Replace("\"abi_version\": 1", "\"abi_version\": 2");
        var path   = WriteTemp(bumped);
        try
        {
            var act = () => NativeManifest.Read(path);
            var ex  = act.Should().Throw<NativeManifestException>().Which;
            ex.Code.Should().Be(DiagnosticCodes.ManifestParseError);
            ex.Message.Should().Contain("abi_version");
            ex.Message.Should().Contain("2");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reader_MissingLibraryName_ThrowsE0909()
    {
        var trimmed = ValidManifest.Replace("\"library_name\": \"numz42\",", "");
        var path    = WriteTemp(trimmed);
        try
        {
            var act = () => NativeManifest.Read(path);
            var ex  = act.Should().Throw<NativeManifestException>().Which;
            ex.Code.Should().Be(DiagnosticCodes.ManifestParseError);
            ex.Message.Should().Contain("library_name");
        }
        finally { File.Delete(path); }
    }
}
