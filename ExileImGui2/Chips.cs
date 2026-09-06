using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using SColor = System.Drawing.Color;

namespace ExileImGui2;

/// <summary>
/// Free-string pill list: existing entries as small removable chips, plus one input row to add more.
/// <para>
/// For an unordered set of short strings - keywords, hotstrings, tag lists - that the user builds up.
/// Reach for <see cref="ListEditor"/> instead when a row needs more than its own text.
/// </para>
/// </summary>
public static class Chips
{
    static readonly Dictionary<string, string> _pending = new(); // in-progress add text, per id

    /// <summary>
    /// Draws the chips, wrapping them across the line, then an input row that appends on Enter or "+".
    /// Mutates <paramref name="items"/> in place.
    /// <para>
    /// A blank, duplicate or rejected entry is left sitting in the box rather than silently eaten, so
    /// the user can see what happened.
    /// </para>
    /// </summary>
    /// <param name="id">Scopes both the ImGui ids and the retained in-progress text, so two chip
    /// lists need two ids.</param>
    /// <param name="items">The caller's list. Added to and removed from directly.</param>
    /// <param name="hint">Placeholder text for the add box.</param>
    /// <param name="caseInsensitiveDedup">When true, "fire" will not add alongside "Fire".</param>
    /// <param name="validate">Optional extra rule an entry must pass. Null accepts anything non-blank.</param>
    /// <returns>True the frame something is added or removed.</returns>
    public static bool Draw(string id, List<string> items, string hint = "add...",
        bool caseInsensitiveDedup = true, Func<string, bool> validate = null)
    {
        bool changed = false;
        ImGui.PushID(id);
        var style = ImGui.GetStyle();
        float wrapX = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;

        int toRemove = -1;
        for (int i = 0; i < items.Count; i++)
        {
            ImGui.PushID(i);
            // wrap decided off the width THIS chip will take, checked against where the last one ended -
            // imgui doesn't auto-wrap, and a chip's width isn't known until after it's drawn, so the call
            // has to be made looking backward at the previous item's rect, same as the imgui wrap idiom.
            if (i > 0)
            {
                float lastX2 = ImGui.GetItemRectMax().X;
                float nextX2 = lastX2 + style.ItemSpacing.X + ChipWidth(items[i]);
                if (nextX2 < wrapX) ImGui.SameLine();
            }
            if (DrawChip(items[i])) toRemove = i;
            ImGui.PopID();
        }
        if (toRemove >= 0) { items.RemoveAt(toRemove); changed = true; }

        var pending = _pending.TryGetValue(id, out var p) ? p : "";
        if (items.Count > 0) ImGui.Spacing();
        ImGui.SetNextItemWidth(160f);
        bool submit = ImGui.InputTextWithHint("##add", hint, ref pending, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        submit |= ImGui.SmallButton("+");
        _pending[id] = pending;

        if (submit && ShouldAdd(pending, items, caseInsensitiveDedup, validate))
        {
            items.Add(pending);
            _pending[id] = "";
            changed = true;
        }

        ImGui.PopID();
        return changed;
    }

    /// <summary>
    /// The pure add-decision, pulled out of <see cref="Draw"/> so it can be asserted without an ImGui
    /// frame: not blank, not already in the list, and passing the caller's validator if there is one.
    /// </summary>
    internal static bool ShouldAdd(string text, List<string> items, bool caseInsensitiveDedup, Func<string, bool> validate)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        bool dup = caseInsensitiveDedup
            ? items.Exists(x => string.Equals(x, text, StringComparison.OrdinalIgnoreCase))
            : items.Contains(text);
        return !dup && (validate == null || validate(text));
    }

    // ---- standalone badges ----

    /// <summary>
    /// The count-to-text rule: past the cap it reads "99+" rather than widening the badge. Pure, so
    /// the rule is testable.
    /// </summary>
    public static string CountText(int n, int max = 99) => n > max ? max + "+" : n.ToString();

