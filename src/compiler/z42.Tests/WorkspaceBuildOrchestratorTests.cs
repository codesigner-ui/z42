using FluentAssertions;
using Z42.Pipeline;
using Z42.Project;

namespace Z42.Tests;

/// 验证 WorkspaceBuildOrchestrator 的成员选择 / 闭包 / 拓扑遍历 / failed/blocked 传播 / WS001/002/006。
public sealed class WorkspaceBuildOrchestratorTests
{
    static ResolvedManifest M(string name, string root, params string[] deps)
    {
        var resolvedDeps = deps.Select(d => new ResolvedDependency(d, "*", null, false, true)).ToList();
        return new ResolvedManifest(
            MemberName:    name,
            Kind:          ProjectKind.Lib,
            Entry:         null,
            Version:       "0.1.0",
            Authors:       Array.Empty<string>(),
            License:       null,
            Description:   null,
            Pack:          null,
            Sources:       new SourcesSection(["src/**/*.z42"], []),
            Build:         new BuildSection("dist", "interp", true),
            Dependencies:  resolvedDeps,
            Origins:       new Dictionary<string, FieldOrigin>(),
            ManifestPath:  Path.Combine(root, $"{name}/{name}.z42.toml"),
            WorkspaceRoot: root);
    }

    static WorkspaceLoadResult Workspace(params ResolvedManifest[] members) =>
        new(members, Array.Empty<ManifestException>());

    static WorkspaceBuildOrchestrator.BuildOptions Opts(
        bool all = false,
        IReadOnlyList<string>? selected = null,
        IReadOnlyList<string>? excluded = null,
        bool checkOnly = false,
        bool release = false) =>
        new(selected ?? Array.Empty<string>(), excluded ?? Array.Empty<string>(), all, checkOnly, release);

    static WorkspaceBuildOrchestrator MockOrchestrator(Func<ResolvedManifest, int> compileFunc) =>
        new() { CompileMember = (m, _) => compileFunc(m) };

    // ── 成员选择 ──────────────────────────────────────────────────────────

    [Fact]
    public void Build_AllWorkspace_CompilesEveryMember()
    {
        var ws = Workspace(M("a", "/r"), M("b", "/r", "a"), M("c", "/r", "b"));
        var compiled = new List<string>();
        var orch = MockOrchestrator(m => { compiled.Add(m.MemberName); return 0; });

        var report = orch.Build(ws, Array.Empty<string>(), Opts(all: true));
        compiled.Should().BeEquivalentTo(new[] { "a", "b", "c" });
        report.AllSucceeded.Should().BeTrue();
    }

    [Fact]
    public void Build_SelectedWithDependencyClosure()
    {
        var ws = Workspace(M("a", "/r"), M("b", "/r", "a"), M("c", "/r", "b"), M("d", "/r"));
        var compiled = new List<string>();
        var orch = MockOrchestrator(m => { compiled.Add(m.MemberName); return 0; });

        orch.Build(ws, Array.Empty<string>(), Opts(selected: new[] { "c" }));
        compiled.Should().BeEquivalentTo(new[] { "a", "b", "c" });   // d 不编（不在闭包）
    }

    [Fact]
    public void Build_DefaultMembersUsed()
    {
        var ws = Workspace(M("hello", "/r"));
        var compiled = new List<string>();
        var orch = MockOrchestrator(m => { compiled.Add(m.MemberName); return 0; });

        // workspace 的 default-members 是相对路径，需要与 manifest 实际路径一致
        // 简化：让 default-members 含 "hello"（manifestPath = /r/hello/hello.z42.toml → relativePath = "hello"）
        orch.Build(ws, new[] { "hello" }, Opts());
        compiled.Should().BeEquivalentTo(new[] { "hello" });
    }

    // ── 拓扑顺序 ─────────────────────────────────────────────────────────

    [Fact]
    public void Build_TopologicalOrder_UpstreamFirst()
    {
        var ws = Workspace(M("a", "/r"), M("b", "/r", "a"), M("c", "/r", "b"));
        var order = new List<string>();
        var orch = MockOrchestrator(m => { order.Add(m.MemberName); return 0; });

        orch.Build(ws, Array.Empty<string>(), Opts(all: true));
        order.Should().Equal("a", "b", "c");
    }

    // ── 失败传播 ─────────────────────────────────────────────────────────

    [Fact]
    public void Build_UpstreamFailureBlocksDownstream()
    {
        var ws = Workspace(M("a", "/r"), M("b", "/r", "a"), M("c", "/r", "b"));
        var orch = MockOrchestrator(m => m.MemberName == "a" ? 1 : 0);

        var report = orch.Build(ws, Array.Empty<string>(), Opts(all: true));
        report.Failed.Should().BeEquivalentTo(new[] { "a" });
        report.Blocked.Should().BeEquivalentTo(new[] { "b", "c" });
        report.AllSucceeded.Should().BeFalse();
        report.ExitCode.Should().Be(1);
    }

    [Fact]
    public void Build_SiblingMemberStillCompilesAfterFailure()
    {
        // a 失败；b 依赖 a → blocked；c 不依赖 a → 仍编译
        var ws = Workspace(M("a", "/r"), M("b", "/r", "a"), M("c", "/r"));
        var compiled = new HashSet<string>();
        var orch = MockOrchestrator(m =>
        {
            compiled.Add(m.MemberName);
            return m.MemberName == "a" ? 1 : 0;
        });

        var report = orch.Build(ws, Array.Empty<string>(), Opts(all: true));
        compiled.Should().BeEquivalentTo(new[] { "a", "c" });
        report.Failed.Should().BeEquivalentTo(new[] { "a" });
        report.Blocked.Should().BeEquivalentTo(new[] { "b" });
        report.Succeeded.Should().BeEquivalentTo(new[] { "c" });
    }

    // ── 错误码 ───────────────────────────────────────────────────────────

    [Fact]
    public void WS001_DuplicateMemberName()
    {
        // 注意 ResolvedManifest 有同名 → orchestrator 报 WS001
        var ws = Workspace(M("dup", "/r"), M("dup", "/r"));
        var orch = MockOrchestrator(_ => 0);
        var act = () => orch.Build(ws, Array.Empty<string>(), Opts(all: true));
        act.Should().Throw<ManifestException>().WithMessage("*WS001*duplicate member*dup*");
    }

    [Fact]
    public void WS002_ExcludedMemberSelected()
    {
        var ws = Workspace(M("a", "/r"), M("b", "/r"));
        var orch = MockOrchestrator(_ => 0);
        var act = () => orch.Build(ws, Array.Empty<string>(),
            Opts(selected: new[] { "a" }, excluded: new[] { "a" }));
        act.Should().Throw<ManifestException>().WithMessage("*WS002*both selected*excluded*");
    }

    [Fact]
    public void WS006_CircularDependency()
    {
        // a → b → a
        var ws = Workspace(M("a", "/r", "b"), M("b", "/r", "a"));
        var orch = MockOrchestrator(_ => 0);
        var act = () => orch.Build(ws, Array.Empty<string>(), Opts(all: true));
        act.Should().Throw<ManifestException>().WithMessage("*WS006*circular*");
    }

    [Fact]
    public void Build_SelectedMemberNotFound_Throws()
    {
        var ws = Workspace(M("a", "/r"));
        var orch = MockOrchestrator(_ => 0);
        var act = () => orch.Build(ws, Array.Empty<string>(), Opts(selected: new[] { "ghost" }));
        act.Should().Throw<ManifestException>().WithMessage("*ghost*not found*");
    }
}
