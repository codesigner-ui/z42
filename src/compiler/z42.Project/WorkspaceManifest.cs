using Tomlyn;
using Tomlyn.Model;

namespace Z42.Project;

/// <summary>
/// z42.workspace.toml 的解析结果。
///
/// 必须以文件名 'z42.workspace.toml' 出现；若 [workspace] 段出现在其他文件名 → WS030。
/// 必须是 virtual manifest（不含 [project] 段）；否则 WS036。
///
/// C1 范围：
///   - [workspace] members / exclude / default-members / resolver
///   - [workspace.project] 共享元数据
///   - [workspace.dependencies] 中央版本声明
///   - [profile.*] 集中声明（C1 仅解析数据；C3 落实"成员不可覆盖" treatment）
///   - [workspace.build] / [policy] 占位字段（C3 实施）
/// </summary>
public sealed class WorkspaceManifest
{
    public required IReadOnlyList<string>             MembersPatterns       { get; init; }
    public required IReadOnlyList<string>             ExcludePatterns       { get; init; }
    public required IReadOnlyList<string>             DefaultMembers        { get; init; }
    public required string                            Resolver              { get; init; }
    public required WorkspaceProjectShared?           WorkspaceProject      { get; init; }
    public required IReadOnlyDictionary<string, WorkspaceDeclaredDep> WorkspaceDependencies { get; init; }
    public required WorkspaceBuildShared              WorkspaceBuild        { get; init; }
    public required IReadOnlyDictionary<string, object> Policy              { get; init; } // C3 用
    public required IReadOnlyDictionary<string, ProfileSection> Profiles    { get; init; }
    public required string                            ManifestPath          { get; init; }
    public required string                            RootDirectory         { get; init; }

    public static WorkspaceManifest Load(string tomlPath)
    {
        // 强制文件名为 z42.workspace.toml（D1 决策；WS030）
        string fileName = Path.GetFileName(tomlPath);
        if (!string.Equals(fileName, "z42.workspace.toml", StringComparison.Ordinal))
        {
            // 文件名不对但若不含 [workspace] 段，让上层判断（这里仅在 caller 已确认是 workspace 文件后调用）
            throw Z42Errors.InvalidWorkspaceFileName(tomlPath);
        }

        string toml  = File.ReadAllText(tomlPath);
        var    model = TomlSerializer.Deserialize<TomlTable>(toml)
                       ?? throw new ManifestException($"error: failed to parse {tomlPath}");

        // virtual manifest 检查（D2 决策；WS036）
        if (model.ContainsKey("project"))
            throw Z42Errors.RootManifestMustBeVirtual(tomlPath);

        // [workspace] 必须存在
        if (!model.TryGetValue("workspace", out var wsRaw) || wsRaw is not TomlTable ws)
            throw new ManifestException($"error: [workspace] section is required in {tomlPath}");

        var members  = ws.TryGetStringArray("members") ?? Array.Empty<string>();
        var excludes = ws.TryGetStringArray("exclude") ?? Array.Empty<string>();
        var defaults = ws.TryGetStringArray("default-members") ?? Array.Empty<string>();
        string resolver = ws.TryGetString("resolver") ?? "1";

        var wsProject  = ParseWorkspaceProject(model, tomlPath);
        var wsDeps     = ParseWorkspaceDependencies(model, tomlPath);
        var wsBuild    = ParseWorkspaceBuild(model);
        var policy     = ParsePolicy(model);
        var profiles   = ParseProfiles(model);

        return new WorkspaceManifest
        {
            MembersPatterns       = members,
            ExcludePatterns       = excludes,
            DefaultMembers        = defaults,
            Resolver              = resolver,
            WorkspaceProject      = wsProject,
            WorkspaceDependencies = wsDeps,
            WorkspaceBuild        = wsBuild,
            Policy                = policy,
            Profiles              = profiles,
            ManifestPath          = tomlPath,
            RootDirectory         = Path.GetDirectoryName(Path.GetFullPath(tomlPath))!,
        };
    }