    /// <summary>
    /// A small coloured pill of text with no widget behind it, for a status or a tag sitting in a row
    /// that has its own click behaviour. Draw-list only, same reasoning as
    /// <see cref="Controls.IconImage(IntPtr, Vector2, Vector2, float, SColor)"/> - a button here would
    /// eat the row's click.
    /// <para>
    /// Shorter than a frame on purpose, so it reads as a marker rather than something to press. Beside
    /// framed controls, put a <see cref="Controls.AlignMid"/> in front of it.
    /// </para>
    /// </summary>
    /// <param name="color">Fill, or the dot's colour in <paramref name="dot"/> mode. Null uses the
    /// theme accent.</param>
    /// <param name="outline">Draws the ring and keeps the fill empty, for a badge that has to sit on
    /// an already busy row without shouting.</param>
    /// <param name="dot">Colour moves to a small dot and the pill itself takes the theme's frame and
    /// border colours. For a category tag on a busy row, where a fully coloured pill would outshout
    /// the row's own text. Beats <paramref name="outline"/> when several sit in a column together.</param>
    public static void Badge(string text, SColor? color = null, bool outline = false, bool dot = false)
    {
        var t = text ?? "";
        if (t.Length == 0) return;
        var style = ImGui.GetStyle();
        var ts = ImGui.CalcTextSize(t);
        float padX = style.FramePadding.X;
        var size = new Vector2(ts.X + padX * 2 + (dot ? DotR * 2 + DotGap : 0f), ts.Y + style.FramePadding.Y);
        var org = ImGui.GetCursorScreenPos();
        ImGui.Dummy(size);

        var dl = ImGui.GetWindowDrawList();
        SColor fill = color ?? EColor.FromVector4(style.Colors[(int)ImGuiCol.Header]);
        float r = size.Y * 0.5f;
        if (dot)
        {
            dl.AddRectFilled(org, org + size, ImGui.GetColorU32(ImGuiCol.FrameBg), r);
            dl.AddRect(org, org + size, ImGui.GetColorU32(ImGuiCol.Border), r);
            dl.AddCircleFilled(new Vector2(org.X + padX + DotR, org.Y + size.Y * 0.5f), DotR, EColor.U32(fill));
            dl.AddText(org + new Vector2(padX + DotR * 2 + DotGap, (size.Y - ts.Y) * 0.5f),
                ImGui.GetColorU32(ImGuiCol.Text), t);
            return;
        }
        SColor fg;
        if (outline)
        {
            dl.AddRect(org, org + size, EColor.U32(fill), r);
            fg = fill;
        }
        else
        {
            dl.AddRectFilled(org, org + size, EColor.U32(fill), r);
            fg = EColor.Contrast(fill);
        }
        dl.AddText(org + new Vector2(padX, (size.Y - ts.Y) * 0.5f), EColor.U32(fg), t);
    }

    const float DotR = 3f, DotGap = 5f;

    /// <summary>
    /// Width <see cref="Badge"/> will take, so a caller can right-align one in a column before
    /// drawing it. Zero for empty text, which draws nothing.
    /// </summary>
    public static float BadgeWidth(string text, bool dot = false)
    {
        var t = text ?? "";
        if (t.Length == 0) return 0f;
        return ImGui.CalcTextSize(t).X + ImGui.GetStyle().FramePadding.X * 2 + (dot ? DotR * 2 + DotGap : 0f);
    }

    /// <summary>
    /// The same badge holding a number, capped so a runaway count can't stretch the row.
    /// </summary>
    /// <param name="showZero">False draws nothing at 0, which is what a "n problems" badge wants.</param>
    public static void CounterBadge(int count, SColor? color = null, int max = 99, bool showZero = false)
    {
        if (count <= 0 && !showZero) return;
        Badge(CountText(count, max), color);
    }

    // label pill + glued-on "x", grouped so wrap treats the pair as one item. returns true on x-click.
    static bool DrawChip(string label)
    {
        bool remove = false;
        ImGui.BeginGroup();
        using (new EColor.StyleColorScope((ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.FrameBg))))
            ImGui.Button(label + "##lbl");
        ImGui.SameLine(0, 2);
        if (ImGui.SmallButton("x##del")) remove = true;
        ImGui.EndGroup();
        return remove;
    }

    // predicted width of DrawChip's group - close enough for the wrap check, doesn't need to be exact,
    // one chip wrapping a frame early or late is cosmetic only.
    static float ChipWidth(string label)
    {
        var style = ImGui.GetStyle();
        float textW = ImGui.CalcTextSize(label ?? "").X;
        float lblBtnW = textW + style.FramePadding.X * 2;
        float xBtnW = ImGui.CalcTextSize("x").X + style.FramePadding.X * 2;
        return lblBtnW + 2 + xBtnW;
    }
}
