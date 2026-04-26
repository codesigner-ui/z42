using Z42.Project;

namespace Z42.Driver;

/// <summary>
/// 把 ManifestException（含 "error[WSxxx]:" 或 "warning[WSxxx]:" 前缀的 message）
/// 渲染为终端友好输出。
///
/// 默认开启友好格式（颜色 + 缩进保留）；--no-pretty 输出原始 message。
/// 颜色仅在 stderr 是 TTY 时启用，避免污染管道日志。
/// </summary>
public static class CliOutputFormatter
{
    const string Reset  = "\u001b[0m";
    const string Red    = "\u001b[31m";
    const string Yellow = "\u001b[33m";
    const string Bold   = "\u001b[1m";
    const string Dim    = "\u001b[2m";

    /// <summary>
    /// 格式化输出 ManifestException。pretty=true 启用颜色（如果终端支持）。
    /// </summary>
    public static string Format(ManifestException ex, bool pretty)
    {
        if (!pretty || !UseColors())
            return ex.Message;

        var lines = ex.Message.Split('\n');
        var sb = new System.Text.StringBuilder();

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];

            // 第一行通常是 "error[WSxxx]: title" 或 "warning[WSxxx]: title"
            if (i == 0 && line.StartsWith("error[", StringComparison.Ordinal))
            {
                int closeBracket = line.IndexOf(']');
                if (closeBracket > 0)
                {
                    sb.Append(Red).Append(Bold).Append(line.AsSpan(0, closeBracket + 1)).Append(Reset);
                    sb.Append(line.AsSpan(closeBracket + 1));
                    sb.Append('\n');
                    continue;
                }
            }
            else if (i == 0 && line.StartsWith("warning[", StringComparison.Ordinal))
            {
                int closeBracket = line.IndexOf(']');
                if (closeBracket > 0)
                {
                    sb.Append(Yellow).Append(Bold).Append(line.AsSpan(0, closeBracket + 1)).Append(Reset);
                    sb.Append(line.AsSpan(closeBracket + 1));
                    sb.Append('\n');
                    continue;
                }
            }

            // "  --> file" / "  help: ..." / "  note: ..." 行加 dim 颜色
            if (line.StartsWith("  --> ", StringComparison.Ordinal) ||
                line.StartsWith("  help:", StringComparison.Ordinal) ||
                line.StartsWith("  note:", StringComparison.Ordinal))
            {
                sb.Append(Dim).Append(line).Append(Reset);
            }
            else
            {
                sb.Append(line);
            }

            if (i < lines.Length - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    static bool UseColors()
    {
        if (Console.IsErrorRedirected) return false;
        if (Environment.GetEnvironmentVariable("NO_COLOR") is not null) return false;
        return true;
    }
}
