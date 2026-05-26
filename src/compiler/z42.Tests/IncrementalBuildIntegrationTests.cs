using FluentAssertions;
using System.Diagnostics;
using Xunit;

namespace Z42.Tests;

/// 端到端：跑两次 z42c build 验证第二次命中 cache。
/// Sequential collection: this test deletes artifacts/build/libraries/ and
/// rebuilds it — must not run concurrently with StdlibSidecarPairingTests.
[Collection("StdlibArtifacts")]
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

    static string Z42cDll => Path.Combine(RepoRoot, "artifacts", "build", "compiler", "z42.Driver", "bin", "z42c.dll");

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
        // Read stdout + stderr in parallel: sequential ReadToEnd() deadlocks
        // on Windows once the child fills the 4 KB pipe buffer on the
        // not-yet-being-read stream (Linux/macOS have larger buffers, so the
        // race usually hides there). `z42c build --workspace` emits enough
        // progress output on stderr to reliably hit this on windows-latest CI.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    /// <summary>清空 stdlib 输出后第一次 build 应全 fresh，第二次应全 cached。</summary>
    [Fact]
    public void StdlibBuild_SecondRun_AllCached()
    {
        string libsRoot = Path.Combine(RepoRoot, "src", "libraries");
        string artifactsLibs = Path.Combine(RepoRoot, "artifacts", "build", "libraries");

        // 清空产物
        if (Directory.Exists(artifactsLibs))
            Directory.Delete(artifactsLibs, recursive: true);

        // 第一次：全 fresh
        var (code1, _, err1) = RunZ42c(libsRoot, "build", "--workspace", "--release");
        code1.Should().Be(0, err1);
        err1.Should().Contain("cached: 0/");
        err1.Should().NotContain("cached: 34/");   // 第一次必然不命中

        // 第二次：全 cached（39 = z42.core 文件数；2026-05-03 fix-delegate-reference-equality
        // 新增 DelegateOps.z42（D-5 落地），43 → 44；2026-05-04 D-7-residual 新增
        // Disposable.z42（IDisposable factory），44 → 45；2026-05-07
        // reorganize-gc-stdlib 新增 GCHandle.z42 + HeapStats.z42，45 → 47；
        // 2026-05-07 add-array-base-class 新增 Array.z42，47 → 48；
        // 2026-05-11 retire-z-codes 新增 InvalidMarshalException.z42，48 → 49。
        // 2026-05-13 add-std-io-directory 新增 z42.io/Directory.z42，z42.io 4 → 5。
        // 2026-05-14 add-z42-time 新增 z42.time 包（TimeSpan/DateTime/Stopwatch），3/3。
        // 2026-05-15 add-platform-os-stdlib 新增 z42.core/Platform.z42（含 OSKind +
        //   ArchKind 两个常量类，2026-05-15 fix-multi-file-static-init 解决了同文件多
        //   class init 的命名冲突）+ OperatingSystem.z42，z42.core 49 → 51。
        // 2026-05-15 add-narrow-int-primitives 新增 Primitives/{I8, I16, U8, U16, U32, U64}.z42
        //   (6 文件)，z42.core 51 → 57。
        // 2026-05-24 add-overflow-divide-by-zero-exceptions 新增 Exceptions/OverflowException.z42 +
        //   Exceptions/DivideByZeroException.z42，z42.core 57 → 59。
        // 2026-05-25 add-gc-oom-exception 新增 Exceptions/OutOfMemoryException.z42，59 → 60。
        // 2026-05-26 add-gc-softref 新增 GC/SoftHandle.z42，z42.core 60 → 61。
        // 如果新增 / 删除 stdlib 文件需同步更新此处。
        var (code2, _, err2) = RunZ42c(libsRoot, "build", "--workspace", "--release");
        code2.Should().Be(0, err2);
        err2.Should().Contain("cached: 61/61");
        err2.Should().Contain("cached: 2/2");
        err2.Should().Contain("cached: 5/5");
        err2.Should().Contain("cached: 3/3");  // z42.time: TimeSpan + DateTime + Stopwatch
    }

    /// <summary>--no-incremental 强制全 fresh。</summary>
    [Fact]
    public void StdlibBuild_NoIncremental_ForcesFreshEvenWithCache()
    {
        string libsRoot = Path.Combine(RepoRoot, "src", "libraries");

        // 先确保 cache 已存在
        var (code1, _, _) = RunZ42c(libsRoot, "build", "--workspace", "--release");
        code1.Should().Be(0);

        // --no-incremental：仍 0/N 即使 cache 存在（39 = z42.core 文件数；
        // 2026-05-03 fix-delegate-reference-equality 新增 DelegateOps.z42，43 → 44；
        // 2026-05-04 D-7-residual 新增 Disposable.z42，44 → 45；2026-05-07
        // reorganize-gc-stdlib 新增 GCHandle.z42 + HeapStats.z42，45 → 47；
        // 2026-05-07 add-array-base-class 新增 Array.z42，47 → 48；
        // 2026-05-11 retire-z-codes 新增 InvalidMarshalException.z42，48 → 49；
        // 2026-05-15 add-platform-os-stdlib 新增 Platform + OperatingSystem，49 → 51；
        // 2026-05-15 add-narrow-int-primitives 新增 I8/I16/U8/U16/U32/U64.z42，51 → 57；
        // 2026-05-24 add-overflow-divide-by-zero-exceptions 新增 OverflowException +
        //   DivideByZeroException，57 → 59；
        // 2026-05-25 add-gc-oom-exception 新增 OutOfMemoryException.z42，59 → 60；
        // 2026-05-26 add-gc-softref 新增 GC/SoftHandle.z42，60 → 61）
        var (code2, _, err2) = RunZ42c(libsRoot, "build", "--workspace", "--release", "--no-incremental");
        code2.Should().Be(0, err2);
        err2.Should().Contain("cached: 0/61");
    }
}
