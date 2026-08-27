using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using ExileCore2;
using ExileCore2.Shared.Enums;
using ExileCore2.Shared.Helpers;
using SColor = System.Drawing.Color;

namespace ExileImGui2;

/// <summary>
/// The individual settings widgets: sliders, colors, grids, icons, button groups, tri-state cells.
/// <para>
/// Every labeled control takes (label, value, ...), ends with an optional id that defaults to the
/// label, and returns a changed flag. Where a control has two overloads they are always the same
/// two: a <c>ref</c> value, and a get/set pair for when the value isn't a field.
/// </para>
/// </summary>
public static class Controls
{
    /// <summary>
    /// Escapes % so ImGui's printf-style text functions don't eat it. Run mod text through this.
    /// Null safe.
    /// </summary>
    public static string EscPct(string s) => s?.Replace("%", "%%") ?? "";

    /// <summary>
    /// Hover tooltip for the item just drawn. A no-op on null or empty text, so it is safe to call
    /// unconditionally.
    /// </summary>
    public static void Tip(string tip)
    {
        if (!string.IsNullOrEmpty(tip) && ImGui.IsItemHovered())
            ImGui.SetTooltip(tip);
    }

    // the "##id" every widget needs. id defaults to the label, so a normal row is just (label, value) -
    // only pass one when the label is empty or repeats inside the same scope.
    static string Sfx(string label, string id) => "##" + (id ?? label);

    // no Toggle here on purpose: ImGui.Checkbox("Label", ref v) already is one.

    /// <summary>Int slider. The bounds live at the call site, next to the control.</summary>
    public static bool SliderInt(string label, ref int v, int min, int max, string id = null) =>
        ImGui.SliderInt(label + Sfx(label, id), ref v, min, max);

    /// <summary>Int slider bound through accessors, for a value that isn't a field.</summary>
    public static bool SliderInt(string label, Func<int> get, Action<int> set, int min, int max, string id = null)
    {
        int v = get();
        if (SliderInt(label, ref v, min, max, id)) { set(v); return true; }
        return false;
    }

    /// <summary>
    /// Float slider with a printf format for the readout.
    /// </summary>
    /// <param name="fmt">Sits BEHIND id so a positional fifth argument is always the id, same as
    /// every other control - a format string landing in the id slot by mistake is only an id
    /// collision, but an id landing in fmt is a silently wrong readout.</param>
    public static bool SliderFloat(string label, ref float v, float min, float max, string id = null, string fmt = "%.1f") =>
        ImGui.SliderFloat(label + Sfx(label, id), ref v, min, max, fmt);

    /// <summary>Float slider bound through accessors, for a value that isn't a field.</summary>
    public static bool SliderFloat(string label, Func<float> get, Action<float> set, float min, float max,
        string id = null, string fmt = "%.1f")
    {
        float v = get();
        if (SliderFloat(label, ref v, min, max, id, fmt)) { set(v); return true; }
        return false;
    }

    /// <summary>
    /// Compact colour row: a swatch that opens the picker, with no raw RGBA spinners cluttering the
    /// row.
    /// </summary>
    /// <param name="alpha">False drops the alpha bar for an opaque-only colour.</param>
    public static bool Color(string label, ref SColor c, string id = null, bool alpha = true)
    {
        Vector4 v = EColor.ToVector4(c);
        var flags = ImGuiColorEditFlags.NoInputs | (alpha ? ImGuiColorEditFlags.AlphaBar : ImGuiColorEditFlags.NoAlpha);
        if (ImGui.ColorEdit4(label + Sfx(label, id), ref v, flags)) { c = EColor.FromVector4(v); return true; }
        return false;
    }

    /// <summary>Colour row bound through accessors, for a value that isn't a field.</summary>
    public static bool Color(string label, Func<SColor> get, Action<SColor> set, string id = null, bool alpha = true)
    {
        SColor c = get();
        if (Color(label, ref c, id, alpha)) { set(c); return true; }
        return false;
    }

    /// <summary>
    /// Dropdown over an enum's names. Same call as <see cref="Combo.Enum{T}"/>, which is where it
    /// lives now - <c>Controls.cs</c> on its own does not need <c>Combo.cs</c>.
    /// </summary>
    /// <param name="id">There is no label to key off, so this falls back to the enum type name. Two
    /// combos over the same enum in one scope need one of their own.</param>
    public static bool EnumCombo<T>(ref T value, string id = null, float width = 120f) where T : struct, Enum =>
        Combo.Enum(ref value, id, width);

    /// <summary>
    /// Collapsing section header, open by default. The usual top-level split on a settings page.
    /// </summary>
    /// <returns>True when the section is expanded and its body should draw.</returns>
    public static bool Category(string label, string id = null) =>
        ImGui.CollapsingHeader(label + Sfx(label, id), ImGuiTreeNodeFlags.DefaultOpen);

