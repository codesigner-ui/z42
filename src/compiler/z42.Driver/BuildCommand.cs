using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using Z42.Pipeline;
using Z42.Project;

namespace Z42.Driver;

static class BuildCommand
{
    // fix-run-forward-script-args (2026-06-07): args after the first literal `--`
    // are stashed here by Program.Main (split off before System.CommandLine
    // parses) and read by the `run` handler to forward to the executed program.
    internal static string[] ForwardedScriptArgs = [];

    /// Split argv at the FIRST literal `--`: everything before is z42c's own CLI
    /// (parsed by System.CommandLine), everything after is forwarded verbatim to
    /// the program that `run` executes. No `--` → (args, []). Doing this in
    /// Program.Main keeps the `--` and trailing tokens away from
    /// System.CommandLine (beta4 prints help when post-`--` tokens are present).
    internal static (string[] Pre, string[] Forwarded) SplitForwardedArgs(string[] args)
    {
        int i = System.Array.IndexOf(args, "--");
        if (i < 0) return (args, []);
        return (args[..i], args[(i + 1)..]);
    }

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
        var noIncrOpt      = new Option<bool>("--no-incremental", "Disable incremental cache (full rebuild)");
        // 1.5b split-debug-symbols: CLI override for [profile.*].strip toml field.
        // Bool? semantics: not specified → null → use toml default; --strip-symbols → true; --strip-symbols=false → false.
        var stripOpt       = new Option<bool?>("--strip-symbols", "Strip debug symbols to a sidecar `<name>.zsym` (overrides [profile.*].strip)") { Arity = ArgumentArity.ZeroOrOne };

        cmd.AddArgument(manifestArg);
        cmd.AddOption(releaseOpt);
        cmd.AddOption(binOpt);
        cmd.AddOption(packagesOpt);
        cmd.AddOption(workspaceOpt);
        cmd.AddOption(excludeOpt);
        cmd.AddOption(noWorkspaceOpt);
        cmd.AddOption(noIncrOpt);
        cmd.AddOption(stripOpt);

