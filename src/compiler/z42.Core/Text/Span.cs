namespace Z42.Core.Text;

/// <summary>
/// Source location range: byte offset [Start, End) with line/column for display.
/// </summary>
public readonly record struct Span(int Start, int End, int Line, int Column, string File = "");
