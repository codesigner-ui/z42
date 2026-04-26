using System.CommandLine;
using System.CommandLine.Invocation;
using Tomlyn;
using Tomlyn.Model;
using Z42.Project;

namespace Z42.Driver;

/// <summary>
/// C4c 脚手架命令：new --workspace / new -p / init / fmt。
/// </summary>
static class ScaffoldCommands
{
    // ── new ──────────────────────────────────────────────────────────────────

    public static Command CreateNew()
    {
        var cmd        = new Command("new", "Scaffold a new workspace or member");
        var dirArg     = new Argument<string?>("dir", () => null, "Target directory (workspace mode) or omit for member mode") { Arity = ArgumentArity.ZeroOrOne };
        var wsOpt      = new Option<bool>("--workspace", "Create a new workspace at <dir>");
        var pkgOpt     = new Option<string?>(["-p", "--package"], "Create a new member with the given name (in current workspace)");
        var kindOpt    = new Option<string>("--kind", () => "lib", "Member kind: lib | exe");
        var entryOpt   = new Option<string?>("--entry", "Entry function for exe (e.g. Hello.main)");

        cmd.AddArgument(dirArg);
        cmd.AddOption(wsOpt);
        cmd.AddOption(pkgOpt);
        cmd.AddOption(kindOpt);
        cmd.AddOption(entryOpt);

        cmd.SetHandler((InvocationContext ctx) =>
        {
            var dir   = ctx.ParseResult.GetValueForArgument(dirArg);
            var ws    = ctx.ParseResult.GetValueForOption(wsOpt);
            var pkg   = ctx.ParseResult.GetValueForOption(pkgOpt);
            var kind  = ctx.ParseResult.GetValueForOption(kindOpt) ?? "lib";
            var entry = ctx.ParseResult.GetValueForOption(entryOpt);

            if (ws)
            {
                if (dir is null) { Console.Error.WriteLine("error: --workspace requires a target directory"); ctx.ExitCode = 1; return; }
                ctx.ExitCode = NewWorkspace(dir);
            }
            else if (pkg is not null)
            {
                ctx.ExitCode = NewMember(pkg, kind, entry);
            }
            else
            {
                Console.Error.WriteLine("error: specify --workspace <dir> or -p <name>");
                ctx.ExitCode = 1;
            }
        });

        return cmd;
    }

    public static Command CreateInit()
    {
        var cmd = new Command("init", "Upgrade current standalone manifest to a workspace");
        cmd.SetHandler((InvocationContext ctx) => ctx.ExitCode = InitWorkspace());
        return cmd;
    }

