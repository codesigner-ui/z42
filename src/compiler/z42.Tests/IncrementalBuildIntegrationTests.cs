using FluentAssertions;
using System.Diagnostics;

namespace Z42.Tests;

/// 端到端：跑两次 z42c build 验证第二次命中 cache。
public sealed class IncrementalBuildIntegrationTests
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

    static string Z42cDll => Path.Combine(RepoRoot, "artifacts", "compiler", "z42.Driver", "bin", "Debug", "net10.0", "z42c.dll");

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

    /// <summary>清空 stdlib 输出后第一次 build 应全 fresh，第二次应全 cached。</summary>
    [Fact]
    public void StdlibBuild_SecondRun_AllCached()
    {
        string libsRoot = Path.Combine(RepoRoot, "src", "libraries");
        string artifactsLibs = Path.Combine(RepoRoot, "artifacts", "libraries");

        // 清空产物
        if (Directory.Exists(artifactsLibs))
            Directory.Delete(artifactsLibs, recursive: true);

        // 第一次：全 fresh
        var (code1, _, err1) = RunZ42c(libsRoot, "build", "--workspace", "--release");
        code1.Should().Be(0, err1);
        err1.Should().Contain("cached: 0/");
        err1.Should().NotContain("cached: 33/");   // 第一次必然不命中

        // 第二次：全 cached（33 = z42.core 文件数；如果新增 / 删除 stdlib 文件
        // 需同步更新此处。fix-incremental-cache-invalidation 引入 "preserved
        // existing zpkg" 行，但 "cached: 33/33" 仍打印）
        var (code2, _, err2) = RunZ42c(libsRoot, "build", "--workspace", "--release");
        code2.Should().Be(0, err2);
        err2.Should().Contain("cached: 33/33");
        err2.Should().Contain("cached: 2/2");
        err2.Should().Contain("cached: 4/4");
    }

    /// <summary>--no-incremental 强制全 fresh。</summary>
    [Fact]
    public void StdlibBuild_NoIncremental_ForcesFreshEvenWithCache()
    {
        string libsRoot = Path.Combine(RepoRoot, "src", "libraries");

        // 先确保 cache 已存在
        var (code1, _, _) = RunZ42c(libsRoot, "build", "--workspace", "--release");
        code1.Should().Be(0);

        // --no-incremental：仍 0/N 即使 cache 存在（33 = z42.core 文件数）
        var (code2, _, err2) = RunZ42c(libsRoot, "build", "--workspace", "--release", "--no-incremental");
        code2.Should().Be(0, err2);
        err2.Should().Contain("cached: 0/33");
    }
}
