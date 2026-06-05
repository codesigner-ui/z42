namespace Z42.Project;

/// <summary>
/// Workspace manifest 错误码集合（C1 范围：WS003 / WS005 / WS007 / WS030-WS039）。
/// 复用现有 <see cref="ManifestException"/>，通过 message 携带 WSxxx 码 + 上下文。
/// 后续 C2 增 WS020-024，C3 增 WS010-011，C4 集成 WS001-007 完整路径。
/// </summary>
public static class Z42Errors
{
    // ── C1 错误码常量 ─────────────────────────────────────────────────────────

    public const string WS003 = "WS003";  // ForbiddenSectionInMember
    public const string WS005 = "WS005";  // AmbiguousManifest
    public const string WS007 = "WS007";  // OrphanMember (warning)
    public const string WS030 = "WS030";  // InvalidWorkspaceFileName
    public const string WS031 = "WS031";  // InvalidDefaultMembers
    public const string WS032 = "WS032";  // WorkspaceFieldNotFound
    public const string WS033 = "WS033";  // InvalidWorkspaceProjectField
    public const string WS034 = "WS034";  // WorkspaceDependencyNotFound
    public const string WS035 = "WS035";  // LegacyWorkspaceVersionSyntax
    public const string WS036 = "WS036";  // RootManifestMustBeVirtual
    public const string WS037 = "WS037";  // UnknownTemplateVariable
    public const string WS038 = "WS038";  // InvalidTemplateSyntax
    public const string WS039 = "WS039";  // TemplateVariableNotAllowed

    // ── C2 错误码（include 机制）────────────────────────────────────────────
    public const string WS020 = "WS020";  // CircularInclude
    public const string WS021 = "WS021";  // ForbiddenSectionInPreset
    public const string WS022 = "WS022";  // IncludeTooDeep
    public const string WS023 = "WS023";  // IncludePathNotFound
    public const string WS024 = "WS024";  // IncludePathNotAllowed

    // ── C3 错误码（policy 与集中产物）───────────────────────────────────────
    public const string WS010 = "WS010";  // PolicyViolation
    public const string WS011 = "WS011";  // PolicyFieldPathNotFound

    // ── C4a 错误码（workspace 编译运行时）────────────────────────────────────
    public const string WS001 = "WS001";  // DuplicateMemberName
    public const string WS002 = "WS002";  // ExcludedMemberSelected
    public const string WS006 = "WS006";  // CircularDependency

    // ── Project manifest hygiene warnings（add-manifest-hygiene-warnings, 2026-06-04）─
    public const string WS008 = "WS008";  // UnknownManifestKey (warning)
    public const string WS009 = "WS009";  // RedundantEntryKey (warning)

    // ── tests/bench manifest config（add-tests-bench-manifest-config, 2026-06-06）─
    public const string WS012 = "WS012";  // TestDepInProductionDeps (warning)
    public const string WS040 = "WS040";  // TestEntryMissingName (error)
    public const string WS041 = "WS041";  // TestEntryMissingSrc (error)
    public const string WS042 = "WS042";  // TestBenchEntryDuplicateName (error)
    public const string WS043 = "WS043";  // TestEntrySrcNotFound (error)

    // ── 工厂方法 ──────────────────────────────────────────────────────────────

    public static ManifestException ForbiddenSectionInMember(string memberPath, string sectionName) =>
        new($"error[{WS003}]: forbidden section '[{sectionName}]' in member manifest\n" +
            $"  --> {memberPath}\n" +
            $"  note: '[workspace.*]', '[policy]' and '[profile.*]' may only appear in 'z42.workspace.toml'");

    public static ManifestException AmbiguousManifest(string dir, IEnumerable<string> files) =>
        new($"error[{WS005}]: multiple manifest files in same directory\n" +
            $"  --> {dir}\n" +
            $"  files: {string.Join(", ", files.Select(Path.GetFileName))}");

    public static ManifestException OrphanMember(string manifestPath, string workspaceRoot) =>
        new($"warning[{WS007}]: manifest is inside workspace tree but not matched by any 'members' pattern\n" +
            $"  --> {manifestPath}\n" +
            $"  workspace: {workspaceRoot}\n" +
            $"  help: add to 'members' or 'exclude' in z42.workspace.toml");

    public static ManifestException InvalidWorkspaceFileName(string path) =>
        new($"error[{WS030}]: '[workspace]' section is only allowed in files named 'z42.workspace.toml'\n" +
            $"  --> {path}");

    public static ManifestException InvalidDefaultMembers(IEnumerable<string> unknown, IEnumerable<string> known) =>
        new($"error[{WS031}]: 'default-members' references unknown members: {string.Join(", ", unknown)}\n" +
            $"  known members: {string.Join(", ", known)}");

