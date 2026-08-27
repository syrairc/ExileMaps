using System;
using System.Collections.Generic;
using ImGuiNET;

namespace ExileImGui2;

/// <summary>
/// Shared string helpers. Mostly for the foreground-drawlist widgets (overlays, toasts, richtext),
/// plus the one filter rule every searchable control in here agrees on.
/// </summary>
public static class Text
{
    /// <summary>
    /// The case-insensitive contains rule every picker in this library filters with.
    /// </summary>
    /// <returns>True when <paramref name="text"/> contains <paramref name="filter"/>. An empty or
    /// whitespace filter matches everything; a null text matches nothing but never throws.</returns>
    public static bool Matches(string filter, string text)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        return (text ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Greedy word wrap, for the foreground widgets that don't get ImGui's own wrapping.
    /// </summary>
    /// <param name="text">Text to wrap. Split on spaces only, so a single long word never breaks.</param>
    /// <param name="maxWidth">Pixel budget for the text at <paramref name="scale"/>.</param>
    /// <param name="scale">Draw size over the current font size, since CalcTextSize measures at the latter.</param>
    /// <returns>One entry per line, never empty - an unwrappable input comes back as a single line.</returns>
    public static List<string> Wrap(string text, float maxWidth, float scale)
    {
        var words = (text ?? "").Split(' ');
        var lines = new List<string>();
        var cur = "";
        foreach (var w in words)
        {
            var trial = cur.Length == 0 ? w : cur + " " + w;
            if (cur.Length > 0 && ImGui.CalcTextSize(trial).X * scale > maxWidth)
            {
                lines.Add(cur);
                cur = w;
            }
            else cur = trial;
        }
        if (cur.Length > 0) lines.Add(cur);
        if (lines.Count == 0) lines.Add(text ?? "");
        return lines;
    }
}
