using System.CommandLine;
using System.CommandLine.Invocation;
using Z42.Pipeline;

namespace Z42.Driver;

static class BuildCommand
{
    // ── Command factories ─────────────────────────────────────────────────────

    public static Command Create()
    {
        var cmd         = new Command("build", "Build a z42 project from a manifest");
        var manifestArg = ManifestArg();
        var releaseOpt  = new Option<bool>("--release", "Build with the release profile");
        var binOpt      = new Option<string?>("--bin", "Build only the named [[exe]] target");

        cmd.AddArgument(manifestArg);
        cmd.AddOption(releaseOpt);
        cmd.AddOption(binOpt);

        cmd.SetHandler((InvocationContext ctx) =>
        {
            var manifest = ctx.ParseResult.GetValueForArgument(manifestArg);
            var release  = ctx.ParseResult.GetValueForOption(releaseOpt);
            var bin      = ctx.ParseResult.GetValueForOption(binOpt);
            ctx.ExitCode = PackageCompiler.Run(manifest, release, bin);
        });

        return cmd;
    }

    public static Command CreateCheck()
    {
        var cmd         = new Command("check", "Type-check a project without emitting artifacts");
        var manifestArg = ManifestArg();
        var binOpt      = new Option<string?>("--bin", "Check only the named [[exe]] target");

        cmd.AddArgument(manifestArg);
        cmd.AddOption(binOpt);

        cmd.SetHandler((InvocationContext ctx) =>
        {
            var manifest = ctx.ParseResult.GetValueForArgument(manifestArg);
            var bin      = ctx.ParseResult.GetValueForOption(binOpt);
            ctx.ExitCode = PackageCompiler.RunCheck(manifest, bin);
        });

        return cmd;
    }

    static Argument<string?> ManifestArg() =>
        new("manifest", () => null, "Path to .z42.toml (auto-discovered if omitted)")
        {
            Arity = ArgumentArity.ZeroOrOne
        };
}
