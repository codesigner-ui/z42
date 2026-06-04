using Z42.Core.Diagnostics;

namespace Z42.Project;

/// <summary>
/// Catalog of <c>WS###</c> workspace / manifest diagnostic codes.
///
/// Mirrors <see cref="DiagnosticCatalog"/> structure (Title / Description /
/// Example) so <c>z42c explain WS###</c> and <c>z42c errors</c> can route
/// across code-spaces uniformly. Source-of-truth for the codes themselves
/// is still <see cref="Z42Errors"/> (constants + factory message strings);
/// this catalog is the documentation surface.
/// </summary>
public static class WorkspaceCatalog
{
    public static readonly IReadOnlyDictionary<string, DiagnosticEntry> All =
        new Dictionary<string, DiagnosticEntry>
    {
        // ── WS001-007: Workspace runtime ─────────────────────────────────────

        [Z42Errors.WS001] = new(
            "Duplicate workspace member name",
            "Two or more member directories were resolved to the same logical " +
            "name (the project name from each member's manifest). Workspace " +
            "build cannot disambiguate dependencies between same-named members.",
            "// apps/foo/foo.z42.toml + libs/foo/foo.z42.toml\n" +
            "// both declare project.name = 'foo' → WS001"),

        [Z42Errors.WS002] = new(
            "Member both selected and excluded",
            "A member name appeared in both `-p <name>` (selected for build) " +
            "and `--exclude <name>` (excluded). The flags are mutually exclusive " +
            "for any single member.",
            "z42c build -p alpha --exclude alpha   // WS002"),

        [Z42Errors.WS003] = new(
            "Forbidden section in member manifest",
            "Section `[workspace.*]`, `[policy]`, or `[profile.*]` appeared in a " +
            "member manifest. These are workspace-only sections — they may only " +
            "appear in the root `z42.workspace.toml`.",
            "// libs/foo/foo.z42.toml\n" +
            "[workspace.dependencies]   # → WS003: forbidden in member"),

        [Z42Errors.WS005] = new(
            "Ambiguous manifest in same directory",
            "Two manifest files coexist in one directory (e.g. both " +
            "`<name>.z42.toml` and `z42.workspace.toml`). The build cannot " +
            "decide which to use.",
            "// dir/{foo.z42.toml, z42.workspace.toml} → WS005"),

        [Z42Errors.WS006] = new(
            "Circular dependency between workspace members",
            "Member dependency graph contains a cycle. Refactor to break the " +
            "cycle (extract shared API into a third member, or use interfaces).",
            "// alpha → beta → gamma → alpha → ... → WS006"),

        [Z42Errors.WS007] = new(
            "Orphan manifest in workspace tree (warning)",
            "A manifest file lives inside the workspace directory tree but does " +
            "not match any pattern in the root `members` glob list. The workspace " +
            "build will not pick it up. Add the directory to `members` or " +
            "`exclude` to make the omission explicit.",
            "// libs/forgotten/forgotten.z42.toml not in members[] → WS007"),

        [Z42Errors.WS008] = new(
            "Unknown key in project manifest (warning)",
            "A key in a `.z42.toml` section is not in the recognized set for " +
            "that section. Common causes: typo, key from an older spec version, " +
            "or copy-paste from a workspace manifest. When the key is within " +
            "edit distance 2 of a known key, the warning suggests the likely " +
            "correction. add-manifest-hygiene-warnings (2026-06-04).",
            "// [project] entrypoint = \"Hello.Main\" → WS008 (did you mean 'entry'?)"),

        [Z42Errors.WS009] = new(
            "Redundant [project].entry / [[exe]].entry (warning)",
            "The explicit `entry` value matches what z42c's auto-detect " +
            "would have resolved unaided (the only `Main()` in compiled " +
            "units). Remove the line to keep the manifest minimal — " +
            "auto-detect kicks in when `entry` is omitted, and reports " +
            "an unambiguous error if there's no `Main()` or more than one. " +
            "add-manifest-hygiene-warnings (2026-06-04).",
            "// [project] entry = \"App.Main\" but App.Main is the only Main → WS009"),

        // ── WS010-011: Policy ────────────────────────────────────────────────

        [Z42Errors.WS010] = new(
            "Policy violation — locked field overridden",
            "A member manifest set a field that the workspace `[policy]` table " +
            "marked locked. Locked fields must either be omitted from members " +
            "or set to a value that matches the workspace lock.",
            "// z42.workspace.toml: [policy] build.out_dir = 'dist'\n" +
            "// libs/foo/foo.z42.toml: [build] out_dir = 'other' → WS010"),

        [Z42Errors.WS011] = new(
            "Policy field path not found",
            "A field path listed in `[policy]` does not refer to any known " +
            "manifest field. Check the spelling against the manifest schema.",
            "[policy]\nbuilds.out_dir = '...'   # → WS011: did you mean 'build.out_dir'?"),

        // ── WS020-024: Include mechanism ─────────────────────────────────────

        [Z42Errors.WS020] = new(
            "Circular include detected",
            "Preset include chain contains a cycle. The compiler refuses to " +
            "resolve recursive includes — break the cycle by inlining or " +
            "splitting the shared bits into a third preset.",
            "presets/a.toml includes b.toml includes a.toml → WS020"),

        [Z42Errors.WS021] = new(
            "Forbidden section in preset",
            "Preset files cannot contain `[workspace.*]`, `[policy]`, " +
            "`[profile.*]`, `[project].name`, or `[project].entry`. Presets " +
            "are merge-fragments, not full manifests.",
            "presets/lint.toml: [project] name = 'foo' → WS021"),

        [Z42Errors.WS022] = new(
            "Include nesting depth exceeds limit",
            "Include chain reached the hard depth limit (default 8). This is " +
            "almost always a sign of accidental recursion; if intentional, " +
            "flatten the hierarchy.",
            "include = ['a.toml']  # a → b → c → ... 8 deep → WS022"),

        [Z42Errors.WS023] = new(
            "Include path not found",
            "An `include` directive references a file that does not exist on " +
            "disk relative to the declaring manifest's directory.",
            "include = ['preset/missing.toml']   # → WS023"),

        [Z42Errors.WS024] = new(
            "Include path not allowed",
            "An `include` path resolves outside the workspace tree, or to a " +
            "file with a disallowed extension. Includes are restricted to " +
            "`*.toml` inside the workspace.",
            "include = ['/etc/passwd']           # → WS024"),

        // ── WS030-039: Workspace manifest schema ─────────────────────────────

        [Z42Errors.WS030] = new(
            "Invalid workspace file name",
            "A `[workspace]` section appeared in a file other than " +
            "`z42.workspace.toml`. The workspace section is reserved for the " +
            "well-known root file name.",
            "// libs/foo/foo.z42.toml\n[workspace]   # → WS030"),

        [Z42Errors.WS031] = new(
            "Invalid `default-members`",
            "`[workspace].default-members` references one or more names that " +
            "do not match any resolved member.",
            "default-members = ['alpha', 'typo']   # 'typo' not a member → WS031"),

        [Z42Errors.WS032] = new(
            "Workspace shared field not declared",
            "A member used `<field>.workspace = true` to inherit a value from " +
            "the workspace, but `[workspace.project]` does not declare that " +
            "field.",
            "// libs/foo/foo.z42.toml: edition.workspace = true\n" +
            "// z42.workspace.toml: [workspace.project] (no edition) → WS032"),

        [Z42Errors.WS033] = new(
            "Workspace project field has wrong type or is not shareable",
            "A `[workspace.project]` field declared the wrong type for its " +
            "name, or a member tried to inherit a field that is not in the " +
            "shareable set (e.g. `name`, `entry` are member-specific).",
            "[workspace.project]\nversion = 42   # version must be string → WS033"),

        [Z42Errors.WS034] = new(
            "Workspace dependency not declared",
            "A member used `<dep>.workspace = true` to pin a dependency to " +
            "the workspace's centralized version, but `[workspace.dependencies]` " +
            "does not list that dependency.",
            "// foo.z42.toml: [dependencies] bar.workspace = true\n" +
            "// z42.workspace.toml: [workspace.dependencies] (no bar) → WS034"),

        [Z42Errors.WS035] = new(
            "Legacy `version = \"workspace\"` syntax",
            "The legacy string-form of inheriting a workspace dependency " +
            "version is no longer supported. Use the explicit table form " +
            "`<dep>.workspace = true` instead.",
            "[dependencies] bar = { version = 'workspace' }   # → WS035"),

        [Z42Errors.WS036] = new(
            "Root `z42.workspace.toml` must be virtual",
            "The root workspace manifest cannot itself be a buildable project. " +
            "Move any `[project]` content into a separate member directory.",
            "// z42.workspace.toml: [project] name = 'root'   # → WS036"),

        [Z42Errors.WS037] = new(
            "Unknown template variable in path field",
            "A `${...}` template variable in a path field references an " +
            "unknown name. Only `workspace_dir`, `member_dir`, `member_name`, " +
            "and `profile` are recognized in C1.",
            "[build] out_dir = '${typo}/dist'   # → WS037"),

        [Z42Errors.WS038] = new(
            "Invalid template syntax",
            "A `${...}` template is malformed (e.g. unclosed brace, empty name).",
            "out_dir = '${unclosed/dist'   # → WS038"),

        [Z42Errors.WS039] = new(
            "Template variable not allowed in this field",
            "Template variables are restricted to a fixed allow-list of fields " +
            "(primarily path-style fields). The field in question is not on " +
            "that list.",
            "[project] name = '${member_name}'   # → WS039"),
    };

    public static DiagnosticEntry? TryGet(string code) =>
        All.TryGetValue(code, out var e) ? e : null;

    /// Register this catalog with the central <see cref="DiagnosticCatalog"/>
    /// so `z42c explain WS###` and `z42c errors` see workspace codes too.
    /// Invoked automatically at type load via <see cref="ModuleInitializer"/>;
    /// idempotent so callers may force-register without double-counting.
    private static bool _registered;
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        DiagnosticCatalog.RegisterExternal(TryGet, () => All);
    }
}

/// <summary>
/// Module initializer wiring <see cref="WorkspaceCatalog"/> into the central
/// diagnostic registry the moment Z42.Project loads. Driver / tests don't
/// need to call <see cref="WorkspaceCatalog.Register"/> manually.
/// </summary>
internal static class ModuleInitializer
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init() => WorkspaceCatalog.Register();
}