    static int NewWorkspace(string dir)
    {
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Console.Error.WriteLine($"error: directory '{dir}' already exists and is not empty");
            return 1;
        }
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "presets"));
        Directory.CreateDirectory(Path.Combine(dir, "libs"));
        Directory.CreateDirectory(Path.Combine(dir, "apps"));

        File.WriteAllText(Path.Combine(dir, "z42.workspace.toml"), Templates.Workspace);
        File.WriteAllText(Path.Combine(dir, ".gitignore"), Templates.Gitignore);
        File.WriteAllText(Path.Combine(dir, "presets", "lib-defaults.toml"), Templates.LibPreset);
        File.WriteAllText(Path.Combine(dir, "presets", "exe-defaults.toml"), Templates.ExePreset);

        Console.Error.WriteLine($"    Created workspace at {dir}/");
        return 0;
    }

    static int NewMember(string name, string kind, string? entry)
    {
        var loader = new ManifestLoader();
        var ws = loader.DiscoverWorkspaceRoot(Directory.GetCurrentDirectory());
        if (ws is null)
        {
            Console.Error.WriteLine("error: not in a workspace; run 'z42c new --workspace <dir>' first");
            return 1;
        }

        bool isExe = string.Equals(kind, "exe", StringComparison.OrdinalIgnoreCase);
        if (isExe && entry is null)
        {
            // 默认 entry：<Capitalized>.main
            entry = char.ToUpperInvariant(name[0]) + name[1..] + ".main";
        }

        // 决定路径：lib → libs/<name>，exe → apps/<name>
        string subDir = isExe ? "apps" : "libs";
        string memberDir = Path.Combine(ws.Manifest.RootDirectory, subDir, name);
        if (Directory.Exists(memberDir) && Directory.EnumerateFileSystemEntries(memberDir).Any())
        {
            Console.Error.WriteLine($"error: directory '{memberDir}' already exists and is not empty");
            return 1;
        }
        Directory.CreateDirectory(Path.Combine(memberDir, "src"));

        string manifest = Templates.MemberManifest(name, isExe ? "exe" : "lib", entry);
        File.WriteAllText(Path.Combine(memberDir, $"{name}.z42.toml"), manifest);

        string srcFile = isExe ? Templates.ExeSourceFile(name, entry!) : Templates.LibSourceFile(name);
        File.WriteAllText(Path.Combine(memberDir, "src", $"{Capitalize(name)}.z42"), srcFile);

        Console.Error.WriteLine($"    Created member '{name}' at {Path.GetRelativePath(ws.Manifest.RootDirectory, memberDir)}/");
        return 0;
    }

    static int InitWorkspace()
    {
        string cwd = Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(cwd, "z42.workspace.toml")))
        {
            Console.Error.WriteLine("error: directory is already a workspace root");
            return 1;
        }

        // 不修改原 manifest；生成 z42.workspace.toml 含 members 推断
        var existingManifest = Directory.GetFiles(cwd, "*.z42.toml").FirstOrDefault();
        string template = existingManifest is null
            ? Templates.Workspace
            : Templates.Workspace.Replace("members         = [\"libs/*\", \"apps/*\"]",
                                          $"members         = [\".\"]   # original manifest: {Path.GetFileName(existingManifest)}");

        File.WriteAllText(Path.Combine(cwd, "z42.workspace.toml"), template);
        if (!File.Exists(Path.Combine(cwd, ".gitignore")))
            File.WriteAllText(Path.Combine(cwd, ".gitignore"), Templates.Gitignore);

        Console.Error.WriteLine("    Created z42.workspace.toml");
        return 0;
    }

    static string Capitalize(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // ── fmt ──────────────────────────────────────────────────────────────────

    public static Command CreateFmt()
    {
        var cmd = new Command("fmt", "Format z42.toml manifests in current workspace or directory");
        cmd.SetHandler((InvocationContext ctx) => ctx.ExitCode = RunFmt());
        return cmd;
    }

    static int RunFmt()
    {
        try
        {
            var loader = new ManifestLoader();
            var ws = loader.DiscoverWorkspaceRoot(Directory.GetCurrentDirectory());

            var files = new List<string>();
            if (ws is not null)
            {
                files.Add(ws.Manifest.ManifestPath);
                var result = loader.LoadWorkspace(ws);
                files.AddRange(result.Members.Select(m => m.ManifestPath));
            }
            else
            {
                files.AddRange(Directory.GetFiles(Directory.GetCurrentDirectory(), "*.z42.toml"));
            }

            int formatted = 0;
            foreach (var path in files)
            {
                if (FormatFile(path)) formatted++;
            }
            Console.Error.WriteLine($"    Formatted {formatted}/{files.Count} manifest(s)");
            return 0;
        }
        catch (ManifestException ex)
        {
            Console.Error.WriteLine(CliOutputFormatter.Format(ex, pretty: true));
            return 1;
        }
    }

    static bool FormatFile(string path)
    {
        string original = File.ReadAllText(path);
        // 用 Tomlyn round-trip：解析 + 序列化（保留语义但可能不保留所有注释格式）
        var doc = TomlSerializer.Deserialize<TomlTable>(original);
        if (doc is null) return false;
        string formatted = TomlSerializer.Serialize(doc);
        if (formatted == original) return false;
        File.WriteAllText(path, formatted);
        return true;
    }

    // ── Templates ────────────────────────────────────────────────────────────

    static class Templates
    {
        public const string Workspace = """
            [workspace]
            members         = ["libs/*", "apps/*"]
            default-members = []

            [workspace.project]
            version = "0.1.0"
            license = "MIT"

            [workspace.build]
            out_dir   = "dist"
            cache_dir = ".cache"
            """;

        public const string Gitignore = """
            dist/
            .cache/
            """;

        public const string LibPreset = """
            [project]
            kind = "lib"

            [sources]
            include = ["src/**/*.z42"]
            """;

        public const string ExePreset = """
            [project]
            kind = "exe"

            [sources]
            include = ["src/**/*.z42"]
            """;

        public static string MemberManifest(string name, string kind, string? entry)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[project]");
            sb.AppendLine($"name              = \"{name}\"");
            sb.AppendLine($"kind              = \"{kind}\"");
            if (entry is not null) sb.AppendLine($"entry             = \"{entry}\"");
            sb.AppendLine("version.workspace = true");
            sb.AppendLine("license.workspace = true");
            sb.AppendLine();
            sb.AppendLine("[sources]");
            sb.AppendLine("include = [\"src/**/*.z42\"]");
            return sb.ToString();
        }

        public static string LibSourceFile(string name)
        {
            string capName = char.ToUpperInvariant(name[0]) + name[1..];
            return $"namespace {capName};\n\npublic static class {capName} {{\n    public static string hello() {{\n        return \"{name}\";\n    }}\n}}\n";
        }

        public static string ExeSourceFile(string name, string entry)
        {
            int dotIdx = entry.IndexOf('.');
            string nsName  = dotIdx > 0 ? entry[..dotIdx] : "Main";
            string fnName  = dotIdx > 0 ? entry[(dotIdx + 1)..] : "main";
            return $"namespace {nsName};\n\npublic static class {nsName} {{\n    public static int {fnName}() {{\n        Console.println(\"hello from {name}\");\n        return 0;\n    }}\n}}\n";
        }
    }
}
