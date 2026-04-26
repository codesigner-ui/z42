using Tomlyn;
using Tomlyn.Model;

namespace Z42.Project;

/// <summary>
/// Workspace member 的 &lt;name&gt;.z42.toml 解析结果。
/// 字段保留"是否引用 workspace 共享"的标记，由 ManifestLoader 在合并阶段展开。
///
/// C1 不允许的段（解析时立即报 WS003）：
///   - [workspace] / [workspace.*]
///   - [policy]
///   - [profile.*]
/// </summary>
public sealed class MemberManifest
{
    public required MemberProject       Project      { get; init; }
    public required SourcesSection?     Sources      { get; init; }   // nullable: null = 未声明
    public required BuildSection?       Build        { get; init; }   // null = 未声明
    public required IReadOnlyList<MemberDependency>? Dependencies { get; init; }  // null = 未声明
    public required IReadOnlyList<string> Include    { get; init; }
    public required string              ManifestPath { get; init; }

    /// <summary>是否为 preset 文件（C2 合并使用；preset 段限制更严）。</summary>
    public bool IsPreset { get; init; }

    // ── 可共享元数据字段（C1 范围）────────────────────────────────────────────
    /// <summary>workspace.project 中允许共享的字段集合（D5 决策）。</summary>
    public static readonly IReadOnlyList<string> ShareableProjectFields = new[]
    {
        "version", "authors", "license", "description",
    };

    /// <summary>从 toml 文件加载 member manifest（不进行 workspace 共享继承，仅原始字段）。</summary>
    public static MemberManifest Load(string tomlPath, bool isPreset = false)
    {
        string toml  = File.ReadAllText(tomlPath);
        var    model = TomlSerializer.Deserialize<TomlTable>(toml)
                       ?? throw new ManifestException($"error: failed to parse {tomlPath}");

        // 段限制：member 不允许 [workspace.*] / [policy] / [profile.*]（C1 WS003 / C2 WS021）
        EnsureNoForbiddenSections(model, tomlPath, isPreset);

        var project = ParseMemberProject(model, tomlPath, isPreset);
        var sources = ParseSourcesNullable(model);
        var build   = ParseOptionalBuild(model);
        var deps    = ParseDependenciesNullable(model, tomlPath);
        var include = ParseIncludeArray(model);

        return new MemberManifest
        {
            Project      = project,
            Sources      = sources,
            Build        = build,
            Dependencies = deps,
            Include      = include,
            ManifestPath = tomlPath,
            IsPreset     = isPreset,
        };
    }

    static void EnsureNoForbiddenSections(TomlTable model, string memberPath, bool isPreset)
    {
        // member 与 preset 都不允许的段
        if (model.ContainsKey("workspace"))
        {
            throw isPreset
                ? Z42Errors.ForbiddenSectionInPreset(memberPath, "workspace")
                : Z42Errors.ForbiddenSectionInMember(memberPath, "workspace");
        }
        if (model.ContainsKey("policy"))
        {
            throw isPreset
                ? Z42Errors.ForbiddenSectionInPreset(memberPath, "policy")
                : Z42Errors.ForbiddenSectionInMember(memberPath, "policy");
        }
        if (model.TryGetValue("profile", out var profileRaw) && profileRaw is TomlTable)
        {
            throw isPreset
                ? Z42Errors.ForbiddenSectionInPreset(memberPath, "profile")
                : Z42Errors.ForbiddenSectionInMember(memberPath, "profile");
        }
    }

    static MemberProject ParseMemberProject(TomlTable model, string tomlPath, bool isPreset)
    {
        // preset 可省略 [project] 段（preset 仅做共享）
        if (!model.TryGetValue("project", out var raw) || raw is not TomlTable t)
        {
            if (isPreset) return MemberProject.Empty();
            throw new ManifestException($"error: [project] section is required in {tomlPath}");
        }

        // preset 不允许 [project] 身份字段（name / entry）
        if (isPreset)
        {
            if (t.ContainsKey("name"))
                throw Z42Errors.ForbiddenSectionInPreset(tomlPath, "project.name");
            if (t.ContainsKey("entry"))
                throw Z42Errors.ForbiddenSectionInPreset(tomlPath, "project.entry");
        }

        // name 由 member 自己声明（不可共享，D5）；缺失从文件名推断（与现行 ProjectManifest 一致）
        string inferred = Path.GetFileName(tomlPath).Replace(".z42.toml", "", StringComparison.OrdinalIgnoreCase);
        string? name   = isPreset ? null : (t.TryGetString("name") ?? inferred);
        string? kindS  = t.TryGetString("kind");
        string? entry  = t.TryGetString("entry");
        bool?  pack    = t.TryGetBool("pack");

        ProjectKind? kind = null;
        if (kindS is not null)
        {
            if (!Enum.TryParse<ProjectKind>(kindS, ignoreCase: true, out var k))
                throw new ManifestException($"error: [project].kind must be 'exe' or 'lib', got '{kindS}'");
            kind = k;
        }

        // 可共享字段：检测是否声明为 .workspace = true
        var version     = ParseShareableField<string>(t, "version", tomlPath);
        var authors     = ParseShareableArrayField(t, "authors", tomlPath);
        var license     = ParseShareableField<string>(t, "license", tomlPath);
        var description = ParseShareableField<string>(t, "description", tomlPath);

        return new MemberProject(name, kind, entry, pack, version, authors, license, description);
    }

