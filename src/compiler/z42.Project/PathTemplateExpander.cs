using System.Text;

namespace Z42.Project;

/// <summary>
/// 路径模板变量展开器（C1 D8 决策）。
///
/// 4 个内置只读变量：
///   - ${workspace_dir} : workspace 根绝对路径
///   - ${member_dir}    : 当前 member 目录绝对路径
///   - ${member_name}   : 当前 member 的 [project].name
///   - ${profile}       : 当前激活的 profile 名（debug / release / 自定义）
///
/// 语法规则：
///   - 占位 ${name}（${env:NAME} 暂不支持，报 WS037）
///   - 字面量 $ 写 $$（展开为 $）
///   - 嵌套不允许（${a${b}} 报 WS038）
///   - 未闭合报 WS038
///
/// 字段白名单由调用方控制：仅 path 字段（include / out_dir / cache_dir / dependencies.path /
/// sources.include|exclude）允许变量；其他字段（version / name / kind 等标量）扫到 ${ 即报 WS039。
/// </summary>
public sealed class PathTemplateExpander
{
    /// <summary>
    /// 4 个内置变量值的来源容器。
    /// </summary>
    public sealed record Context(
        string WorkspaceDir,
        string MemberDir,
        string MemberName,
        string Profile);

    /// <summary>路径字段允许变量替换；标量字段不允许（出现 ${ 即报 WS039）。</summary>
    public enum FieldKind { Path, Scalar }

    /// <summary>白名单字段路径（用于 WS039 错误信息提示）。</summary>
    public static readonly IReadOnlyList<string> AllowedFieldPaths = new[]
    {
        "include[]",
        "[workspace.build].out_dir",
        "[workspace.build].cache_dir",
        "[workspace.dependencies].*.path",
        "[dependencies].*.path",
        "[sources].include[]",
        "[sources].exclude[]",
    };

    /// <summary>
    /// 展开模板字符串。kind=Scalar 时若发现 ${ 立即报 WS039；
    /// kind=Path 时按变量表展开，未知变量报 WS037，语法错误报 WS038。
    /// </summary>
    /// <param name="template">原始模板字符串</param>
    /// <param name="ctx">变量值上下文</param>
    /// <param name="filePath">所在 manifest 文件路径（用于错误信息）</param>
    /// <param name="fieldPath">字段路径（用于错误信息，如 "[project].version"）</param>
    /// <param name="kind">字段类型决定是否允许变量</param>
    public string Expand(string template, Context ctx, string filePath, string fieldPath, FieldKind kind)
    {
        if (template.Length == 0) return template;

        // 标量字段：扫到 ${ 立即报错（不需要完整解析）
        if (kind == FieldKind.Scalar)
        {
            for (int i = 0; i < template.Length - 1; i++)
            {
                if (template[i] == '$' && template[i + 1] == '{')
                {
                    throw Z42Errors.TemplateVariableNotAllowed(filePath, fieldPath, AllowedFieldPaths);
                }
            }
            return template;
        }

        // Path 字段：单趟扫描 + 展开
        var sb = new StringBuilder(template.Length);
        int pos = 0;
        while (pos < template.Length)
        {
            char c = template[pos];

            if (c == '$')
            {
                // $$ → 字面量 $
                if (pos + 1 < template.Length && template[pos + 1] == '$')
                {
                    sb.Append('$');
                    pos += 2;
                    continue;
                }

                // ${name} 形式
                if (pos + 1 < template.Length && template[pos + 1] == '{')
                {
                    int closeIdx = FindMatchingBrace(template, pos + 2, filePath, fieldPath);
                    string varName = template.Substring(pos + 2, closeIdx - (pos + 2));
                    if (varName.Length == 0)
                    {
                        throw Z42Errors.InvalidTemplateSyntax(filePath, fieldPath, "empty variable name '${}'");
                    }
                    sb.Append(LookupVariable(varName, ctx, filePath, fieldPath));
                    pos = closeIdx + 1;
                    continue;
                }

                // 单独的 $（非 $$、非 ${）→ 语法错误
                throw Z42Errors.InvalidTemplateSyntax(
                    filePath, fieldPath, "stray '$'; use '$$' for a literal '$' or '${name}' for a variable");
            }

            sb.Append(c);
            pos++;
        }

        return sb.ToString();
    }

    /// <summary>从 openPos 开始找匹配的 }；遇到 ${ 嵌套或缺 } 报 WS038。</summary>
    static int FindMatchingBrace(string s, int openPos, string filePath, string fieldPath)
    {
        for (int i = openPos; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '}') return i;
            if (c == '$' && i + 1 < s.Length && s[i + 1] == '{')
            {
                throw Z42Errors.InvalidTemplateSyntax(filePath, fieldPath, "nested '${...${...}...}' is not allowed");
            }
            // 变量名只允许 [a-zA-Z_:0-9]
            if (!IsValidVarChar(c))
            {
                throw Z42Errors.InvalidTemplateSyntax(filePath, fieldPath, $"invalid character '{c}' in variable name");
            }
        }
        throw Z42Errors.InvalidTemplateSyntax(filePath, fieldPath, "unclosed '${' (missing '}')");
    }

    static bool IsValidVarChar(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == ':';

    static string LookupVariable(string name, Context ctx, string filePath, string fieldPath)
    {
        return name switch
        {
            "workspace_dir" => ctx.WorkspaceDir,
            "member_dir"    => ctx.MemberDir,
            "member_name"   => ctx.MemberName,
            "profile"       => ctx.Profile,
            _               => throw Z42Errors.UnknownTemplateVariable(filePath, fieldPath, name),
        };
    }
}
