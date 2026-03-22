using System.Text.RegularExpressions;
using Xunit;
using Z42.Compiler.Features;

namespace Z42.Tests;

/// <summary>
/// Validates that every <c>[feat:NAME]</c> tag in <c>docs/design/grammar.peg</c> is
/// recognised by <see cref="LanguageFeatures"/>. If a tag is a typo or a new
/// feature is added to the grammar but not to <c>LanguageFeatures.GetByName</c>,
/// this test will fail with the list of unrecognised names.
///
/// This prevents spec/code drift: grammar.peg is the source of truth for
/// feature names; LanguageFeatures must cover all of them.
/// </summary>
public sealed class GrammarSyncTests
{
    private static readonly string GrammarPath = FindGrammarPath();

    private static string FindGrammarPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "docs", "design", "grammar.peg");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..", "docs", "design", "grammar.peg"));
    }

    [Fact]
    public void AllGrammarFeatTagsAreRecognizedByLanguageFeatures()
    {
        Assert.True(File.Exists(GrammarPath),
            $"grammar.peg not found at: {GrammarPath}");

        var content = File.ReadAllText(GrammarPath);
        // Only lowercase names are real feature tags; uppercase like [feat:NAME] are doc examples.
        var tags = Regex.Matches(content, @"\[feat:([a-z][a-z0-9_]*)\]")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        Assert.NotEmpty(tags);   // sanity: grammar must have at least one feat tag

        // Each feat name must appear in LanguageFeatures.KnownFeatureNames.
        var unrecognised = tags
            .Where(name => !LanguageFeatures.KnownFeatureNames.Contains(name))
            .ToList();

        Assert.True(unrecognised.Count == 0,
            $"These [feat:NAME] tags in grammar.peg are not in LanguageFeatures.KnownFeatureNames:\n"
            + string.Join("\n", unrecognised.Select(n => $"  - {n}")));
    }
}
