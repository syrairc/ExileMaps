// icon nav: a rail of sections down the left, the selected one's body to the right
using System;
using System.Numerics;
using System.Text;
using ImGuiNET;

namespace ExileImGui2;

/// <summary>
/// One nav entry. <see cref="Body"/> draws the section when it is the selected one.
/// </summary>
public sealed class NavItem
{
    /// <summary>Stable id. This is what a caller sets <see cref="NavState.Active"/> to.</summary>
    public string Key;

    /// <summary>Drawn beside the icon with labels on, and used as the tooltip with them off.</summary>
    public string Label;

    /// <summary>Texture handle. Zero draws the label's initials in a box instead, so a caller with no art still works.</summary>
    public IntPtr Icon;

    /// <summary>Optional selected-state texture. Falls back to <see cref="Icon"/>.</summary>
    public IntPtr IconActive;

    /// <summary>Null means always shown. A hidden item is skipped, and cannot be the active one.</summary>
    public Func<bool> Visible;

    /// <summary>Null means no badge. Whatever this returns is drawn on the item, so keep it to a couple of characters.</summary>
    public Func<string> Badge;

    /// <summary>Returns its changed flag, same contract as <see cref="Windows.TabBar"/>.</summary>
    public Func<bool> Body;
}

/// <summary>
/// Caller-owned nav state. Persist it wherever the panel's other settings live.
/// </summary>
public sealed class NavState
{
    /// <summary>Key of the selected item. An unknown or hidden key falls back to the first visible one.</summary>
    public string Active;

    /// <summary>
    /// Rail: wide with labels, or icon-only with tooltips. One knob rather than two, because it means
    /// the same thing whichever way the nav ends up running.
    /// </summary>
    public bool ShowLabels;
}