    public static ManifestException WorkspaceFieldNotFound(string memberPath, string fieldName) =>
        new($"error[{WS032}]: member references workspace shared field that is not declared\n" +
            $"  --> {memberPath}\n" +
            $"  field: '{fieldName}'\n" +
            $"  help: add '{fieldName}' to '[workspace.project]' in z42.workspace.toml");

    public static ManifestException InvalidWorkspaceProjectField(string fieldName, string expectedType, string actualType) =>
        new($"error[{WS033}]: '[workspace.project].{fieldName}' has wrong type\n" +
            $"  expected: {expectedType}\n" +
            $"  actual:   {actualType}");

    public static ManifestException WorkspaceProjectFieldNotShareable(string fieldName, string allowedFields) =>
        new($"error[{WS033}]: '[project].{fieldName}' may not be shared via workspace; member must declare it directly\n" +
            $"  shareable fields: {allowedFields}");

    public static ManifestException WorkspaceDependencyNotFound(string memberPath, string depName) =>
        new($"error[{WS034}]: member references workspace dependency that is not declared\n" +
            $"  --> {memberPath}\n" +
            $"  dependency: '{depName}'\n" +
            $"  help: add '{depName}' to '[workspace.dependencies]' in z42.workspace.toml");

    public static ManifestException LegacyWorkspaceVersionSyntax(string memberPath, string depName) =>
        new($"error[{WS035}]: legacy 'version = \"workspace\"' syntax is no longer supported\n" +
            $"  --> {memberPath}\n" +
            $"  dependency: '{depName}'\n" +
            $"  help: use '\"{depName}\".workspace = true' or '\"{depName}\" = {{ workspace = true }}' instead");

    public static ManifestException RootManifestMustBeVirtual(string rootPath) =>
        new($"error[{WS036}]: 'z42.workspace.toml' must be a virtual manifest (no '[project]' section)\n" +
            $"  --> {rootPath}\n" +
            $"  help: move '[project]' content into a separate member directory (e.g. apps/<name>/<name>.z42.toml)");

    public static ManifestException UnknownTemplateVariable(string filePath, string fieldPath, string varName)
    {
        string note = varName.StartsWith("env:")
            ? "\n  note: '${env:...}' is reserved for a future version, not supported in C1"
            : "\n  note: only 'workspace_dir', 'member_dir', 'member_name', 'profile' are recognized";
        return new($"error[{WS037}]: unknown template variable '${{{varName}}}'\n" +
            $"  --> {filePath}\n" +
            $"  field: {fieldPath}{note}");
    }

    public static ManifestException InvalidTemplateSyntax(string filePath, string fieldPath, string reason) =>
        new($"error[{WS038}]: invalid template syntax in path field\n" +
            $"  --> {filePath}\n" +
            $"  field:  {fieldPath}\n" +
            $"  reason: {reason}");

    public static ManifestException TemplateVariableNotAllowed(string filePath, string fieldPath, IReadOnlyList<string> allowedFields) =>
        new($"error[{WS039}]: template variable '${{...}}' is not allowed in this field\n" +
            $"  --> {filePath}\n" +
            $"  field: {fieldPath}\n" +
            $"  allowed in: {string.Join(", ", allowedFields)}");

    // ── C2 工厂方法（include 机制）──────────────────────────────────────────

    public static ManifestException CircularInclude(IReadOnlyList<string> cycle) =>
        new($"error[{WS020}]: circular include detected\n" +
            $"  cycle: {string.Join(" -> ", cycle)}");

    public static ManifestException ForbiddenSectionInPreset(string presetPath, string sectionName) =>
        new($"error[{WS021}]: forbidden section '[{sectionName}]' in preset file\n" +
            $"  --> {presetPath}\n" +
            $"  note: presets may not contain '[workspace.*]', '[policy]', '[profile.*]', '[project].name', or '[project].entry'");

    public static ManifestException IncludeTooDeep(IReadOnlyList<string> chain, int limit) =>
        new($"error[{WS022}]: include nesting depth exceeds limit ({limit})\n" +
            $"  chain: {string.Join(" -> ", chain)}");

    public static ManifestException IncludePathNotFound(string declaredIn, string includePath, string resolvedPath) =>
        new($"error[{WS023}]: include path not found\n" +
            $"  declared in: {declaredIn}\n" +
            $"  include:     {includePath}\n" +
            $"  resolved:    {resolvedPath}");

    public static ManifestException IncludePathNotAllowed(string declaredIn, string includePath, string reason) =>
        new($"error[{WS024}]: include path not allowed\n" +
            $"  declared in: {declaredIn}\n" +
            $"  include:     {includePath}\n" +
            $"  reason:      {reason}");