    /// <summary>
    /// 解析可共享字段 X：可能是直接值（"0.1.0"）或子表 { workspace = true }。
    /// 不可共享字段（如 name）调用方不要走此路径。
    /// </summary>
    static FieldRef<T>? ParseShareableField<T>(TomlTable t, string key, string tomlPath) where T : class
    {
        if (!t.TryGetValue(key, out var raw)) return null;

        // 子表 { workspace = true } 形式
        if (raw is TomlTable sub)
        {
            if (sub.TryGetValue("workspace", out var ws) && ws is bool wsBool && wsBool)
                return FieldRef<T>.UseWorkspace();
            // 非预期子表
            throw Z42Errors.InvalidWorkspaceProjectField(key, "string or { workspace = true }", "table");
        }

        // 直接值（仅 string 支持；其他类型由调用方扩展）
        if (typeof(T) == typeof(string) && raw is string s)
            return FieldRef<T>.Direct((T)(object)s);

        throw Z42Errors.InvalidWorkspaceProjectField(key, typeof(T).Name, raw.GetType().Name);
    }

    static FieldRef<IReadOnlyList<string>>? ParseShareableArrayField(TomlTable t, string key, string tomlPath)
    {
        if (!t.TryGetValue(key, out var raw)) return null;

        if (raw is TomlTable sub)
        {
            if (sub.TryGetValue("workspace", out var ws) && ws is bool wsBool && wsBool)
                return FieldRef<IReadOnlyList<string>>.UseWorkspace();
            throw Z42Errors.InvalidWorkspaceProjectField(key, "string array or { workspace = true }", "table");
        }

        if (raw is TomlArray arr)
            return FieldRef<IReadOnlyList<string>>.Direct(arr.OfType<string>().ToList());

        throw Z42Errors.InvalidWorkspaceProjectField(key, "string array or { workspace = true }", raw.GetType().Name);
    }

    static SourcesSection? ParseSourcesNullable(TomlTable model)
    {
        if (!model.TryGetValue("sources", out var raw) || raw is not TomlTable t)
            return null;
        var include = t.TryGetStringArray("include") ?? ["src/**/*.z42"];
        var exclude = t.TryGetStringArray("exclude") ?? [];
        return new SourcesSection(include, exclude);
    }

    static BuildSection? ParseOptionalBuild(TomlTable model)
    {
        if (!model.TryGetValue("build", out var raw) || raw is not TomlTable t) return null;
        string outDir   = t.TryGetString("out_dir") ?? "dist";
        string mode     = t.TryGetString("mode")    ?? "interp";
        bool   inc      = t.TryGetBool("incremental") ?? true;
        return new BuildSection(outDir, mode, inc);
    }

    static IReadOnlyList<MemberDependency>? ParseDependenciesNullable(TomlTable model, string tomlPath)
    {
        if (!model.TryGetValue("dependencies", out var raw) || raw is not TomlTable t) return null;

        var deps = new List<MemberDependency>();
        foreach (var kv in t)
        {
            string depName = kv.Key;

            // 形式 1：直接字符串版本 "*" / "1.2.0"
            if (kv.Value is string s)
            {
                // 检测旧语法 version = "workspace"（实际上 string 形式不能有 version 字段，这里跳过）
                deps.Add(new MemberDependency(depName, false, s, null, false));
                continue;
            }

            // 形式 2：子表
            if (kv.Value is TomlTable sub)
            {
                bool? legacyVersion = sub.TryGetValue("version", out var verRaw)
                                       && verRaw is string verStr
                                       && verStr == "workspace"
                                       ? true : null;
                if (legacyVersion is true)
                    throw Z42Errors.LegacyWorkspaceVersionSyntax(tomlPath, depName);

                bool useWs = sub.TryGetValue("workspace", out var ws) && ws is bool b && b;
                string? version = sub.TryGetString("version");
                string? path    = sub.TryGetString("path");
                bool optional   = sub.TryGetBool("optional") ?? false;

                deps.Add(new MemberDependency(depName, useWs, version, path, optional));
                continue;
            }

            throw new ManifestException($"error: invalid dependency '{depName}' in {tomlPath}");
        }
        return deps;
    }

    static IReadOnlyList<string> ParseIncludeArray(TomlTable model)
    {
        if (!model.TryGetValue("include", out var raw)) return [];
        if (raw is not TomlArray arr) return [];
        return arr.OfType<string>().ToList();
    }
}

/// <summary>Member 的 [project] 段，含可共享字段的引用标记。preset 中 Name/Kind 可为 null。</summary>
public sealed record MemberProject(
    string?                                         Name,
    ProjectKind?                                    Kind,
    string?                                         Entry,
    bool?                                           Pack,
    FieldRef<string>?                               Version,
    FieldRef<IReadOnlyList<string>>?                Authors,
    FieldRef<string>?                               License,
    FieldRef<string>?                               Description)
{
    public static MemberProject Empty() => new(null, null, null, null, null, null, null, null);
}

/// <summary>
/// 字段值引用：要么直接值（Value 非 null），要么引用 workspace 共享（UsesWorkspace = true）。
/// </summary>
public sealed record FieldRef<T> where T : class
{
    public T?   Value          { get; init; }
    public bool UsesWorkspace  { get; init; }

    public static FieldRef<T> Direct(T value)         => new() { Value = value, UsesWorkspace = false };
    public static FieldRef<T> UseWorkspace()          => new() { Value = null,  UsesWorkspace = true  };
}

/// <summary>Member 中声明的依赖项（含 workspace 引用标记）。</summary>
public sealed record MemberDependency(
    string  Name,
    bool    UsesWorkspace,
    string? DirectVersion,
    string? DirectPath,
    bool    Optional);