/// <summary>
/// Icon-driven section nav. <see cref="Windows.SideTabs"/> with pictures instead of a text list, for a
/// panel that groups a handful of long-lived sections into one window.
/// <para>
/// Draws nothing of its own: every colour comes from <see cref="ImGuiCol"/>, so it tracks whatever
/// style the host sets. Selected is a plain <c>Header</c> fill, exactly what a Selectable draws.
/// </para>
/// </summary>
public static class Nav
{
    /// <summary>
    /// Vertical rail on the left, body pane to the right. The chevron at the rail's foot toggles labels.
    /// <para>
    /// The width snaps rather than animating - immediate mode has no transitions, and lerping it would
    /// make this the one control in here that cares what the frame rate is.
    /// </para>
    /// </summary>
    /// <param name="items">Left-to-right, top-to-bottom order. Hidden entries are skipped in place.</param>
    /// <param name="state">Caller-owned. This writes <see cref="NavState.Active"/> and <see cref="NavState.ShowLabels"/>.</param>
    /// <param name="iconSize">Drawn size of the glyph. Row height follows it, so this is the knob that
    /// makes the rail chunkier - tying it to the font size gave a 15px icon nobody could read.</param>
    /// <returns>The selected body's changed flag, or false when nothing is visible.</returns>
    public static bool Rail(string id, NavItem[] items, NavState state,
        float collapsedWidth = 40f, float expandedWidth = 152f, float iconSize = 20f)
    {
        if (items == null || items.Length == 0 || state == null)
            return false;

        int active = Resolve(items, state);
        if (active < 0)
            return false;

        ImGui.PushID(id);

        // rows sit flush and the rail keeps its own tight padding, so a 40px column is mostly icon.
        // NoScrollbar because a reserved scrollbar eats width the rail does not have - the wheel
        // still scrolls if someone ever hands this more items than fit
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(2f, 2f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        ImGui.BeginChild("##rail", new Vector2(state.ShowLabels ? expandedWidth : collapsedWidth, 0),
            ImGuiChildFlags.Border, ImGuiWindowFlags.NoScrollbar);

        for (int i = 0; i < items.Length; i++)
        {
            var it = items[i];
            if (it == null || (it.Visible != null && !it.Visible()))
                continue;
            if (DrawItem(it, i, i == active, state.ShowLabels, iconSize))
                state.Active = it.Key;
        }

        // push the chevron to the foot. Dummy rather than SetCursorPosY so a rail taller than its
        // window still scrolls instead of drawing the chevron over the last item. exact only because
        // ItemSpacing is zeroed above - with it on, the spacing before the chevron overflows the child
        float slack = ImGui.GetContentRegionAvail().Y - ImGui.GetFrameHeight();
        if (slack > 0f) ImGui.Dummy(new Vector2(0f, slack));
        if (Chevron(state.ShowLabels))
            state.ShowLabels = !state.ShowLabels;

        ImGui.EndChild();
        ImGui.PopStyleVar(2);

        ImGui.SameLine();
        ImGui.BeginChild("##body", Vector2.Zero, ImGuiChildFlags.Border);
        bool changed = items[active].Body?.Invoke() ?? false;
        ImGui.EndChild();

        ImGui.PopID();
        return changed;
    }

    // index of the active item, or the first visible one when the stored key is unknown or went hidden.
    // -1 when nothing is visible at all. Visible() runs here and again in the draw loop - these are
    // cheap predicates, and reading them once would mean allocating a list to remember the answer
    private static int Resolve(NavItem[] items, NavState state)
    {
        int first = -1;
        for (int i = 0; i < items.Length; i++)
        {
            var it = items[i];
            if (it == null || (it.Visible != null && !it.Visible()))
                continue;
            if (first < 0) first = i;
            if (it.Key != null && it.Key == state.Active) return i;
        }
        if (first >= 0) state.Active = items[first].Key;
        return first;
    }

    // one row: full-width hit rect, then everything drawn on top of it. same InvisibleButton-plus-drawlist
    // shape the sprite bar and TextStyle's swatch use
    private static bool DrawItem(NavItem it, int index, bool selected, bool labels, float side)
    {
        float h = Math.Max(ImGui.GetFrameHeight(), side + 8f);
        float w = Math.Max(1f, ImGui.GetContentRegionAvail().X);

        ImGui.InvisibleButton("##i" + index, new Vector2(w, h));
        bool clicked = ImGui.IsItemClicked();
        bool hovered = ImGui.IsItemHovered();

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var dl = ImGui.GetWindowDrawList();

        // hover wins over selected, which is what Selectable does
        if (hovered) dl.AddRectFilled(min, max, ImGui.GetColorU32(ImGuiCol.HeaderHovered));
        else if (selected) dl.AddRectFilled(min, max, ImGui.GetColorU32(ImGuiCol.Header));

        float pad = ImGui.GetStyle().FramePadding.X;
        // centered with no label beside it, otherwise there is a dead gap to its right
        float iconX = labels ? min.X + pad : min.X + (w - side) * 0.5f;
        var iconMin = new Vector2(iconX, min.Y + (h - side) * 0.5f);
        var iconMax = iconMin + new Vector2(side, side);

        IntPtr tex = selected && it.IconActive != IntPtr.Zero ? it.IconActive : it.Icon;
        if (tex != IntPtr.Zero) dl.AddImage(tex, iconMin, iconMax);
        else Initials(dl, it.Label, iconMin, iconMax);

        string label = it.Label ?? "";
        if (labels && label.Length > 0)
        {
            dl.PushClipRect(min, max, true);
            dl.AddText(new Vector2(iconMax.X + pad, min.Y + (h - ImGui.GetTextLineHeight()) * 0.5f),
                ImGui.GetColorU32(ImGuiCol.Text), label);
            dl.PopClipRect();
        }
        else if (hovered && label.Length > 0)
        {
            ImGui.SetTooltip(label);
        }

        string badge = it.Badge?.Invoke();
        if (!string.IsNullOrEmpty(badge))
            Badge(dl, badge, min, max, iconMax, labels);

        return clicked;
    }

    // squared, because ImGui has no pill and a rounded one at this size just looks smudged
    private static void Badge(ImDrawListPtr dl, string text, Vector2 min, Vector2 max, Vector2 iconMax, bool labels)
    {
        const float padX = 2f;
        var size = ImGui.CalcTextSize(text);
        float w = size.X + padX * 2f;

        // beside the label when there is one, otherwise tucked onto the icon's top corner
        var bMin = labels
            ? new Vector2(max.X - w - padX * 2f, min.Y + (max.Y - min.Y - size.Y) * 0.5f)
            : new Vector2(Math.Min(iconMax.X - w * 0.5f, max.X - w), min.Y);

        var bMax = bMin + new Vector2(w, size.Y);
        dl.AddRectFilled(bMin, bMax, ImGui.GetColorU32(ImGuiCol.Button));
        dl.AddText(bMin + new Vector2(padX, 0f), ImGui.GetColorU32(ImGuiCol.Text), text);
    }

    // the no-texture fallback: up to two letters in a box
    private static void Initials(ImDrawListPtr dl, string label, Vector2 min, Vector2 max)
    {
        dl.AddRect(min, max, ImGui.GetColorU32(ImGuiCol.TextDisabled));

        string s = InitialsOf(label);
        if (s.Length == 0) return;

        var size = ImGui.CalcTextSize(s);
        dl.PushClipRect(min, max, true);
        dl.AddText((min + max) * 0.5f - size * 0.5f, ImGui.GetColorU32(ImGuiCol.Text), s);
        dl.PopClipRect();
    }

    private static string InitialsOf(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "";
        var sb = new StringBuilder(2);
        bool boundary = true;
        foreach (char c in label)
        {
            if (c == ' ') { boundary = true; continue; }
            if (boundary && char.IsLetter(c))
            {
                sb.Append(char.ToUpperInvariant(c));
                if (sb.Length == 2) break;
                boundary = false;
            }
        }
        return sb.ToString();
    }

    private static bool Chevron(bool labels)
    {
        float h = ImGui.GetFrameHeight();
        float w = Math.Max(1f, ImGui.GetContentRegionAvail().X);

        ImGui.InvisibleButton("##chev", new Vector2(w, h));
        bool clicked = ImGui.IsItemClicked();

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var dl = ImGui.GetWindowDrawList();

        if (ImGui.IsItemHovered()) dl.AddRectFilled(min, max, ImGui.GetColorU32(ImGuiCol.HeaderHovered));
        dl.AddLine(min, new Vector2(max.X, min.Y), ImGui.GetColorU32(ImGuiCol.Border));

        string g = labels ? "<<" : ">>";
        dl.AddText(new Vector2(min.X + ImGui.GetStyle().FramePadding.X,
                               min.Y + (h - ImGui.GetTextLineHeight()) * 0.5f),
            ImGui.GetColorU32(ImGuiCol.TextDisabled), g);

        return clicked;
    }
}