        cmd.SetHandler((InvocationContext ctx) =>
        {
            var manifest = ctx.ParseResult.GetValueForArgument(manifestArg);
            var release  = ctx.ParseResult.GetValueForOption(releaseOpt);
            var bin      = ctx.ParseResult.GetValueForOption(binOpt);
            var packages = ctx.ParseResult.GetValueForOption(packagesOpt) ?? [];
            var workspace = ctx.ParseResult.GetValueForOption(workspaceOpt);
            var exclude  = ctx.ParseResult.GetValueForOption(excludeOpt) ?? [];
            var noWs     = ctx.ParseResult.GetValueForOption(noWorkspaceOpt);
            var noIncr   = ctx.ParseResult.GetValueForOption(noIncrOpt);
            var stripCli = ctx.ParseResult.GetValueForOption(stripOpt);

            // Workspace 模式判断（C4a）：显式 path / --no-workspace → 走单工程
            ctx.ExitCode = TryRunWorkspace(release, packages, workspace, exclude, noWs, manifest, checkOnly: false, incremental: !noIncr, stripCli: stripCli)
                            ?? PackageCompiler.Run(manifest, release, bin, useIncremental: !noIncr, cliStripOverride: stripCli);
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
        var noIncrOpt      = new Option<bool>("--no-incremental", "Disable incremental cache");

        cmd.AddArgument(manifestArg);
        cmd.AddOption(binOpt);
        cmd.AddOption(packagesOpt);
        cmd.AddOption(workspaceOpt);
        cmd.AddOption(excludeOpt);
        cmd.AddOption(noWorkspaceOpt);
        cmd.AddOption(noIncrOpt);

        cmd.SetHandler((InvocationContext ctx) =>
        {
            var manifest = ctx.ParseResult.GetValueForArgument(manifestArg);
            var bin      = ctx.ParseResult.GetValueForOption(binOpt);
            var packages = ctx.ParseResult.GetValueForOption(packagesOpt) ?? [];
            var workspace = ctx.ParseResult.GetValueForOption(workspaceOpt);
            var exclude  = ctx.ParseResult.GetValueForOption(excludeOpt) ?? [];
            var noWs     = ctx.ParseResult.GetValueForOption(noWorkspaceOpt);
            var noIncr   = ctx.ParseResult.GetValueForOption(noIncrOpt);

            ctx.ExitCode = TryRunWorkspace(release: false, packages, workspace, exclude, noWs, manifest, checkOnly: true, incremental: !noIncr)
                            ?? PackageCompiler.RunCheck(manifest, bin);
        });

        return cmd;
    }

    public static Command CreateRun()
    {
        var cmd         = new Command("run",
            "Build and execute a z42 project or single `.z42` script");
        // add-z42c-run-script (2026-05-17): accept either a project manifest
        // (.z42.toml — existing behavior) or a single .z42 source file (script
        // mode — compile to temp .zbc, auto-detect Main(), exec z42vm).
        var pathArg     = new Argument<string?>("path",
            () => null,
            "Path to .z42.toml (manifest, auto-discovered if omitted) or .z42 (script)")
        {
            Arity = ArgumentArity.ZeroOrOne
        };
        var releaseOpt  = new Option<bool>("--release", "Build and run with the release profile");
        var binOpt      = new Option<string?>("--bin", "Run the named [[exe]] target (project mode only)");
        var modeOpt     = new Option<string>("--mode", () => "interp", "VM execution mode: interp | jit | aot");

        cmd.AddArgument(pathArg);
        cmd.AddOption(releaseOpt);
        cmd.AddOption(binOpt);
        cmd.AddOption(modeOpt);

        cmd.SetHandler((InvocationContext ctx) =>
        {
            var path    = ctx.ParseResult.GetValueForArgument(pathArg);
            var release = ctx.ParseResult.GetValueForOption(releaseOpt);
            var bin     = ctx.ParseResult.GetValueForOption(binOpt);
            var mode    = ctx.ParseResult.GetValueForOption(modeOpt) ?? "interp";
            // fix-run-forward-script-args (2026-06-07): everything after a literal
            // `--` separator is forwarded to the running z42 program, not parsed by
            // z42c. Program.Main splits these off BEFORE System.CommandLine sees
            // them (System.CommandLine beta4 otherwise prints help when post-`--`
            // tokens are present) and stashes them here; we hand them to z42vm
            // after its own `--` so the program reads them via
            // Std.IO.Environment.GetCommandLineArgs(). e.g.
            //   z42c run app.z42 -- a b c   →   program argv = ["a","b","c"]
            var scriptArgs = ForwardedScriptArgs;

            // Script mode: explicit .z42 path.
            if (path is not null && path.EndsWith(".z42", StringComparison.OrdinalIgnoreCase))
            {
                if (bin is not null)
                {
                    Console.Error.WriteLine("error: --bin is not valid in script mode");
                    ctx.ExitCode = 2;
                    return;
                }
                ctx.ExitCode = RunScript(path, mode, scriptArgs);
                return;
            }

            ctx.ExitCode = RunProject(path, release, bin, mode, scriptArgs);
        });

        return cmd;
    }

    public static Command CreateClean()
    {
        var cmd         = new Command("clean", "Remove build artifacts for a z42 project or workspace");
        var manifestArg = ManifestArg();
        var packagesOpt = new Option<string?>(["-p", "--package"], "Clean only the named workspace member");
        var noWsOpt     = new Option<bool>("--no-workspace", "Force standalone mode");

        cmd.AddArgument(manifestArg);
        cmd.AddOption(packagesOpt);
        cmd.AddOption(noWsOpt);

        cmd.SetHandler((InvocationContext ctx) =>
        {
            var manifestPath = ctx.ParseResult.GetValueForArgument(manifestArg);
            var pkg          = ctx.ParseResult.GetValueForOption(packagesOpt);
            var noWs         = ctx.ParseResult.GetValueForOption(noWsOpt);

            // C4c: workspace 模式优先（除非 --no-workspace 或显式 manifest）
            int? wsResult = TryCleanWorkspace(pkg, noWs, manifestPath);
            ctx.ExitCode = wsResult ?? CleanProject(manifestPath);
        });

        return cmd;
    }

    static int? TryCleanWorkspace(string? pkg, bool noWorkspace, string? explicitManifest)
    {
        if (noWorkspace) return null;
        if (explicitManifest is not null) return null;

        try
        {
            var loader = new ManifestLoader();
            var ws = loader.DiscoverWorkspaceRoot(Directory.GetCurrentDirectory());
            if (ws is null) return null;

            var result = loader.LoadWorkspace(ws);

            int removed = 0;
            if (pkg is null)
            {
                // 全 workspace clean：删除 [workspace.build] dist_dir + cache_dir。
                // restructure-build-output-dirs (2026-06-06): 走 effective 默认
                // (workspace.OutputDir ?? workspace_root) + (dist ?? ${output_dir}/dist) /
                // (cache ?? ${output_dir}/.cache) —— 与 build 路径计算保持一致。
                var wsBuild = ws.Manifest.WorkspaceBuild;
                string outputAbs = ResolveWorkspacePath(wsBuild.OutputDir, ws.Manifest.RootDirectory, ws.Manifest.RootDirectory);
                string distAbs   = ResolveWorkspacePath(wsBuild.DistDir,  outputAbs,                  Path.Combine(outputAbs, "dist"));
                string cacheAbs  = ResolveWorkspacePath(wsBuild.CacheDir, outputAbs,                  Path.Combine(outputAbs, ".cache"));

                foreach (var dir in new[] { distAbs, cacheAbs })
                {
                    if (!Directory.Exists(dir)) continue;
                    Directory.Delete(dir, recursive: true);
                    Console.Error.WriteLine($"   Removed {Path.GetRelativePath(ws.Manifest.RootDirectory, dir)}/");
                    removed++;
                }
            }
            else
            {
                // per-member clean
                var member = result.Members.FirstOrDefault(m => m.MemberName == pkg);
                if (member is null)
                {
                    Console.Error.WriteLine($"error: member '{pkg}' not found");
                    return 1;
                }

                if (File.Exists(member.EffectiveProductPath))
                {
                    File.Delete(member.EffectiveProductPath);
                    Console.Error.WriteLine($"   Removed {Path.GetRelativePath(ws.Manifest.RootDirectory, member.EffectiveProductPath)}");
                    removed++;
                }
                if (Directory.Exists(member.EffectiveCacheDir))
                {
                    Directory.Delete(member.EffectiveCacheDir, recursive: true);
                    Console.Error.WriteLine($"   Removed {Path.GetRelativePath(ws.Manifest.RootDirectory, member.EffectiveCacheDir)}/");
                    removed++;
                }
            }

            Console.Error.WriteLine(removed == 0 ? "   Nothing to clean." : $"    Finished cleaning workspace");
            return 0;
        }
        catch (ManifestException ex)
        {
            Console.Error.WriteLine(CliOutputFormatter.Format(ex, pretty: true));
            return 1;
        }
    }

    // ── Implementations ───────────────────────────────────────────────────────

    // add-z42c-run-script (2026-05-17): single-file script mode.
    // 编译 `.z42` 到临时 zbc → 自动检测 Main entry → exec z42vm。
    // 用于无 manifest 的 build/test scripts（Phase 1 of shell → z42 自举）。
    static int RunScript(string scriptPath, string mode, string[] scriptArgs)
    {
        var src = new FileInfo(scriptPath);
        if (!src.Exists)
        {
            Console.Error.WriteLine($"error: script not found: {scriptPath}");
            return 1;
        }

        // 1. Compile to in-memory IR module.
        string sourceText;
        try { sourceText = File.ReadAllText(src.FullName); }
        catch
        {
            Console.Error.WriteLine($"error: cannot read {src.FullName}");
            return 1;
        }

        var tokens = new Z42.Syntax.Lexer.Lexer(sourceText, src.FullName).Tokenize();
        var parser = new Z42.Syntax.Parser.Parser(tokens);
        var cu     = parser.ParseCompilationUnit();
        if (parser.Diagnostics.HasErrors)
        {
            parser.Diagnostics.PrintAll();
            return 1;
        }

        var depIndex = SingleFileCompiler.LocateDepIndex(src.FullName);
        var imported = SingleFileCompiler.LocateImportedSymbols(src.FullName, cu.Usings);
        var result   = PipelineCore.CheckAndGenerate(cu, src.FullName, depIndex, imported: imported);
        result.Diags.PrintAll();
        if (result.Diags.HasErrors || result.Module is null) return 1;

        // 2. Auto-detect Main entry.
        var fnNames = result.Module.Functions.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var (entry, err) = AutoDetectScriptEntry(fnNames);
        if (err is not null)
        {
            Console.Error.WriteLine($"error: {err}");
            return 1;
        }
        if (entry is null)
        {
            Console.Error.WriteLine(
                $"error: no `Main()` function found in script `{src.Name}`. " +
                $"Define a `Main()` (optionally inside a namespace) to use `z42c run`.");
            return 1;
        }

        // 3. Write temp zbc.
        string tmpDir = Path.Combine(
            Path.GetTempPath(),
            $"z42c-run-{Path.GetFileNameWithoutExtension(src.Name)}-{Path.GetRandomFileName()}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            string zbcPath = Path.Combine(tmpDir, "script.zbc");
            var exports    = result.Module.Functions.Select(f => f.Name).ToList();
            File.WriteAllBytes(zbcPath, Z42.IR.BinaryFormat.ZbcWriter.Write(result.Module, exports: exports));

            // 4. Locate z42vm + exec.
            string? vm = FindVm(Path.GetDirectoryName(src.FullName) ?? ".");
            if (vm is null)
            {
                Console.Error.WriteLine(
                    "error: z42vm not found. Set Z42VM env var, add z42vm to PATH, or run `./scripts/package.sh` first.");
                return 1;
            }

            var psi = new ProcessStartInfo(vm)
            {
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(zbcPath);
            psi.ArgumentList.Add(entry);
            psi.ArgumentList.Add("--mode");
            psi.ArgumentList.Add(mode);
            // Forward script args after `--` so z42vm hands them to the program.
            if (scriptArgs.Length > 0)
            {
                psi.ArgumentList.Add("--");
                foreach (var a in scriptArgs) psi.ArgumentList.Add(a);
            }

            var proc = Process.Start(psi)!;
            proc.WaitForExit();
            return proc.ExitCode;
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// Auto-detect Main entry from function FQ names. Mirrors
    /// `PackageCompiler.BuildTarget::AutoDetectEntry` + `vm::resolve_entry`.
    /// Priority: `*.Main` → `Main` → `*.main` → `main`. Returns (entry, error);
    /// error != null on ambiguity (multiple `*.Main`).
    static (string? Entry, string? Error) AutoDetectScriptEntry(HashSet<string> fnNames)
    {
        static (string?, string?) PickFrom(List<string> candidates, string kindLabel)
        {
            if (candidates.Count == 1) return (candidates[0], null);
            if (candidates.Count >  1) return (null,
                $"multiple `{kindLabel}` functions found ({string.Join(", ", candidates)}); " +
                $"the script must have exactly one `Main()` to run via `z42c run`");
            return (null, null);
        }

        var qMain = fnNames.Where(n => n.EndsWith(".Main", StringComparison.Ordinal))
                           .OrderBy(s => s, StringComparer.Ordinal).ToList();
        var (e1, err1) = PickFrom(qMain, "Main");
        if (err1 is not null || e1 is not null) return (e1, err1);
        if (fnNames.Contains("Main")) return ("Main", null);

        var qMainLc = fnNames.Where(n => n.EndsWith(".main", StringComparison.Ordinal))
                             .OrderBy(s => s, StringComparer.Ordinal).ToList();
        var (e2, err2) = PickFrom(qMainLc, "main");
        if (err2 is not null || e2 is not null) return (e2, err2);
        if (fnNames.Contains("main")) return ("main", null);

        return (null, null);
    }

    static int RunProject(string? explicitToml, bool useRelease, string? bin, string mode, string[] scriptArgs)
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
            var result = ProjectManifest.LoadWithWarnings(tomlPath);
            manifest = result.Manifest;
            foreach (var w in result.Warnings)
                Console.Error.WriteLine(w.Message);
        }
        catch (ManifestException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        string projectDir = Path.GetDirectoryName(Path.GetFullPath(tomlPath))!;
        // restructure-build-output-dirs (2026-06-06): cascade-defaulted dist_dir.
        string outDir     = manifest.ResolveBuildLayout(projectDir).EffectiveDistDir;

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

        // 4. Execute (ArgumentList avoids quoting pitfalls and lets us forward
        // user args after `--`, same contract as script mode).
        var psi = new ProcessStartInfo(vm) { UseShellExecute = false };
        psi.ArgumentList.Add(zpkgPath);
        psi.ArgumentList.Add("--mode");
        psi.ArgumentList.Add(mode);
        if (scriptArgs.Length > 0)
        {
            psi.ArgumentList.Add("--");
            foreach (var a in scriptArgs) psi.ArgumentList.Add(a);
        }
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
            var result = ProjectManifest.LoadWithWarnings(tomlPath);
            manifest = result.Manifest;
            foreach (var w in result.Warnings)
                Console.Error.WriteLine(w.Message);
        }
        catch (ManifestException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        string projectDir = Path.GetDirectoryName(Path.GetFullPath(tomlPath))!;
        // restructure-build-output-dirs (2026-06-06): clean uses the same
        // effective dist_dir / cache_dir cascade as build (any explicit
        // `[build].cache_dir` is honoured; defaults to `${output_dir}/.cache`).
        var cleanLayout = manifest.ResolveBuildLayout(projectDir);
        string outDir   = cleanLayout.EffectiveDistDir;
        string cacheDir = cleanLayout.EffectiveCacheDir;

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

    /// <summary>
    /// Resolve a workspace-level path field (output_dir / cache_dir /
    /// dist_dir): if explicit and absolute → use as-is; if explicit and
    /// relative → combine with workspaceRoot; if null → use the supplied
    /// fallback (which itself may be derived from a parent effective path).
    /// restructure-build-output-dirs (2026-06-06).
    /// </summary>
    static string ResolveWorkspacePath(string? raw, string workspaceRoot, string fallback)
    {
        if (raw is null) return Path.GetFullPath(fallback);
        return Path.IsPathRooted(raw)
            ? Path.GetFullPath(raw)
            : Path.GetFullPath(Path.Combine(workspaceRoot, raw));
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

        // 2. Walk up looking for the z42vm binary in known layout positions:
        //    a) artifacts/build/runtime/release/z42vm  (current dev layout)
        //    b) artifacts/build/runtime/debug/z42vm    (current dev layout)
        //    c) artifacts/z42/z42vm                    (legacy layout)
        string vmName = OperatingSystem.IsWindows() ? "z42vm.exe" : "z42vm";
        var dir = new DirectoryInfo(projectDir);
        while (dir != null)
        {
            foreach (var rel in new[] {
                Path.Combine("artifacts", "build", "runtime", "release", vmName),
                Path.Combine("artifacts", "build", "runtime", "debug",   vmName),
                Path.Combine("artifacts", "z42", vmName),
            })
            {
                string candidate = Path.Combine(dir.FullName, rel);
                if (File.Exists(candidate)) return candidate;
            }
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
        bool checkOnly,
        bool incremental = true,
        bool? stripCli = null)
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

            // 1.5b: workspace effective strip = CLI override ?? built-in default (release=true / debug=false).
            // [profile.*].strip from individual member toml not honored at workspace level — workspace
            // members forbid [profile.*] sections (WS003). Workspace-root profile would need a separate
            // resolution path (deferred).
            bool effectiveStrip = stripCli ?? release;

            var orchestrator = new WorkspaceBuildOrchestrator();
            var opts = new WorkspaceBuildOrchestrator.BuildOptions(
                Selected:     packages,
                Excluded:     exclude,
                AllWorkspace: allWorkspace,
                CheckOnly:    checkOnly,
                Release:      release,
                Incremental:  incremental,
                StripSymbols: effectiveStrip);

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
