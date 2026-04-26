using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using Z42.Pipeline;
using Z42.Project;

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
        var packagesOpt = new Option<string[]>(["-p", "--package"], "Build only the named workspace member(s)") { AllowMultipleArgumentsPerToken = true };
        var workspaceOpt = new Option<bool>("--workspace", "Build all members of the workspace");
        var excludeOpt  = new Option<string[]>("--exclude", "Exclude the named workspace member(s)") { AllowMultipleArgumentsPerToken = true };
        var noWorkspaceOpt = new Option<bool>("--no-workspace", "Force standalone mode, ignoring workspace");

        cmd.AddArgument(manifestArg);
        cmd.AddOption(releaseOpt);
        cmd.AddOption(binOpt);
        cmd.AddOption(packagesOpt);
        cmd.AddOption(workspaceOpt);
        cmd.AddOption(excludeOpt);
        cmd.AddOption(noWorkspaceOpt);

        cmd.SetHandler((InvocationContext ctx) =>
        {
            var manifest = ctx.ParseResult.GetValueForArgument(manifestArg);
            var release  = ctx.ParseResult.GetValueForOption(releaseOpt);
            var bin      = ctx.ParseResult.GetValueForOption(binOpt);
            var packages = ctx.ParseResult.GetValueForOption(packagesOpt) ?? [];
            var workspace = ctx.ParseResult.GetValueForOption(workspaceOpt);
            var exclude  = ctx.ParseResult.GetValueForOption(excludeOpt) ?? [];
            var noWs     = ctx.ParseResult.GetValueForOption(noWorkspaceOpt);

            // Workspace 模式判断（C4a）：显式 path / --no-workspace → 走单工程
            ctx.ExitCode = TryRunWorkspace(release, packages, workspace, exclude, noWs, manifest, checkOnly: false)
                            ?? PackageCompiler.Run(manifest, release, bin);
        });

        return cmd;
    }

    public static Command CreateCheck()
    {
        var cmd         = new Command("check", "Type-check a project without emitting artifacts");
        var manifestArg = ManifestArg();
        var binOpt      = new Option<string?>("--bin", "Check only the named [[exe]] target");
        var packagesOpt = new Option<string[]>(["-p", "--package"], "Check only the named workspace member(s)") { AllowMultipleArgumentsPerToken = true };
        var workspaceOpt = new Option<bool>("--workspace", "Check all workspace members");
        var excludeOpt  = new Option<string[]>("--exclude", "Exclude the named workspace member(s)") { AllowMultipleArgumentsPerToken = true };
        var noWorkspaceOpt = new Option<bool>("--no-workspace", "Force standalone mode");

        cmd.AddArgument(manifestArg);
        cmd.AddOption(binOpt);
        cmd.AddOption(packagesOpt);
        cmd.AddOption(workspaceOpt);
        cmd.AddOption(excludeOpt);
        cmd.AddOption(noWorkspaceOpt);

        cmd.SetHandler((InvocationContext ctx) =>
        {
            var manifest = ctx.ParseResult.GetValueForArgument(manifestArg);
            var bin      = ctx.ParseResult.GetValueForOption(binOpt);
            var packages = ctx.ParseResult.GetValueForOption(packagesOpt) ?? [];
            var workspace = ctx.ParseResult.GetValueForOption(workspaceOpt);
            var exclude  = ctx.ParseResult.GetValueForOption(excludeOpt) ?? [];
            var noWs     = ctx.ParseResult.GetValueForOption(noWorkspaceOpt);

            ctx.ExitCode = TryRunWorkspace(release: false, packages, workspace, exclude, noWs, manifest, checkOnly: true)
                            ?? PackageCompiler.RunCheck(manifest, bin);
        });

        return cmd;
    }

    public static Command CreateRun()
    {
        var cmd         = new Command("run", "Build and execute a z42 project");
        var manifestArg = ManifestArg();
        var releaseOpt  = new Option<bool>("--release", "Build and run with the release profile");
        var binOpt      = new Option<string?>("--bin", "Run the named [[exe]] target");
        var modeOpt     = new Option<string>("--mode", () => "interp", "VM execution mode: interp | jit | aot");

        cmd.AddArgument(manifestArg);
        cmd.AddOption(releaseOpt);
        cmd.AddOption(binOpt);
        cmd.AddOption(modeOpt);

        cmd.SetHandler((InvocationContext ctx) =>
        {
            var manifestPath = ctx.ParseResult.GetValueForArgument(manifestArg);
            var release      = ctx.ParseResult.GetValueForOption(releaseOpt);
            var bin          = ctx.ParseResult.GetValueForOption(binOpt);
            var mode         = ctx.ParseResult.GetValueForOption(modeOpt) ?? "interp";
            ctx.ExitCode = RunProject(manifestPath, release, bin, mode);
        });

        return cmd;
    }

    public static Command CreateClean()
    {
        var cmd         = new Command("clean", "Remove build artifacts for a z42 project");
        var manifestArg = ManifestArg();

        cmd.AddArgument(manifestArg);

        cmd.SetHandler((InvocationContext ctx) =>
        {
            var manifestPath = ctx.ParseResult.GetValueForArgument(manifestArg);
            ctx.ExitCode = CleanProject(manifestPath);
        });

        return cmd;
    }

    // ── Implementations ───────────────────────────────────────────────────────

    static int RunProject(string? explicitToml, bool useRelease, string? bin, string mode)
    {
        // 1. Build first
        int buildCode = PackageCompiler.Run(explicitToml, useRelease, bin);
        if (buildCode != 0) return buildCode;

        // 2. Resolve manifest to find output file
        string tomlPath;
        ProjectManifest manifest;
        try
        {
            tomlPath = ProjectManifest.Discover(Directory.GetCurrentDirectory(), explicitToml);
            manifest = ProjectManifest.Load(tomlPath);
        }
        catch (ManifestException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        string projectDir = Path.GetDirectoryName(Path.GetFullPath(tomlPath))!;
        string outDir     = Path.GetFullPath(Path.Combine(projectDir, manifest.Build.OutDir));

        // Determine target name
        string targetName = bin ?? manifest.Project.Name;
        if (manifest.Project.Kind == ProjectKind.Multi && bin is null)
        {
            if (manifest.ExeTargets.Count == 1)
                targetName = manifest.ExeTargets[0].Name;
            else
            {
                Console.Error.WriteLine("error: multiple [[exe]] targets — specify one with --bin <name>");
                return 1;
            }
        }

        string zpkgPath = Path.Combine(outDir, targetName + ".zpkg");
        if (!File.Exists(zpkgPath))
        {
            Console.Error.WriteLine($"error: expected output not found: {zpkgPath}");
            return 1;
        }

        // 3. Locate z42vm
        string? vm = FindVm(projectDir);
        if (vm is null)
        {
            Console.Error.WriteLine(
                "error: z42vm not found. Set Z42VM env var, add z42vm to PATH, or run `./scripts/package.sh` first.");
            return 1;
        }

        // 4. Execute
        var psi = new ProcessStartInfo(vm, $"\"{zpkgPath}\" --mode {mode}")
        {
            UseShellExecute = false,
        };
        var proc = Process.Start(psi)!;
        proc.WaitForExit();
        return proc.ExitCode;
    }

    static int CleanProject(string? explicitToml)
    {
        string tomlPath;
        ProjectManifest manifest;
        try
        {
            tomlPath = ProjectManifest.Discover(Directory.GetCurrentDirectory(), explicitToml);
            manifest = ProjectManifest.Load(tomlPath);
        }
        catch (ManifestException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        string projectDir = Path.GetDirectoryName(Path.GetFullPath(tomlPath))!;
        string outDir     = Path.GetFullPath(Path.Combine(projectDir, manifest.Build.OutDir));
        string cacheDir   = Path.GetFullPath(Path.Combine(projectDir, ".cache"));

        int removed = 0;
        foreach (var dir in new[] { outDir, cacheDir })
        {
            if (!Directory.Exists(dir)) continue;
            Directory.Delete(dir, recursive: true);
            Console.Error.WriteLine($"   Removed {Path.GetRelativePath(projectDir, dir)}/");
            removed++;
        }

        if (removed == 0)
            Console.Error.WriteLine("   Nothing to clean.");
        else
            Console.Error.WriteLine($"    Finished cleaning {manifest.Project.Name}");

        return 0;
    }

    /// Locate the z42vm binary. Search order:
    ///   1. Z42VM environment variable
    ///   2. Walk up from projectDir looking for artifacts/z42/z42vm
    ///   3. PATH
    static string? FindVm(string projectDir)
    {
        // 1. Env var
        var envVm = Environment.GetEnvironmentVariable("Z42VM");
        if (!string.IsNullOrWhiteSpace(envVm) && File.Exists(envVm))
            return envVm;

        // 2. Walk up looking for artifacts/z42/z42vm
        string vmName = OperatingSystem.IsWindows() ? "z42vm.exe" : "z42vm";
        var dir = new DirectoryInfo(projectDir);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "artifacts", "z42", vmName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        // 3. PATH
        foreach (var pathEntry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string candidate = Path.Combine(pathEntry, vmName);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    static Argument<string?> ManifestArg() =>
        new("manifest", () => null, "Path to .z42.toml (auto-discovered if omitted)")
        {
            Arity = ArgumentArity.ZeroOrOne
        };

    // ── Workspace mode dispatch (C4a) ─────────────────────────────────────────

    /// <summary>
    /// 判断是否走 workspace 模式：CWD 含 z42.workspace.toml 祖先 + 未指定 --no-workspace +
    /// 未显式给出 manifest 路径 → 走 orchestrator；否则返回 null 让 caller 走单工程路径。
    /// </summary>
    static int? TryRunWorkspace(
        bool release,
        string[] packages,
        bool allWorkspace,
        string[] exclude,
        bool noWorkspace,
        string? explicitManifest,
        bool checkOnly)
    {
        if (noWorkspace) return null;
        if (explicitManifest is not null) return null;       // 显式 path → 单工程

        try
        {
            var loader = new ManifestLoader();
            var ws = loader.DiscoverWorkspaceRoot(Directory.GetCurrentDirectory());
            if (ws is null) return null;                     // 无 workspace → fallback 单工程

            string profile = release ? "release" : "debug";
            var result = loader.LoadWorkspace(ws, profile);

            // 输出 orphan warnings（W7）
            foreach (var warn in result.Warnings)
                Console.Error.WriteLine(warn.Message);

            // 如果在 member 子目录运行（CWD 在某 member 下），自动加为 -p 默认
            if (packages.Length == 0 && !allWorkspace)
            {
                var memberInCwd = result.Members.FirstOrDefault(m =>
                    Directory.GetCurrentDirectory().StartsWith(
                        Path.GetDirectoryName(Path.GetFullPath(m.ManifestPath))!,
                        StringComparison.Ordinal));
                if (memberInCwd is not null)
                    packages = [memberInCwd.MemberName];
            }

            var orchestrator = new WorkspaceBuildOrchestrator();
            var opts = new WorkspaceBuildOrchestrator.BuildOptions(
                Selected:     packages,
                Excluded:     exclude,
                AllWorkspace: allWorkspace,
                CheckOnly:    checkOnly,
                Release:      release);

            var report = orchestrator.Build(result, ws.Manifest.DefaultMembers, opts);

            // 报告
            if (report.Succeeded.Count > 0)
                Console.Error.WriteLine($"    Built {report.Succeeded.Count} member(s): {string.Join(", ", report.Succeeded)}");
            if (report.Failed.Count > 0)
                Console.Error.WriteLine($"error: {report.Failed.Count} member(s) failed: {string.Join(", ", report.Failed)}");
            if (report.Blocked.Count > 0)
                Console.Error.WriteLine($"warning: {report.Blocked.Count} member(s) blocked: {string.Join(", ", report.Blocked)}");

            return report.ExitCode;
        }
        catch (ManifestException ex)
        {
            Console.Error.WriteLine(CliOutputFormatter.Format(ex, pretty: true));
            return 1;
        }
    }
}
