using FluentAssertions;
using System.Diagnostics;

namespace Z42.Tests;

/// 验证 C4c 脚手架与清理命令：new --workspace / new -p / init / fmt / clean。
/// 用临时目录 + 子进程跑 z42c 验证生成结构与清理效果。
public sealed class ScaffoldCommandsTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "z42_sc_" + Path.GetRandomFileName());

    public ScaffoldCommandsTests() => Directory.CreateDirectory(_tmp);
    public void Dispose()           => Directory.Delete(_tmp, recursive: true);

    static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "examples")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "src", "compiler")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("repo root not found");
        }
    }

    static string Z42cDll => Path.Combine(RepoRoot, "artifacts", "compiler", "z42.Driver", "bin", "z42c.dll");

    static (int code, string stdout, string stderr) RunZ42c(string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workDir,
        };
        psi.ArgumentList.Add(Z42cDll);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["NO_COLOR"] = "1";

        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, stdout, stderr);
    }

    // ── new --workspace ──────────────────────────────────────────────────────

    [Fact]
    public void NewWorkspace_GeneratesExpectedStructure()
    {
        var (code, _, stderr) = RunZ42c(_tmp, "new", "--workspace", "monorepo");
        code.Should().Be(0, stderr);

        string root = Path.Combine(_tmp, "monorepo");
        File.Exists(Path.Combine(root, "z42.workspace.toml")).Should().BeTrue();
        File.Exists(Path.Combine(root, ".gitignore")).Should().BeTrue();
        File.Exists(Path.Combine(root, "presets", "lib-defaults.toml")).Should().BeTrue();
        File.Exists(Path.Combine(root, "presets", "exe-defaults.toml")).Should().BeTrue();
        Directory.Exists(Path.Combine(root, "libs")).Should().BeTrue();
        Directory.Exists(Path.Combine(root, "apps")).Should().BeTrue();
    }

    [Fact]
    public void NewWorkspace_NonEmptyDir_Fails()
    {
        Directory.CreateDirectory(Path.Combine(_tmp, "existing"));
        File.WriteAllText(Path.Combine(_tmp, "existing", "stuff.txt"), "x");
        var (code, _, _) = RunZ42c(_tmp, "new", "--workspace", "existing");
        code.Should().NotBe(0);
    }

    // ── new -p（lib / exe） ──────────────────────────────────────────────────

    [Fact]
    public void NewMember_LibInsideWorkspace()
    {
        // 先创建 workspace
        RunZ42c(_tmp, "new", "--workspace", "ws").code.Should().Be(0);
        string wsDir = Path.Combine(_tmp, "ws");

        var (code, _, stderr) = RunZ42c(wsDir, "new", "-p", "foo", "--kind", "lib");
        code.Should().Be(0, stderr);

        File.Exists(Path.Combine(wsDir, "libs", "foo", "foo.z42.toml")).Should().BeTrue();
        File.Exists(Path.Combine(wsDir, "libs", "foo", "src", "Foo.z42")).Should().BeTrue();

        string toml = File.ReadAllText(Path.Combine(wsDir, "libs", "foo", "foo.z42.toml"));
        toml.Should().Contain("name              = \"foo\"").And.Contain("kind              = \"lib\"");
    }

    [Fact]
    public void NewMember_ExeInsideWorkspace()
    {
        RunZ42c(_tmp, "new", "--workspace", "ws").code.Should().Be(0);
        string wsDir = Path.Combine(_tmp, "ws");

        var (code, _, _) = RunZ42c(wsDir, "new", "-p", "hello", "--kind", "exe");
        code.Should().Be(0);

        File.Exists(Path.Combine(wsDir, "apps", "hello", "hello.z42.toml")).Should().BeTrue();
        string toml = File.ReadAllText(Path.Combine(wsDir, "apps", "hello", "hello.z42.toml"));
        toml.Should().Contain("kind              = \"exe\"").And.Contain("entry             = \"Hello.main\"");
    }

    [Fact]
    public void NewMember_NotInWorkspace_Fails()
    {
        var (code, _, _) = RunZ42c(_tmp, "new", "-p", "foo", "--kind", "lib");
        code.Should().NotBe(0);
    }

    // ── init ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Init_CreatesWorkspaceManifest()
    {
        File.WriteAllText(Path.Combine(_tmp, "single.z42.toml"), "[project]\nname = \"single\"\nkind = \"lib\"\n");
        var (code, _, _) = RunZ42c(_tmp, "init");
        code.Should().Be(0);
        File.Exists(Path.Combine(_tmp, "z42.workspace.toml")).Should().BeTrue();
    }

    [Fact]
    public void Init_AlreadyWorkspace_Fails()
    {
        File.WriteAllText(Path.Combine(_tmp, "z42.workspace.toml"), "[workspace]\nmembers = []\n");
        var (code, _, _) = RunZ42c(_tmp, "init");
        code.Should().NotBe(0);
    }

    // ── clean ────────────────────────────────────────────────────────────────

    [Fact]
    public void Clean_WorkspaceDistAndCache()
    {
        RunZ42c(_tmp, "new", "--workspace", "ws").code.Should().Be(0);
        string wsDir = Path.Combine(_tmp, "ws");
        // 制造 dist + .cache
        Directory.CreateDirectory(Path.Combine(wsDir, "dist"));
        File.WriteAllText(Path.Combine(wsDir, "dist", "foo.zpkg"), "x");
        Directory.CreateDirectory(Path.Combine(wsDir, ".cache", "foo"));
        File.WriteAllText(Path.Combine(wsDir, ".cache", "foo", "x.zbc"), "x");

        var (code, _, _) = RunZ42c(wsDir, "clean");
        code.Should().Be(0);

        Directory.Exists(Path.Combine(wsDir, "dist")).Should().BeFalse();
        Directory.Exists(Path.Combine(wsDir, ".cache")).Should().BeFalse();
    }

    [Fact]
    public void Clean_NothingToClean_Succeeds()
    {
        RunZ42c(_tmp, "new", "--workspace", "ws").code.Should().Be(0);
        string wsDir = Path.Combine(_tmp, "ws");
        var (code, _, _) = RunZ42c(wsDir, "clean");
        code.Should().Be(0);
    }
}
