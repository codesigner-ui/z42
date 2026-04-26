using FluentAssertions;
using System.Diagnostics;
using System.Text.Json;
using Z42.Driver;
using Z42.Project;

namespace Z42.Tests;

/// 验证 C4b 查询命令：通过子进程跑 z42c 在 examples/workspace-full/ 上执行 + 校验输出。
public sealed class QueryCommandsTests
{
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

    static string ExampleDir => Path.Combine(RepoRoot, "examples", "workspace-full");
    static string Z42cDll    => Path.Combine(RepoRoot, "artifacts", "compiler", "z42.Driver", "bin", "Debug", "net10.0", "z42c.dll");

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
        psi.Environment["NO_COLOR"] = "1";   // 测试中禁用颜色

        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, stdout, stderr);
    }

    // ── info ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Info_OverviewListsMembersAndKinds()
    {
        var (code, stdout, _) = RunZ42c(ExampleDir, "info");
        code.Should().Be(0);
        stdout.Should().Contain("Workspace root").And.Contain("hello").And.Contain("utils").And.Contain("core");
        stdout.Should().Contain("(exe").And.Contain("(lib");
    }

    [Fact]
    public void Info_ResolvedShowsOriginsAndPolicyLock()
    {
        var (code, stdout, _) = RunZ42c(ExampleDir, "info", "--resolved", "-p", "hello");
        code.Should().Be(0);
        stdout.Should().Contain("[project]").And.Contain("hello").And.Contain("0.1.0");
        (stdout.Contains("(member)") || stdout.Contains("(workspace.project"))
            .Should().BeTrue("at least one origin label expected");
    }

    // ── metadata ─────────────────────────────────────────────────────────────

    [Fact]
    public void Metadata_JsonSchemaVersionAndMembers()
    {
        var (code, stdout, _) = RunZ42c(ExampleDir, "metadata", "--format", "json");
        code.Should().Be(0);

        var doc = JsonDocument.Parse(stdout);
        doc.RootElement.GetProperty("schema_version").GetString().Should().Be("1");
        doc.RootElement.GetProperty("workspace_root").GetString().Should().NotBeNullOrEmpty();

        var members = doc.RootElement.GetProperty("members").EnumerateArray().ToList();
        members.Should().HaveCount(3);
        members.Select(m => m.GetProperty("name").GetString()).Should().BeEquivalentTo(new[] { "core", "utils", "hello" });

        var graph = doc.RootElement.GetProperty("dependency_graph").EnumerateArray().ToList();
        graph.Should().HaveCount(2);   // hello→utils, utils→core
    }

    // ── tree ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Tree_ShowsDependencyHierarchy()
    {
        var (code, stdout, _) = RunZ42c(ExampleDir, "tree");
        code.Should().Be(0);
        stdout.Should().Contain("hello").And.Contain("utils").And.Contain("core");
        // hello 在 utils 之前出现（root 顶层）
        stdout.IndexOf("hello").Should().BeLessThan(stdout.IndexOf("core"));
    }

    // ── lint-manifest ────────────────────────────────────────────────────────

    [Fact]
    public void LintManifest_OkOnValidWorkspace()
    {
        var (code, stdout, _) = RunZ42c(ExampleDir, "lint-manifest");
        code.Should().Be(0);
        stdout.Should().Contain("manifest OK").And.Contain("3 member(s)");
    }
}