    static WorkspaceProjectShared? ParseWorkspaceProject(TomlTable model, string tomlPath)
    {
        if (!model.TryGetValue("workspace", out var wsRaw) || wsRaw is not TomlTable ws) return null;
        if (!ws.TryGetValue("project", out var raw) || raw is not TomlTable t) return null;

        // 不允许的字段（仅 D5 列出的可共享）
        foreach (var kv in t)
        {
            if (!MemberManifest.ShareableProjectFields.Contains(kv.Key))
            {
                throw Z42Errors.WorkspaceProjectFieldNotShareable(
                    kv.Key, string.Join(", ", MemberManifest.ShareableProjectFields));
            }
        }

        string?              version     = t.TryGetString("version");
        IReadOnlyList<string>? authors   = t.TryGetStringArray("authors");
        string?              license     = t.TryGetString("license");
        string?              description = t.TryGetString("description");

        // 类型校验：authors 必须是字符串数组
        if (t.TryGetValue("authors", out var authorsRaw) && authorsRaw is not TomlArray)
            throw Z42Errors.InvalidWorkspaceProjectField("authors", "string[]", authorsRaw.GetType().Name);

        return new WorkspaceProjectShared(version, authors ?? [], license, description);
    }

    static IReadOnlyDictionary<string, WorkspaceDeclaredDep> ParseWorkspaceDependencies(TomlTable model, string tomlPath)
    {
        var result = new Dictionary<string, WorkspaceDeclaredDep>();
        if (!model.TryGetValue("workspace", out var wsRaw) || wsRaw is not TomlTable ws) return result;
        if (!ws.TryGetValue("dependencies", out var raw) || raw is not TomlTable t) return result;

        foreach (var kv in t)
        {
            string name = kv.Key;
            string? version = null;
            string? path = null;

            if (kv.Value is string s)
            {
                version = s;
            }
            else if (kv.Value is TomlTable sub)
            {
                version = sub.TryGetString("version");
                path    = sub.TryGetString("path");
            }
            else
            {
                throw new ManifestException(
                    $"error: invalid [workspace.dependencies].{name} in {tomlPath}");
            }

            result[name] = new WorkspaceDeclaredDep(name, version ?? "*", path);
        }

        return result;
    }

    static WorkspaceBuildShared ParseWorkspaceBuild(TomlTable model)
    {
        if (!model.TryGetValue("workspace", out var wsRaw) || wsRaw is not TomlTable ws)
            return WorkspaceBuildShared.Default;
        if (!ws.TryGetValue("build", out var raw) || raw is not TomlTable t)
            return WorkspaceBuildShared.Default;

        string outDir   = t.TryGetString("out_dir")   ?? "dist";
        string cacheDir = t.TryGetString("cache_dir") ?? ".cache";
        bool inc        = t.TryGetBool("incremental") ?? true;
        string mode     = t.TryGetString("mode")      ?? "interp";

        return new WorkspaceBuildShared(outDir, cacheDir, inc, mode);
    }

    static IReadOnlyDictionary<string, object> ParsePolicy(TomlTable model)
    {
        var result = new Dictionary<string, object>();
        if (!model.TryGetValue("policy", out var raw) || raw is not TomlTable t) return result;

        foreach (var kv in t)
        {
            // C1 阶段仅原始解析；C3 实施字段路径校验与冲突检测
            result[kv.Key] = kv.Value;
        }
        return result;
    }

    static IReadOnlyDictionary<string, ProfileSection> ParseProfiles(TomlTable model)
    {
        var result = new Dictionary<string, ProfileSection>();
        if (!model.TryGetValue("profile", out var raw) || raw is not TomlTable profiles) return result;

        foreach (var kv in profiles)
        {
            if (kv.Value is not TomlTable t) continue;
            string mode  = t.TryGetString("mode")    ?? "interp";
            int    opt   = (int)(t.TryGetLong("optimize") ?? 0);
            bool   debug = t.TryGetBool("debug")     ?? true;
            bool   strip = t.TryGetBool("strip")     ?? false;
            bool?  pack  = t.TryGetBool("pack");
            result[kv.Key] = new ProfileSection(mode, opt, debug, strip, pack);
        }
        return result;
    }
}

/// <summary>workspace 共享元数据（D5 限定字段）。</summary>
public sealed record WorkspaceProjectShared(
    string?               Version,
    IReadOnlyList<string> Authors,
    string?               License,
    string?               Description);

/// <summary>workspace 中央依赖声明。</summary>
public sealed record WorkspaceDeclaredDep(
    string  Name,
    string  Version,
    string? Path);

/// <summary>[workspace.build] 占位字段（C1 解析；C3 实施集中产物布局）。</summary>
public sealed record WorkspaceBuildShared(
    string OutDir,
    string CacheDir,
    bool   Incremental,
    string Mode)
{
    public static WorkspaceBuildShared Default => new("dist", ".cache", true, "interp");
}