    // ── C3 工厂方法（policy 与集中产物）─────────────────────────────────────

    public static ManifestException PolicyViolation(
        string fieldPath,
        object? lockedValue,
        object? memberValue,
        string lockedFromFile,
        string memberFromFile) =>
        new($"error[{WS010}]: policy violation: field '{fieldPath}' is locked by workspace\n" +
            $"  --> {memberFromFile}\n" +
            $"  member sets:    {memberValue ?? "null"}\n" +
            $"  workspace lock: {lockedValue ?? "null"} (at {lockedFromFile})\n" +
            $"  help: remove this line or align value with workspace policy");

    public static ManifestException PolicyFieldPathNotFound(string fieldPath, string? suggestion)
    {
        string help = suggestion is null ? "" : $"\n  help: did you mean '{suggestion}'?";
        return new($"error[{WS011}]: policy field path '{fieldPath}' is not a known field{help}");
    }

    // ── C4a 工厂方法（workspace 编译运行时）─────────────────────────────────

    public static ManifestException DuplicateMemberName(string name, IEnumerable<string> paths) =>
        new($"error[{WS001}]: duplicate member name '{name}'\n" +
            $"  members:\n" +
            string.Join("\n", paths.Select(p => $"    {p}")));

    public static ManifestException ExcludedMemberSelected(string name) =>
        new($"error[{WS002}]: member '{name}' is both selected (-p) and excluded (--exclude)\n" +
            $"  help: remove conflicting flag");

    public static ManifestException CircularDependency(IReadOnlyList<string> cycle) =>
        new($"error[{WS006}]: circular dependency between workspace members\n" +
            $"  cycle: {string.Join(" -> ", cycle)}");

    // ── Project manifest hygiene factory methods ─────────────────────────────

    public static ManifestException UnknownManifestKey(
        string manifestPath, string section, string key, string? suggestion = null)
    {
        string help = suggestion is null
            ? "\n  help: remove the line, or check the docs for recognized keys"
            : $"\n  help: did you mean '{suggestion}'?";
        return new($"warning[{WS008}]: unknown key '{key}' in [{section}]\n" +
            $"  --> {manifestPath}{help}");
    }

    public static ManifestException RedundantEntryKey(
        string manifestPath, string section, string explicitEntry) =>
        new($"warning[{WS009}]: [{section}].entry = \"{explicitEntry}\" is redundant\n" +
            $"  --> {manifestPath}\n" +
            $"  note: auto-detect would resolve to the same Main()\n" +
            $"  help: remove this line — z42c finds Main() automatically when unambiguous");

    // ── tests/bench manifest config factory methods ──────────────────────────

    public static ManifestException TestDepInProductionDeps(
        string manifestPath, string depName, string suggestedSection) =>
        new($"warning[{WS012}]: test-only dep '{depName}' in [dependencies]\n" +
            $"  --> {manifestPath}\n" +
            $"  note: this dep will be embedded in the release zpkg metadata even though it's only needed for tests/bench\n" +
            $"  help: move '{depName}' to [{suggestedSection}.dependencies] to keep release artifacts clean");

    public static ManifestException TestEntryMissingName(
        string manifestPath, string entryKind, int entryIndex) =>
        new($"error[{WS040}]: [[{entryKind}]] entry #{entryIndex} missing required field 'name'\n" +
            $"  --> {manifestPath}\n" +
            $"  help: every [[{entryKind}]] block must declare a unique 'name' string");

    public static ManifestException TestEntryMissingSrc(
        string manifestPath, string entryKind, string entryName) =>
        new($"error[{WS041}]: [[{entryKind}]] '{entryName}' missing required field 'src'\n" +
            $"  --> {manifestPath}\n" +
            $"  help: declare 'src = \"<path/to/entry.z42>\"' relative to the package root");

    public static ManifestException TestBenchEntryDuplicateName(
        string manifestPath, string entryKind, string entryName) =>
        new($"error[{WS042}]: duplicate [[{entryKind}]] name '{entryName}'\n" +
            $"  --> {manifestPath}\n" +
            $"  help: each [[{entryKind}]] block must have a unique 'name' (used for filter + output zpkg naming)");

    public static ManifestException TestEntrySrcNotFound(
        string manifestPath, string entryKind, string entryName, string srcPath, string resolvedPath) =>
        new($"error[{WS043}]: [[{entryKind}]] '{entryName}'.src path does not exist\n" +
            $"  --> {manifestPath}\n" +
            $"  declared:  {srcPath}\n" +
            $"  resolved:  {resolvedPath}");
}