    /// <summary>
    /// N buttons in a row instead of a dropdown, for 2-4 short options where a dropdown is a click
    /// too many. The active one is tinted with the theme accent, so it tracks the user's theme.
    /// </summary>
    /// <param name="opts">One (label, value) pair per button. Usually an enum, but any type that
    /// compares by default equality works - Picker's category filter passes strings.</param>
    public static bool Segmented<T>(string id, ref T value, params (string label, T val)[] opts)
    {
        bool changed = false;
        ImGui.PushID(id);
        uint accent = ImGui.GetColorU32(ImGuiCol.Header);
        // accent and the plain button fill sit close together in some themes, so the losers get dimmed too
        uint dim = ImGui.GetColorU32(ImGuiCol.Button, 0.35f);
        uint dimText = ImGui.GetColorU32(ImGuiCol.Text, 0.55f);
        for (int i = 0; i < opts.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, 1);
            bool active = EqualityComparer<T>.Default.Equals(value, opts[i].val);
            ImGui.PushStyleColor(ImGuiCol.Button, active ? accent : dim);
            if (!active) ImGui.PushStyleColor(ImGuiCol.Text, dimText);
            if (ImGui.Button(opts[i].label)) { value = opts[i].val; changed = true; }
            ImGui.PopStyleColor(active ? 1 : 2);
        }
        ImGui.PopID();
        return changed;
    }

    // ---- button group: 2..5 buttons on one row, each with its own text, optional icon, optional color ----

    /// <summary>Which side of a <see cref="GroupButton"/>'s text its icon sits on.</summary>
    public enum IconSide
    {
        /// <summary>Icon before the text.</summary>
        Left,
        /// <summary>Icon after the text, pinned to the button's right padding.</summary>
        Right,
    }

    /// <summary>
    /// One button in a <see cref="ButtonGroup"/>.
    /// </summary>
    public struct GroupButton
    {
        /// <summary>Button label. Required.</summary>
        public string Text;

        /// <summary>Fill colour. Null uses the theme's button colour.</summary>
        public SColor? Color;

        /// <summary>Icon sheet name. Null means no icon.</summary>
        public string Sheet;

        /// <summary>Cell within <see cref="Sheet"/>.</summary>
        public int Cell;

        /// <summary>Which side of the text the icon sits on.</summary>
        public IconSide Side;

        /// <summary>
        /// Describes one button. Sheet and Cell are the same (sheet name, cell) pair
        /// <see cref="IconPicker(string, IconSheet[], ref string, ref int, SColor, float, float, int)"/>
        /// stores, resolved against the sheets array you pass to <see cref="ButtonGroup"/>.
        /// </summary>
        public GroupButton(string text, SColor? color = null, string sheet = null, int cell = 0,
            IconSide side = IconSide.Left)
        {
            Text = text; Color = color; Sheet = sheet; Cell = cell; Side = side;
        }
    }

    /// <summary>
    /// How many of the given buttons actually draw. Under 2 isn't a group - use a plain button - and
    /// over 5 stops fitting a settings row. Both degrade rather than throwing, since this runs inside
    /// a render loop.
    /// </summary>
    public static int GroupCount(int n) => n < 2 ? 0 : Math.Min(n, 5);

    /// <summary>
    /// 2 to 5 buttons on one row, each with its own text, optional icon and optional colour.
    /// </summary>
    /// <param name="selected">-1 draws a row of plain action buttons. 0 or more puts it in picker
    /// mode: the active button draws at full strength and the rest fade.</param>
    /// <param name="sheets">Sheets the buttons' (sheet, cell) icon pairs resolve against.</param>
    /// <param name="desaturate">Greys the losers out instead of fading them, so only the pick carries
    /// colour.</param>
    /// <returns>The index clicked this frame, or -1.</returns>
    public static int ButtonGroup(string id, GroupButton[] buttons, int selected = -1,
        IconSheet[] sheets = null, float iconSize = 16f, bool desaturate = false)
    {
        int n = GroupCount(buttons?.Length ?? 0);
        if (n == 0) return -1;

        int clicked = -1;
        var style = ImGui.GetStyle();
        var dl = ImGui.GetWindowDrawList();
        bool picker = selected >= 0;
        ImGui.PushID(id);
        for (int i = 0; i < n; i++)
        {
            var b = buttons[i];
            if (i > 0) ImGui.SameLine(0, 1);

            string label = b.Text ?? "";
            var sh = b.Sheet != null && sheets != null && sheets.Length > 0
                ? sheets[SheetIndex(sheets, b.Sheet)] : null;
            bool hasIcon = sh != null && sh.TexId != IntPtr.Zero;
            float iconW = hasIcon ? iconSize + style.ItemInnerSpacing.X : 0f;
            var ts = ImGui.CalcTextSize(label);
            var size = new Vector2(ts.X + iconW + style.FramePadding.X * 2, ImGui.GetFrameHeight());

            bool on = !picker || i == selected;
            // no color of its own falls back to the theme: accent for the pick, plain button fill for the
            // rest (same as Segmented). themes where those two are close read as one solid row, so the
            // losers get faded either way - never leave the pick to color alone.
            SColor bas = b.Color ?? EColor.FromVector4(
                style.Colors[(int)(on && picker ? ImGuiCol.Header : ImGuiCol.Button)]);
            SColor fill = on ? bas
                             : desaturate ? EColor.Desaturate(bas)
                                          : EColor.Fade(bas, 0.3f);
            uint bg = EColor.U32(fill);
            // a faded fill lets the panel through, so the theme's text color still reads on it. a solid one
            // (colored pick, or a desaturated loser) picks its own: dark text on light, light on dark.
            uint fg = b.Color.HasValue && (on || desaturate)
                ? EColor.U32(EColor.Contrast(fill))
                : ImGui.GetColorU32(ImGuiCol.Text, on ? 1f : 0.55f);

            using (new EColor.StyleColorScope(
                (ImGuiCol.Button, bg),
                (ImGuiCol.ButtonHovered, b.Color.HasValue ? EColor.U32(EColor.Scale(fill, 1.2f)) : ImGui.GetColorU32(ImGuiCol.ButtonHovered)),
                (ImGuiCol.ButtonActive, b.Color.HasValue ? EColor.U32(EColor.Scale(fill, 0.8f)) : ImGui.GetColorU32(ImGuiCol.ButtonActive))))
                if (ImGui.Button("##b" + i, size)) clicked = i;

            // the button is drawn label-less so the icon and text can be laid out by hand - imgui centres a
            // button's own label and has no room for a sprite beside it.
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            float textX = min.X + style.FramePadding.X;
            float iconX = b.Side == IconSide.Right ? max.X - style.FramePadding.X - iconSize : textX;
            if (hasIcon && b.Side == IconSide.Left) textX += iconW;
            if (hasIcon)
            {
                var (uv0, uv1) = CellUv(sh, b.Cell);
                float iy = min.Y + (size.Y - iconSize) * 0.5f;
                dl.AddImage(sh.TexId, new Vector2(iconX, iy), new Vector2(iconX + iconSize, iy + iconSize),
                    uv0, uv1, fg);
            }
            dl.AddText(new Vector2(textX, min.Y + (size.Y - ts.Y) * 0.5f), fg, label);
        }
        ImGui.PopID();
        return clicked;
    }

    /// <summary>
    /// Picker flavour: writes the clicked index back into <paramref name="selected"/>.
    /// </summary>
    /// <returns>True the frame the pick actually changes. Re-clicking the active button is not a change.</returns>
    public static bool ButtonGroup(string id, ref int selected, GroupButton[] buttons,
        IconSheet[] sheets = null, float iconSize = 16f, bool desaturate = false)
    {
        int hit = ButtonGroup(id, buttons, selected, sheets, iconSize, desaturate);
        if (hit < 0 || hit == selected) return false;
        selected = hit;
        return true;
    }

    // ---- menu / split / compound buttons ----

    // imgui's own combo arrow isn't public, so draw one. c is the centre of the glyph box, r its half width.
    static void Chevron(ImDrawListPtr dl, Vector2 c, float r, uint col) =>
        dl.AddTriangleFilled(new Vector2(c.X - r, c.Y - r * 0.5f), new Vector2(c.X + r, c.Y - r * 0.5f),
            new Vector2(c.X, c.Y + r * 0.75f), col);

    // shared dropdown body. anchored under the button instead of at the mouse, so it reads as a menu
    // hanging off the thing you clicked rather than a context menu.
    static int MenuPopup(string popId, string[] items, Vector2 anchor)
    {
        int hit = -1;
        if (ImGui.IsPopupOpen(popId)) ImGui.SetNextWindowPos(anchor);
        if (ImGui.BeginPopup(popId))
        {
            for (int i = 0; i < items.Length; i++)
            {
                if (items[i] == null) { ImGui.Separator(); continue; }
                if (ImGui.Selectable(items[i] + "##i" + i)) hit = i;
            }
            ImGui.EndPopup();
        }
        return hit;
    }

    /// <summary>
    /// A button that drops a menu instead of doing anything itself - the whole button opens it. For a
    /// short list of related one-shot actions that doesn't deserve a row of its own.
    /// </summary>
    /// <param name="items">Menu entries in order. A null entry draws a separator and keeps its index,
    /// so the indices you get back still line up with the array.</param>
    /// <param name="width">0 sizes to the label plus the chevron.</param>
    /// <returns>The index picked this frame, or -1.</returns>
    public static int MenuButton(string label, string[] items, string id = null, float width = 0f)
    {
        if (items == null || items.Length == 0) return -1;
        var style = ImGui.GetStyle();
        string txt = label ?? "";
        float h = ImGui.GetFrameHeight();
        float arrow = h * 0.4f;
        var ts = ImGui.CalcTextSize(txt);
        float w = width > 0 ? width : ts.X + arrow + style.ItemInnerSpacing.X + style.FramePadding.X * 2;

        ImGui.PushID(id ?? label);
        if (ImGui.Button("##m", new Vector2(w, h))) ImGui.OpenPopup("##menu");
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        // label drawn by hand, same reason as ButtonGroup: imgui centres a button's own label and would
        // put it under the chevron.
        var dl = ImGui.GetWindowDrawList();
        uint fg = ImGui.GetColorU32(ImGuiCol.Text);
        dl.AddText(new Vector2(min.X + style.FramePadding.X, min.Y + (h - ts.Y) * 0.5f), fg, txt);
        Chevron(dl, new Vector2(max.X - style.FramePadding.X - arrow * 0.5f, min.Y + h * 0.5f), arrow * 0.5f, fg);
        int hit = MenuPopup("##menu", items, new Vector2(min.X, max.Y));
        ImGui.PopID();
        return hit;
    }

    /// <summary>
    /// How a split button's total width divides: the chevron half gets its own, the primary takes the
    /// rest. Pure, so the math is testable.
    /// </summary>
    /// <param name="gap">Space between the halves. 0 - the two are drawn flush.</param>
    /// <returns>Primary width, never under 1 - a zero-width button is one you can't click.</returns>
    public static float SplitPrimaryWidth(float total, float arrow, float gap = 0f) =>
        Math.Max(total - arrow - gap, 1f);

    /// <summary>
    /// The one action you take most, with a chevron half glued to its right holding the variants.
    /// Clicking the primary does the thing; clicking the chevron drops the menu.
    /// </summary>
    /// <param name="menu">Entries for the chevron half, same rules as <see cref="MenuButton"/>. Null or
    /// empty leaves the chevron there but dead - use a plain button instead.</param>
    /// <param name="picked">Menu index clicked this frame, or -1.</param>
    /// <param name="width">Total width, chevron included. 0 sizes the primary to its label.</param>
    /// <returns>True the frame the PRIMARY half is clicked. The menu reports through
    /// <paramref name="picked"/>, so the two never collide.</returns>
    public static bool SplitButton(string label, string[] menu, out int picked, string id = null, float width = 0f)
    {
        picked = -1;
        var style = ImGui.GetStyle();
        string txt = label ?? "";
        float h = ImGui.GetFrameHeight();
        float aw = h * 0.8f;
        float pw = width > 0 ? SplitPrimaryWidth(width, aw)
                             : ImGui.CalcTextSize(txt).X + style.FramePadding.X * 2;
        bool has = menu != null && menu.Length > 0;

        ImGui.PushID(id ?? label);
        bool hit = ImGui.Button(txt + "##p", new Vector2(pw, h));
        float left = ImGui.GetItemRectMin().X;
        // the two halves overlap by the frame rounding rather than sitting flush: butted up, each half's
        // rounded corners curve away from the other and leave a notch top and bottom. overlapping puts the
        // chevron's left corners on top of the primary's fill instead, so the seam is solid.
        // SameLine(0, -r) does NOT pull left - a negative spacing with no offset means "use the default
        // ItemSpacing", so it widens the gap instead. SetCursorPosX is plain window-local, same trap Grid
        // and Layout.BeginTrailing document.
        float r = style.FrameRounding;
        ImGui.SameLine(0, 0);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() - r);
        if (ImGui.Button("##d", new Vector2(aw + r, h)) && has) ImGui.OpenPopup("##menu");
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var dl = ImGui.GetWindowDrawList();
        // hairline so the halves still read as two things to click. without it the pair is one slab and
        // nothing says the chevron does something else.
        dl.AddLine(new Vector2(min.X + r, min.Y + r), new Vector2(min.X + r, max.Y - r),
            ImGui.GetColorU32(ImGuiCol.Text, 0.25f));
        Chevron(dl, new Vector2((min.X + r + max.X) * 0.5f, min.Y + h * 0.5f),
            h * 0.2f, ImGui.GetColorU32(ImGuiCol.Text));
        if (has) picked = MenuPopup("##menu", menu, new Vector2(left, max.Y));
        ImGui.PopID();
        return hit;
    }

    // the second line rides at this fraction of the font size. small enough to read as support text,
    // big enough to still read.
    const float SubScale = 0.85f;

    /// <summary>
    /// Compound button height: the taller of the stacked text block and the icon, plus padding. Pure,
    /// so the layout math is testable. Pass 0 for <paramref name="subH"/> and the gap drops out too.
    /// </summary>
    public static float CompoundHeight(float topH, float subH, float gap, float iconSize, float padY) =>
        Math.Max(topH + (subH > 0f ? gap + subH : 0f), iconSize) + padY * 2;

    /// <summary>
    /// Two-line button: the action on top, a line of explanation under it in smaller dim text. For the
    /// two or three buttons on a page where the label alone leaves people guessing what they do.
    /// </summary>
    /// <param name="b">Text, optional colour and optional (sheet, cell) icon - the same struct
    /// <see cref="ButtonGroup"/> takes. The icon sits left of both lines, so Side is ignored here.</param>
    /// <param name="secondary">The second line. Empty gives a plain button at normal height.</param>
    /// <param name="width">0 sizes to the wider of the two lines.</param>
    public static bool CompoundButton(GroupButton b, string secondary, IconSheet[] sheets = null,
        float iconSize = 24f, float width = 0f, string id = null)
    {
        var style = ImGui.GetStyle();
        string top = b.Text ?? "";
        string sub = secondary ?? "";
        var sh = b.Sheet != null && sheets != null && sheets.Length > 0
            ? sheets[SheetIndex(sheets, b.Sheet)] : null;
        bool hasIcon = sh != null && sh.TexId != IntPtr.Zero;

        float subPx = ImGui.GetFontSize() * SubScale;
        var t0 = ImGui.CalcTextSize(top);
        float subW = sub.Length > 0 ? ImGui.CalcTextSize(sub).X * SubScale : 0f;
        float subH = sub.Length > 0 ? subPx : 0f;
        float gap = style.ItemInnerSpacing.Y;
        float iconW = hasIcon ? iconSize + style.ItemInnerSpacing.X * 2 : 0f;
        float w = width > 0 ? width : Math.Max(t0.X, subW) + iconW + style.FramePadding.X * 2;
        float th = CompoundHeight(t0.Y, subH, gap, 0f, 0f);                                  // text block alone
        float hgt = CompoundHeight(t0.Y, subH, gap, hasIcon ? iconSize : 0f, style.FramePadding.Y);

        bool clicked;
        var col = b.Color;
        ImGui.PushID(id ?? b.Text);
        using (new EColor.StyleColorScope(
                   (ImGuiCol.Button, col.HasValue ? EColor.U32(col.Value) : ImGui.GetColorU32(ImGuiCol.Button)),
                   (ImGuiCol.ButtonHovered, col.HasValue ? EColor.U32(EColor.Scale(col.Value, 1.2f)) : ImGui.GetColorU32(ImGuiCol.ButtonHovered)),
                   (ImGuiCol.ButtonActive, col.HasValue ? EColor.U32(EColor.Scale(col.Value, 0.8f)) : ImGui.GetColorU32(ImGuiCol.ButtonActive))))
            clicked = ImGui.Button("##c", new Vector2(w, hgt));

        // both lines and the icon are hand placed - imgui centres one line of label and has room for
        // nothing else.
        var min = ImGui.GetItemRectMin();
        var dl = ImGui.GetWindowDrawList();
        uint fg = col.HasValue ? EColor.U32(EColor.Contrast(col.Value)) : ImGui.GetColorU32(ImGuiCol.Text);
        uint dim = col.HasValue ? EColor.U32(EColor.Fade(EColor.Contrast(col.Value), 0.7f))
                                : ImGui.GetColorU32(ImGuiCol.Text, 0.6f);
        float x = min.X + style.FramePadding.X;
        if (hasIcon)
        {
            var (uv0, uv1) = CellUv(sh, b.Cell);
            float iy = min.Y + (hgt - iconSize) * 0.5f;
            dl.AddImage(sh.TexId, new Vector2(x, iy), new Vector2(x + iconSize, iy + iconSize), uv0, uv1, fg);
            x += iconSize + style.ItemInnerSpacing.X * 2;
        }
        float y = min.Y + (hgt - th) * 0.5f;
        dl.AddText(new Vector2(x, y), fg, top);
        if (sub.Length > 0) dl.AddText(ImGui.GetFont(), subPx, new Vector2(x, y + t0.Y + gap), dim, sub);
        ImGui.PopID();
        return clicked;
    }

    // the one red in here. a wrong value and an excluded filter are the same "no" to the eye, so they
    // share a colour rather than drifting apart.
    static readonly SColor Bad = SColor.FromArgb(255, 220, 80, 80);

    // ---- info marks and fields ----

    /// <summary>
    /// The dim <c>(?)</c> that explains itself on hover, for putting after a control you drew yourself.
    /// ImGui ships this as a snippet in its demo rather than a call, which is why everyone rewrites it.
    /// </summary>
    /// <param name="info">Tooltip body. Empty draws the mark but says nothing, so a missing string is
    /// visible rather than silent.</param>
    /// <param name="wrap">Wrap width for the tooltip. Long help unwrapped runs off the screen.</param>
    public static void InfoMark(string info, float wrap = 320f)
    {
        ImGui.TextDisabled("(?)");
        if (string.IsNullOrEmpty(info) || !ImGui.IsItemHovered()) return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(wrap);
        ImGui.TextUnformatted(info);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    /// <summary>
    /// A label with the <c>(?)</c> glued to its right. The label carries the short name, the mark
    /// carries the paragraph nobody wants sitting on the settings page.
    /// </summary>
    public static void InfoLabel(string label, string info, float wrap = 320f)
    {
        ImGui.TextUnformatted(label ?? "");
        ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
        InfoMark(info, wrap);
    }

    /// <summary>
    /// Label on top, your control under it, a hint or an error message below. The shape for a value
    /// that can be wrong - a path, a regex - where the complaint has to sit with what caused it.
    /// <para>
    /// The control is wrapped in a group, so an error rings whatever it drew, however many items that
    /// was.
    /// </para>
    /// </summary>
    /// <param name="control">Draws the control and returns its own changed flag.</param>
    /// <param name="hint">Dim helper line under the control. Drawn only while there is no error.</param>
    /// <param name="error">Non-empty replaces the hint, in red, and rings the control. Null or empty
    /// is the good state.</param>
    /// <returns>The control's changed flag, passed straight through.</returns>
    public static bool Field(string id, string label, Func<bool> control, string hint = null, string error = null)
    {
        if (control == null) return false;
        bool bad = !string.IsNullOrEmpty(error);
        ImGui.PushID(id);
        if (!string.IsNullOrEmpty(label))
        {
            if (bad) ImGui.PushStyleColor(ImGuiCol.Text, EColor.U32(Bad));
            ImGui.TextUnformatted(label);
            if (bad) ImGui.PopStyleColor();
        }
        // grouped so GetItemRect covers everything the callback drew, not just its last item
        ImGui.BeginGroup();
        bool changed = control();
        ImGui.EndGroup();
        if (bad)
            ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
                EColor.U32(Bad), ImGui.GetStyle().FrameRounding);
        string msg = bad ? error : hint;
        if (!string.IsNullOrEmpty(msg))
        {
            if (bad) ImGui.TextColored(EColor.ToVector4(Bad), msg);
            else ImGui.TextDisabled(msg);
        }
        ImGui.PopID();
        return changed;
    }

    // ---- overflow row ----

    /// <summary>
    /// How many items fit before the overflow button has to swallow the rest. Everything fitting means
    /// no overflow button is needed, so that case gets the full width rather than reserving a slot for
    /// a button that never draws. Pure, so the reflow math is testable.
    /// </summary>
    /// <param name="overflowW">Width the "..." button costs once anything has to go in it.</param>
    /// <returns>0..widths.Length. Zero puts everything in the menu.</returns>
    public static int FitItems(float[] widths, float avail, float spacing, float overflowW)
    {
        if (widths == null || widths.Length == 0) return 0;
        float all = 0f;
        for (int i = 0; i < widths.Length; i++) all += widths[i] + (i > 0 ? spacing : 0f);
        if (all <= avail) return widths.Length;

        float budget = avail - overflowW - spacing;
        int n = 0;
        float used = 0f;
        for (int i = 0; i < widths.Length; i++)
        {
            float w = widths[i] + (i > 0 ? spacing : 0f);
            if (used + w > budget) break;
            used += w;
            n++;
        }
        return n;
    }

    /// <summary>
    /// A row of buttons that gives up gracefully when the panel narrows: as many as fit are drawn, the
    /// rest fold into a trailing menu. Nothing disappears, it just stops being one click away.
    /// </summary>
    /// <param name="width">Space to fill. 0 measures what's available.</param>
    /// <param name="more">Label for the overflow button.</param>
    /// <returns>The index clicked this frame, or -1. Items in the menu keep their array index, so the
    /// return means the same thing whether the row was wide or narrow.</returns>
    public static int OverflowRow(string id, string[] items, float width = 0f, string more = "...")
    {
        if (items == null || items.Length == 0) return -1;
        var style = ImGui.GetStyle();
        float avail = width > 0 ? width : ImGui.GetContentRegionAvail().X;
        float pad = style.FramePadding.X * 2;

        var widths = new float[items.Length];
        for (int i = 0; i < items.Length; i++) widths[i] = ImGui.CalcTextSize(items[i] ?? "").X + pad;
        float moreW = ImGui.CalcTextSize(more).X + pad + ImGui.GetFrameHeight() * 0.4f
                    + style.ItemInnerSpacing.X;
        int n = FitItems(widths, avail, style.ItemSpacing.X, moreW);

        int hit = -1;
        ImGui.PushID(id);
        for (int i = 0; i < n; i++)
        {
            if (i > 0) ImGui.SameLine();
            if (ImGui.Button((items[i] ?? "") + "##o" + i)) hit = i;
        }
        if (n < items.Length)
        {
            if (n > 0) ImGui.SameLine();
            // the menu holds the tail but reports in the caller's indices, so the offset is added back here
            var rest = new string[items.Length - n];
            Array.Copy(items, n, rest, 0, rest.Length);
            int m = MenuButton(more, rest, "more");
            if (m >= 0) hit = n + m;
        }
        ImGui.PopID();
        return hit;
    }

    /// <summary>
    /// <c>[checkbox][your control, greyed when off] label</c> - the shape for "use the default unless
    /// I say otherwise".
    /// </summary>
    /// <param name="ovr">The override toggle. The inner control is disabled while this is false.</param>
    /// <param name="control">Draws the inner control and returns its own changed flag.</param>
    /// <returns>The toggle's change OR the inner control's, folded into one flag.</returns>
    public static bool OverrideRow(string id, string label, ref bool ovr, Func<bool> control, string tip = null)
    {
        ImGui.PushID(id);
        bool changed = ImGui.Checkbox("##o", ref ovr);
        // the bare checkbox is the whole point of the row and reads as nothing without this. callers
        // using the row as a plain enable rather than an override pass their own wording
        Tip(tip ?? (ovr
            ? "Overriding " + label.ToLowerInvariant() + ". Untick to inherit."
            : "Inheriting " + label.ToLowerInvariant() + ". Tick to override."));
        ImGui.SameLine();
        if (!ovr) ImGui.BeginDisabled();
        changed |= control();
        if (!ovr) ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextUnformatted(label);
        ImGui.PopID();
        return changed;
    }

    // ---- grids ----

    /// <summary>
    /// One cell of a <see cref="ToggleGrid(string, ToggleItem[], int)"/>. Uses accessors rather than a
    /// ref because the values usually live on an object.
    /// </summary>
    public struct ToggleItem
    {
        /// <summary>Cell label, and what the column stride is measured off.</summary>
        public string Label;
        /// <summary>Optional hover tooltip.</summary>
        public string Tip;
        /// <summary>Reads the current value.</summary>
        public Func<bool> Get;
        /// <summary>Writes a new value.</summary>
        public Action<bool> Set;

        /// <summary>Describes one checkbox cell.</summary>
        public ToggleItem(string label, Func<bool> get, Action<bool> set, string tip = null)
        { Label = label; Get = get; Set = set; Tip = tip; }
    }

    /// <summary>Same shape as <see cref="ToggleItem"/>, for a colour cell.</summary>
    public struct ColorItem
    {
        /// <summary>Cell label, and what the column stride is measured off.</summary>
        public string Label;
        /// <summary>Reads the current colour.</summary>
        public Func<SColor> Get;
        /// <summary>Writes a new colour.</summary>
        public Action<SColor> Set;

        /// <summary>Describes one colour cell.</summary>
        public ColorItem(string label, Func<SColor> get, Action<SColor> set) { Label = label; Get = get; Set = set; }
    }

    /// <summary>
    /// Column width for a set of labels: the widest one, plus a framed widget and its gaps. A
    /// checkbox and a compact colour swatch are both one frame-height square, so this measures
    /// either. Useful on its own when something else in the column - a slider, say - has to line up
    /// with the grid.
    /// </summary>
    public static float LabelStride(params string[] labels)
    {
        float w = 0f;
        foreach (var l in labels) w = Math.Max(w, ImGui.CalcTextSize(l).X);
        return w + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X * 2;
    }

    /// <summary>
    /// How many <paramref name="stride"/>-wide columns fit in <paramref name="avail"/>. Pure, so the
    /// reflow math is testable.
    /// </summary>
    /// <param name="max">Caps the count when a row of 9 would just look silly. 0 means no cap.</param>
    /// <returns>Always at least 1 - one clipped column beats zero.</returns>
    public static int FitColumns(float avail, float stride, int max = 0)
    {
        if (stride <= 0f) return 1;
        int n = (int)Math.Floor(avail / stride);
        if (n < 1) n = 1;
        if (max > 0 && n > max) n = max;
        return n;
    }

    /// <summary>
    /// Generic n-up grid: you draw the cell, this places it.
    /// <para>
    /// Deliberately NOT a table - a table with no explicit size fills whatever width its parent
    /// offers, which is circular inside a column that is itself measuring its content. Plain items
    /// plus SameLine stay addable, so a measuring parent column can size to them.
    /// </para>
    /// </summary>
    /// <param name="stride">Cell width. <see cref="LabelStride"/> for a widget-plus-label cell, or
    /// your own number.</param>
    /// <param name="cell">Draws cell i and returns its changed flag. Called inside its own PushID(i),
    /// so ids only have to be unique within one cell.</param>
    /// <param name="columns">Below 1 measures the space available and reflows as the window resizes.</param>
    /// <returns>The OR of every cell's changed flag.</returns>
    public static bool Grid(string id, int count, float stride, Func<int, bool> cell, int columns = 0)
    {
        if (count < 1 || cell == null) return false;
        if (columns < 1) columns = FitColumns(ImGui.GetContentRegionAvail().X, stride);

        bool d = false;
        float x0 = ImGui.GetCursorPosX();   // window-local origin: inside a table this isn't 0
        ImGui.PushID(id);
        for (int i = 0; i < count; i++)
        {
            int col = i % columns;
            // SameLine(0,0) + SetCursorPosX, NOT SameLine(offset) - same trap Layout.BeginTrailing documents.
            // SameLine's offset is measured from the column origin and imgui adds ColumnsOffset back
            // in, but x0 came from GetCursorPosX and already carries it, so the column offset lands
            // twice and every cell past the first drifts right by a whole column. SetCursorPosX is
            // plain window-local, which is the space x0 is in.
            if (col > 0)
            {
                ImGui.SameLine(0, 0);
                ImGui.SetCursorPosX(x0 + col * stride);
            }
            ImGui.PushID(i);
            d |= cell(i);
            ImGui.PopID();
        }
        ImGui.PopID();
        return d;
    }

    // labels off an item array, for the Grid stride
    static float ItemStride<T>(T[] items, Func<T, string> label)
    {
        var labels = new string[items.Length];
        for (int i = 0; i < items.Length; i++) labels[i] = label(items[i]);
        return LabelStride(labels);
    }

    /// <summary>
    /// N-up checkboxes on a stride measured off the widest label. Use this overload when the values
    /// don't live in an array.
    /// </summary>
    /// <param name="columns">Below 1 fits whatever the width allows.</param>
    public static bool ToggleGrid(string id, ToggleItem[] items, int columns = 2)
    {
        if (items == null || items.Length == 0) return false;
        return Grid(id, items.Length, ItemStride(items, it => it.Label), i =>
        {
            bool v = items[i].Get();
            bool changed = ImGui.Checkbox(items[i].Label + "##t", ref v);
            if (changed) items[i].Set(v);
            Tip(items[i].Tip);   // runs every pass, IsItemHovered only reads the checkbox just drawn
            return changed;
        }, columns);
    }

    /// <summary>
    /// The usual case: parallel label and value arrays. No ToggleItem, no per-cell lambdas and
    /// nothing to cache - the array elements are written through by ref.
    /// </summary>
    /// <param name="tips">Optional hover tooltips, lined up by index. Shorter than the labels is fine.</param>
    public static bool ToggleGrid(string id, string[] labels, bool[] values, int columns = 2, string[] tips = null)
    {
        if (labels == null || values == null) return false;
        int n = Math.Min(labels.Length, values.Length);
        if (n == 0) return false;
        return Grid(id, n, LabelStride(labels), i =>
        {
            bool changed = ImGui.Checkbox(labels[i] + "##t", ref values[i]);
            if (tips != null && i < tips.Length) Tip(tips[i]);
            return changed;
        }, columns);
    }

    /// <summary>
    /// The same grid of compact colour swatches. Defaults to auto-fit, since colour labels vary more
    /// than checkbox ones and a fixed column count wastes width more often here.
    /// </summary>
    public static bool ColorGrid(string id, ColorItem[] items, int columns = 0, bool alpha = true)
    {
        if (items == null || items.Length == 0) return false;
        return Grid(id, items.Length, ItemStride(items, it => it.Label), i =>
        {
            SColor c = items[i].Get();
            if (Color(items[i].Label, ref c, "c", alpha)) { items[i].Set(c); return true; }
            return false;
        }, columns);
    }

    /// <summary>Array form, same as <see cref="ToggleGrid(string, string[], bool[], int, string[])"/>.</summary>
    public static bool ColorGrid(string id, string[] labels, SColor[] values, int columns = 0, bool alpha = true)
    {
        if (labels == null || values == null) return false;
        int n = Math.Min(labels.Length, values.Length);
        if (n == 0) return false;
        return Grid(id, n, LabelStride(labels), i => Color(labels[i], ref values[i], "c", alpha), columns);
    }

    /// <summary>
    /// Right-aligns a group of widgets. Same call as <see cref="Layout.BeginTrailing"/>, which is
    /// where it lives now - the frames that want only right-alignment don't need this file.
    /// </summary>
    /// <param name="pad">Pushes the group off the right edge, to leave room for a scrollbar or a
    /// child border.</param>
    public static void BeginTrailing(string id, float pad = 0f) => Layout.BeginTrailing(id, pad);

    /// <summary>Closes a <see cref="BeginTrailing"/>. Same call as <see cref="Layout.EndTrailing"/>.</summary>
    public static void EndTrailing() => Layout.EndTrailing();

    /// <summary>
    /// Nudges the next item down so a short widget - a 12px swatch, a line of plain text - sits
    /// centred against the framed controls on the same row. Affects that one item only; SameLine puts
    /// the line's y back afterwards.
    /// </summary>
    /// <param name="itemHeight">Height of the short item you are about to draw.</param>
    public static void AlignMid(float itemHeight) =>
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Math.Max(0f, (ImGui.GetFrameHeight() - itemHeight) * 0.5f));

    /// <summary>
    /// A square latch button instead of a labelled checkbox, with the label living in the tooltip.
    /// Use it when a header row has no width to spare - "M" and "W" beat "Map icon" and "World icon"
    /// by about 200px.
    /// <para>
    /// The on state gets three cues: a brighter fill, an accent outline, and a full-strength glyph.
    /// The accent alone is invisible when the toggle sits on a header bar already drawn in Header.
    /// </para>
    /// </summary>
    /// <param name="glyph">One or two characters. ASCII only - the default font has no symbol glyphs.</param>
    public static bool IconToggle(string glyph, ref bool v, string tooltip = null, string id = null, float width = 22f)
    {
        id ??= glyph;
        bool changed = false;
        // three cues for the on state: brighter fill, accent outline, full-strength glyph. Header alone
        // was invisible when the toggle sits on a header bar that's already drawn in Header.
        using (new EColor.StyleColorScope(
                   (ImGuiCol.Button, ImGui.GetColorU32(v ? ImGuiCol.ButtonActive : ImGuiCol.FrameBg)),
                   (ImGuiCol.Text, ImGui.GetColorU32(v ? ImGuiCol.Text : ImGuiCol.TextDisabled))))
            if (ImGui.Button(glyph + "##" + id, new Vector2(width, 0)))
            {
                v = !v;
                changed = true;
            }
        if (v)
            ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
                ImGui.GetColorU32(ImGuiCol.CheckMark), ImGui.GetStyle().FrameRounding);
        if (tooltip != null && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        return changed;
    }

    // ---- sprite sheets: draw a cell, or pick one ----

    /// <summary>
    /// A sprite with no widget behind it, for headers and summary rows where a button would sit on
    /// top of the header's own hit area and eat the click. Works with any sheet - you pass the
    /// texture and the UVs.
    /// <para>
    /// A texture id of 0 (a sheet that never loaded) draws an empty slot of the right size rather
    /// than handing the renderer a null texture, which is not something to find out about mid-map.
    /// </para>
    /// <code>
    /// Graphics.InitImage("MyIcons.png");                 // once, if it isn't the game's own sheet
    /// var tex = Graphics.GetTextureId("MyIcons.png");    // cache it, it's a dictionary lookup
    /// </code>
    /// </summary>
    public static void IconImage(IntPtr texId, Vector2 uv0, Vector2 uv1, float size, SColor tint)
    {
        if (texId == IntPtr.Zero) { ImGui.Dummy(new Vector2(size, size)); return; }
        ImGui.Image(texId, new Vector2(size, size), uv0, uv1, EColor.ToVector4(tint));
    }

    /// <summary>Same, for the game's own Icons.png, where <see cref="MapIconsIndex"/> picks the cell.</summary>
    public static void IconImage(IntPtr texId, MapIconsIndex icon, float size, SColor tint)
    {
        var uv = SpriteHelper.GetUV(icon);
        IconImage(texId, new Vector2(uv.Left, uv.Top), new Vector2(uv.Right, uv.Bottom), size, tint);
    }

    /// <summary>
    /// Same, for a plugin's own row-major sheet. <see cref="GridAtlas"/> does the cell-to-UV math.
    /// </summary>
    public static void IconImage(IntPtr texId, GridAtlas atlas, int index, float size, SColor tint)
    {
        var (uv0, uv1) = atlas.UVPair(index);
        IconImage(texId, uv0, uv1, size, tint);
    }

    /// <summary>
    /// Same, resolved out of a set of sheets by (sheet name, cell) - the pair the icon picker stores.
    /// An unknown sheet name falls back to the first sheet rather than drawing nothing, so a renamed
    /// sheet in an old config still shows something.
    /// </summary>
    public static void IconImage(IconSheet[] sheets, string sheet, int index, float size, SColor tint)
    {
        if (sheets == null || sheets.Length == 0) { ImGui.Dummy(new Vector2(size, size)); return; }
        var sh = sheets[SheetIndex(sheets, sheet)];
        var (uv0, uv1) = CellUv(sh, index);
        IconImage(sh.TexId, uv0, uv1, size, tint);
    }

    // ---- icon picker over any number of sprite sheets ----

    /// <summary>
    /// One sheet the icon picker can browse. Use the factories below, or build one by hand for a
    /// sheet no factory covers.
    /// <para>
    /// Build these once and cache them - the factories load the texture and resolve its id, which is
    /// not work to repeat per row per frame.
    /// </para>
    /// </summary>
    public sealed class IconSheet
    {
        /// <summary>Sheet name. This is the half of the stored (sheet, cell) pair that names the sheet.</summary>
        public string Name = "";

        /// <summary>Texture id, or 0 when the sheet never loaded. The picker says "sheet not loaded" for 0.</summary>
        public IntPtr TexId;

        /// <summary>How many cells the sheet has.</summary>
        public int Count;

        /// <summary>Maps a cell index to its UV corners.</summary>
        public Func<int, (Vector2 uv0, Vector2 uv1)> Uv;

        /// <summary>
        /// Optional per-cell name, used as both the search text and the cell tooltip. Supply it and
        /// the picker gets a search box.
        /// </summary>
        public Func<int, string> Label;

        /// <summary>
        /// The game's own Icons.png out of ExileCore2's textures folder: <see cref="MapIconsIndex"/>
        /// picks the cell and its enum name is the label. About 690 cells, mostly loot-filter shapes,
        /// which is why the search box isn't optional for this one.
        /// </summary>
        public static IconSheet Game(Graphics gfx, string name = "Game")
        {
            gfx.InitImage("Icons.png");   // lives in ExileCore2's own textures folder
            var names = Enum.GetNames(typeof(MapIconsIndex));
            return new IconSheet
            {
                Name = name,
                TexId = Resolve(gfx, "Icons.png"),
                Count = names.Length,
                Label = i => names[i],
                Uv = i =>
                {
                    var r = SpriteHelper.GetUV((MapIconsIndex)i);
                    return (new Vector2(r.Left, r.Top), new Vector2(r.Right, r.Bottom));
                },
            };
        }

        /// <summary>A row-major sheet whose texture id you already hold.</summary>
        public static IconSheet Grid(string name, IntPtr texId, GridAtlas atlas, int count,
            Func<int, string> label = null) =>
            new() { Name = name, TexId = texId, Count = count, Uv = atlas.UVPair, Label = label };

        /// <summary>
        /// A row-major sheet loaded from a file the plugin ships.
        /// </summary>
        /// <param name="path">Normally <c>Path.Combine(DirectoryFullName, "textures", "MySheet.png")</c>
        /// - the plugin's OWN output dir, not ExileCore2's textures folder. Have the csproj copy the
        /// file to output. A missing file leaves TexId 0 and the picker says "sheet not loaded"
        /// rather than throwing.</param>
        public static IconSheet File(Graphics gfx, string name, string path, GridAtlas atlas, int count,
            Func<int, string> label = null)
        {
            if (System.IO.File.Exists(path)) gfx.InitImage(name, path);
            return Grid(name, Resolve(gfx, name), atlas, count, label);
        }

        // GetTextureId on a name that was never loaded is not worth finding out about the hard way.
        static IntPtr Resolve(Graphics gfx, string name) => gfx.HasImage(name) ? gfx.GetTextureId(name) : IntPtr.Zero;
    }

    static readonly Dictionary<string, int> _pickerSheet = new();      // sheet being browsed, per picker id
    static readonly Dictionary<string, string> _pickerFilter = new();

    /// <summary>
    /// A button showing the current icon. Clicking opens a popup that browses EVERY sheet you hand
    /// it, with a radio row to switch between them without leaving the popup.
    /// <para>
    /// The stored value is a (sheet name, cell) pair, which is why pointing a field at your own art
    /// instead of the game atlas needs no second field.
    /// </para>
    /// </summary>
    /// <param name="sheet">Sheet name half of the stored value. Written on a pick.</param>
    /// <param name="index">Cell half of the stored value. Written on a pick.</param>
    /// <param name="size">Size of the preview button itself.</param>
    /// <param name="cell">Size of one cell in the browse grid.</param>
    /// <returns>True the frame something is picked.</returns>
    public static bool IconPicker(string id, IconSheet[] sheets, ref string sheet, ref int index,
        SColor tint, float size = 22f, float cell = 32f, int columns = 8)
    {
        int noSize = 0;
        return IconPicker(id, sheets, ref sheet, ref index, ref tint, ref noSize, false, 0, 0, false, size, cell, columns);
    }

    /// <summary>
    /// The same picker with a preview strip at the top of the popup: the sprite drawn at the size and
    /// tint it will really render at, next to the slider and swatch that set them. Picking a cell
    /// leaves this popup open - that being the point of the preview - so it ends on Done, Esc or a
    /// click away.
    /// </summary>
    /// <param name="tint">Written back by the popup's own colour swatch.</param>
    /// <param name="drawSize">Written back by the popup's own size slider.</param>
    /// <returns>True the frame a cell, the tint or the size changes - not just on a cell pick.</returns>
    public static bool IconPicker(string id, IconSheet[] sheets, ref string sheet, ref int index,
        ref SColor tint, ref int drawSize, int minSize = 8, int maxSize = 96,
        float size = 22f, float cell = 32f, int columns = 8) =>
        IconPicker(id, sheets, ref sheet, ref index, ref tint, ref drawSize, true, minSize, maxSize, true,
            size, cell, columns);

    /// <summary>
    /// The preview popup with a tint swatch but NO size slider, for a value that has no per-item size
    /// of its own. Everything else matches the size-bearing overload.
    /// </summary>
    /// <param name="tint">Written back by the popup's own colour swatch.</param>
    /// <param name="tintEnabled">False previews the sprite untinted - white - while still letting the
    /// swatch set the colour, for a caller whose tint is behind a toggle of its own.</param>
    /// <returns>True the frame a cell or the tint changes.</returns>
    public static bool IconPicker(string id, IconSheet[] sheets, ref string sheet, ref int index,
        ref SColor tint, bool tintEnabled = true, float size = 22f, float cell = 32f, int columns = 8)
    {
        int px = 48;   // fixed preview size, since there is nothing per-item to vary it by
        return IconPicker(id, sheets, ref sheet, ref index, ref tint, ref px, true, px, px, false,
            size, cell, columns, tintEnabled);
    }

    static bool IconPicker(string id, IconSheet[] sheets, ref string sheet, ref int index,
        ref SColor tint, ref int drawSize, bool preview, int minSize, int maxSize, bool showSize,
        float size, float cell, int columns, bool tintEnabled = true)
    {
        if (sheets == null || sheets.Length == 0) return false;
        bool changed = false;
        // tintEnabled says whether the tint APPLIES, not whether it can be edited. a caller whose tint
        // is switched off still wants to set the colour it will use once switched on, and previewing it
        // tinted anyway would be a lie about what the sprite currently draws as.
        var shown = tintEnabled ? tint : SColor.FromArgb(255, 255, 255, 255);
        var tintV = EColor.ToVector4(shown);
        int cur = SheetIndex(sheets, sheet);

        ImGui.PushID(id);
        // the preview button IS the current sprite at its current tint, so the button is the wysiwyg. the
        // frame is transparent - a button plate around a sprite reads as chrome and fights the art. hover
        // and press still come from the theme, so it's clear the thing is clickable.
        var (u0, u1) = CellUv(sheets[cur], index);
        bool clicked;
        using (new EColor.StyleColorScope((ImGuiCol.Button, 0u)))
            clicked = sheets[cur].TexId == IntPtr.Zero
                ? ImGui.Button("?##pick", new Vector2(size, size))     // sheet never loaded - still openable
                : ImGui.ImageButton("##pick", sheets[cur].TexId, new Vector2(size, size), u0, u1, Vector4.Zero, tintV);
        if (clicked)
        {
            _pickerSheet[id] = cur;        // open on the sheet the value already lives in
            _pickerFilter[id] = "";
            ImGui.OpenPopup("##iconpick");
        }

        if (ImGui.BeginPopup("##iconpick"))
        {
            int active = _pickerSheet.TryGetValue(id, out var a) ? Math.Clamp(a, 0, sheets.Length - 1) : cur;
            // radio row rather than a combo: every sheet is visible and switching costs one click.
            for (int i = 0; i < sheets.Length; i++)
            {
                if (i > 0) ImGui.SameLine(0, 12);
                if (ImGui.RadioButton(sheets[i].Name + "##s" + i, active == i)) active = i;
            }
            _pickerSheet[id] = active;
            var sh = sheets[active];
            ImGui.Separator();

            if (preview)
            {
                // the sprite at the size and tint it will actually draw at, next to the two controls that
                // set them - picking a cell and then guessing at 20 vs 48px in another panel is the slow way.
                // the box keeps a fixed footprint so the popup doesn't resize while you drag the slider.
                float box = Math.Clamp(maxSize, 16, 96);
                var org = ImGui.GetCursorScreenPos();
                ImGui.Dummy(new Vector2(box, box));
                var dl = ImGui.GetWindowDrawList();
                // framed like an input, so a small or dark sprite still sits in something you can see.
                dl.AddRectFilled(org, org + new Vector2(box), ImGui.GetColorU32(ImGuiCol.FrameBg), 3f);
                dl.AddRect(org, org + new Vector2(box), ImGui.GetColorU32(ImGuiCol.Border), 3f);
                if (sheets[cur].TexId != IntPtr.Zero)
                {
                    float px = Math.Clamp(drawSize, minSize, maxSize);
                    var (p0, p1) = CellUv(sheets[cur], index);   // the stored value, not the sheet being browsed
                    var at = org + new Vector2((box - px) * 0.5f, (box - px) * 0.5f);
                    dl.AddImage(sheets[cur].TexId, at, at + new Vector2(px), p0, p1, EColor.U32(shown));
                }
                ImGui.SameLine();
                ImGui.BeginGroup();
                if (showSize)
                {
                    ImGui.SetNextItemWidth(160f);
                    if (ImGui.SliderInt("Size", ref drawSize, minSize, maxSize)) changed = true;
                }
                changed |= Color("Tint", ref tint, "ptint");
                // picking a cell leaves this popup open (that's the point of the preview), so it needs a
                // way out that isn't "click somewhere else". Esc still works too.
                if (ImGui.Button("Done")) ImGui.CloseCurrentPopup();
                ImGui.EndGroup();
                shown = tintEnabled ? tint : SColor.FromArgb(255, 255, 255, 255);
                tintV = EColor.ToVector4(shown);   // so the cells below pick up a tint edit the same frame
                ImGui.Separator();
            }

            var filter = _pickerFilter.TryGetValue(id, out var f) ? f : "";
            if (sh.Label != null)
            {
                ImGui.SetNextItemWidth(180f);
                ImGui.InputTextWithHint("##filter", "search", ref filter, 64);
                _pickerFilter[id] = filter;
            }

            if (sh.TexId == IntPtr.Zero)
            {
                // sheet the plugin never loaded (missing file, or InitImage was never called). say so,
                // instead of handing the renderer a null texture once per cell.
                ImGui.TextDisabled("sheet not loaded");
            }
            else
            {
                var style = ImGui.GetStyle();
                float step = cell + style.ItemSpacing.X + style.FramePadding.X * 2;
                ImGui.BeginChild("##grid", new Vector2(columns * step + 12f, 320f), ImGuiChildFlags.Border);
                int col = 0;
                // transparent cell frames, same as the preview button: a grid of button plates buries the
                // sprites you're trying to compare. hover/press still highlight.
                using (new EColor.StyleColorScope((ImGuiCol.Button, 0u)))
                for (int i = 0; i < sh.Count; i++)
                {
                    var name = sh.Label?.Invoke(i);
                    if (name != null && !Text.Matches(filter, name)) continue;
                    var (c0, c1) = CellUv(sh, i);
                    if (ImGui.ImageButton("c" + i, sh.TexId, new Vector2(cell, cell), c0, c1, Vector4.Zero, tintV))
                    {
                        sheet = sh.Name;
                        index = i;
                        changed = true;
                        // with a preview up there, closing on the first click means you never see what you
                        // picked at its real size. keep it open and let Done/Esc/click-away end it.
                        if (!preview) ImGui.CloseCurrentPopup();
                    }
                    // ring the current cell. the frames are transparent, so without this there's nothing
                    // saying which one the value is on - and now that the popup stays open, you look.
                    if (sh.Name == sheet && i == index)
                        ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
                            ImGui.GetColorU32(ImGuiCol.CheckMark), 2f);
                    if (name != null && ImGui.IsItemHovered()) ImGui.SetTooltip(name);
                    // wrap on drawn cells, not on i, or a filtered grid comes out full of holes.
                    col++;
                    if (col % columns != 0) ImGui.SameLine();
                }
                ImGui.EndChild();
            }
            ImGui.EndPopup();
        }
        ImGui.PopID();
        return changed;
    }

    // an unknown or empty sheet name falls back to the first sheet instead of drawing nothing, so a
    // renamed sheet in an old config still shows something.
    static int SheetIndex(IconSheet[] sheets, string name)
    {
        for (int i = 0; i < sheets.Length; i++)
            if (sheets[i].Name == name) return i;
        return 0;
    }

    static (Vector2 uv0, Vector2 uv1) CellUv(IconSheet s, int index)
    {
        if (s.Uv == null || s.Count <= 0) return (Vector2.Zero, Vector2.One);
        return s.Uv(Math.Clamp(index, 0, s.Count - 1));
    }

    // ---- tri-state filter cell ----

    /// <summary>One item in a <see cref="TriStateGrid"/>.</summary>
    public struct TriItem
    {
        /// <summary>Cell label, drawn beside the glyph.</summary>
        public string Label;

        /// <summary>0 any, 1 require, 2 exclude. Written back by the grid.</summary>
        public int Value;

        /// <summary>Describes one tri-state cell, defaulting to "any".</summary>
        public TriItem(string label, int value = 0) { Label = label; Value = value; }
    }

    /// <summary>
    /// The pure cycle rule, if you drive it yourself: any -> require -> exclude -> any, with
    /// <paramref name="back"/> reversing it.
    /// </summary>
    public static int CycleTri(int v, bool back) => back ? (v + 2) % 3 : (v + 1) % 3;

    /// <summary>
    /// Three states in the width of a checkbox. Left-click cycles forward, shift-click back,
    /// right-click resets to any.
    /// <para>
    /// The glyphs are ASCII on purpose - the default font has no check or cross - so the colour does
    /// most of the talking: grey "-", accent "+", red "x". Right-click reset is there because
    /// cycling forward three times to get back to any is tedious, not as a fallback for shift.
    /// </para>
    /// </summary>
    /// <param name="v">0 any, 1 require, 2 exclude.</param>
    /// <param name="glyphScale">Blows the glyph up relative to the label - a 1x "-" or "+" reads as
    /// noise at settings size.</param>
    public static bool TriState(string label, ref int v, string id = null, float box = 22f, float glyphScale = 1.4f)
    {
        bool changed = false;
        ImGui.PushID(id ?? label);
        uint col = v == 1 ? ImGui.GetColorU32(ImGuiCol.CheckMark)
                 : v == 2 ? EColor.U32(Bad)
                 : ImGui.GetColorU32(ImGuiCol.TextDisabled);
        string glyph = v == 1 ? "+" : v == 2 ? "x" : "-";

        float padY = ImGui.GetStyle().FramePadding.Y;
        float rowH = ImGui.GetFrameHeight();     // unscaled, so a grid of these keeps normal row height
        ImGui.SetWindowFontScale(glyphScale);
        // the scaled glyph has to FIT the frame: imgui only centres text vertically while there's room,
        // and pins it to the top padding once it overflows (reads as a dropped, top-heavy glyph). so the
        // box grows to the scaled line height when it has to, and stays at row height when it doesn't.
        var size = new Vector2(box, Math.Max(rowH, ImGui.GetTextLineHeight() + padY * 2));
        using (new EColor.StyleColorScope((ImGuiCol.Text, col),
                                          (ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.FrameBg))))
            if (ImGui.Button(glyph + "##t", size))
            {
                v = CycleTri(v, ImGui.GetIO().KeyShift);
                changed = true;
            }
        ImGui.SetWindowFontScale(1f);
        float btnH = ImGui.GetItemRectSize().Y;
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right) && v != 0) { v = 0; changed = true; }
        if (!string.IsNullOrEmpty(label))
        {
            // centre the label on the button, not on the frame padding - the button may be the taller of
            // the two now that the glyph is scaled.
            ImGui.SameLine();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (btnH - ImGui.GetTextLineHeight()) * 0.5f);
            ImGui.TextUnformatted(label);
        }
        ImGui.PopID();
        return changed;
    }

    /// <summary>
    /// N-across grid of <see cref="TriState"/> cells in equal-width columns, writing
    /// <see cref="TriItem.Value"/> back into the array.
    /// </summary>
    public static bool TriStateGrid(string id, TriItem[] items, int columns)
    {
        if (items == null || items.Length == 0) return false;
        if (columns < 1) columns = 1;
        if (!ImGui.BeginTable("##" + id, columns, ImGuiTableFlags.SizingStretchSame)) return false;

        bool changed = false;
        for (int i = 0; i < items.Length; i++)
        {
            if (i % columns == 0) ImGui.TableNextRow();
            ImGui.TableNextColumn();
            int v = items[i].Value;
            if (TriState(items[i].Label, ref v, id + i)) { items[i].Value = v; changed = true; }
        }
        ImGui.EndTable();
        return changed;
    }

    /// <summary>
    /// Rounder frames and tighter padding for a whole settings page. Style VARS only, never colours,
    /// so the user's chosen theme still shows through. Use with <c>using</c>.
    /// </summary>
    public readonly struct PanelStyleScope : IDisposable
    {
        /// <summary>Pushes the panel style vars.</summary>
        public PanelStyleScope()
        {
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3f);
            ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 3f);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 3f);
            ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 3f);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6, 3));
        }
        /// <summary>Pops the style vars this scope pushed.</summary>
        public void Dispose() => ImGui.PopStyleVar(5);
    }
}
