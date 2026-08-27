using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using SColor = System.Drawing.Color;

namespace ExileImGui2;

/// <summary>Severity of a toast, which picks its accent-bar colour.</summary>
public enum ToastLevel
{
    /// <summary>Neutral blue. The default.</summary>
    Info,
    /// <summary>Green.</summary>
    Success,
    /// <summary>Amber.</summary>
    Warning,
    /// <summary>Red.</summary>
    Error,
}

/// <summary>
/// Which way the stack grows from its anchor. Expose it to your own users, or just pin one.
/// </summary>
public enum ToastGrow
{
    /// <summary>Always grow upward from the anchor.</summary>
    Up,
    /// <summary>Always grow downward from the anchor.</summary>
    Down,
    /// <summary>Read the anchor's screen position: lower half grows up, upper half grows down.</summary>
    Default,
}

/// <summary>
/// Look and placement for a toast stack. Plain mutable defaults, so callers can bind their own
/// settings fields straight to it.
/// </summary>
public sealed class ToastStyle
{
    /// <summary>Text pixel size. Scaled off the current font size when drawing.</summary>
    public float TextSize = 16f;
    /// <summary>Padding inside each box.</summary>
    public float Padding = 6f;
    /// <summary>Wrap width for the whole box. 0 or less means no wrap.</summary>
    public float MaxWidth = 320f;
    /// <summary>Default lifetime in seconds. Override per toast with Show(..., seconds:).</summary>
    public double Duration = 4.0;
    /// <summary>Box fill. A zero alpha draws no background.</summary>
    public SColor Background = SColor.FromArgb(230, 20, 22, 28);
    /// <summary>Box outline colour.</summary>
    public SColor Border = SColor.FromArgb(255, 60, 64, 74);
    /// <summary>Box outline thickness. 0 draws no outline.</summary>
    public float BorderThickness = 1f;
    /// <summary>Text colour.</summary>
    public SColor TextColor = SColor.FromArgb(255, 230, 232, 238);
    /// <summary>Most toasts kept at once. The oldest are dropped past this.</summary>
    public int Max = 5;
    /// <summary>Width of the coloured bar down the left edge.</summary>
    public float AccentWidth = 4f;
    /// <summary>How long the tail fade lasts, in seconds.</summary>
    public double FadeSeconds = 0.5;
    /// <summary>Stack direction. See <see cref="ToastGrow"/>.</summary>
    public ToastGrow Grow = ToastGrow.Default;
}

/// <summary>
/// Transient messages stacked at a fixed anchor, each fading near the end of its life. One host per
/// plugin: call <see cref="Show"/> from anywhere, <see cref="Draw"/> every frame in Render.
/// </summary>
public sealed class ToastHost
{
    sealed class Toast { public string Text; public ToastLevel Level; public DateTime ShownAt; public double Duration; }

    readonly List<Toast> _toasts = new();

    /// <summary>How many toasts are currently queued.</summary>
    public int Count => _toasts.Count;

    /// <summary>Drops every queued toast immediately, with no fade.</summary>
    public void Clear() => _toasts.Clear();

    /// <summary>
    /// Queues a notification. Blank text is ignored; queuing past <see cref="ToastStyle.Max"/> drops
    /// the oldest.
    /// </summary>
    /// <param name="seconds">Lifetime override. Null uses <see cref="ToastStyle.Duration"/>.</param>
    public void Show(string text, ToastStyle style, ToastLevel level = ToastLevel.Info, double? seconds = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _toasts.Add(new Toast { Text = text, Level = level, ShownAt = DateTime.Now, Duration = seconds ?? style.Duration });
        if (_toasts.Count > style.Max) _toasts.RemoveRange(0, _toasts.Count - style.Max);
    }

    /// <summary>Accent-bar colour for a level.</summary>
    public static SColor Accent(ToastLevel l) => l switch
    {
        ToastLevel.Success => SColor.FromArgb(255, 120, 210, 120),
        ToastLevel.Warning => SColor.FromArgb(255, 230, 180, 90),
        ToastLevel.Error => SColor.FromArgb(255, 230, 110, 100),
        _ => SColor.FromArgb(255, 150, 190, 240),
    };

    /// <summary>
    /// Fade multiplier for a toast: 1 until the last <paramref name="fade"/> seconds, then ramping to
    /// 0 at expiry. Clamped, and pure so it can be asserted without a live frame.
    /// </summary>
    public static float FadeAlpha(double remaining, double fade) =>
        remaining >= fade ? 1f : (float)Math.Clamp(remaining / fade, 0, 1);

    /// <summary>
    /// Whether the stack grows up from the anchor. Pulled out so the Default rule is testable
    /// without a live ImGui frame.
    /// </summary>
    public static bool GrowsUp(ToastGrow grow, float topY, float screenH) =>
        grow == ToastGrow.Up || (grow == ToastGrow.Default && topY > screenH * 0.5f);

    /// <summary>
    /// Draws the stack and expires whatever ran out. Call every frame.
    /// <para>
    /// Newest always sits nearest the bottom of the block. There is no screen-bounds clamp - a stack
    /// tall enough to run off screen is the caller's problem.
    /// </para>
    /// </summary>
    /// <param name="anchorX">Horizontal centre of the stack.</param>
    /// <param name="topY">The fixed anchor edge. <see cref="ToastStyle.Grow"/> decides which way it stacks from here.</param>
    public void Draw(float anchorX, float topY, ToastStyle st)
    {
        var now = DateTime.Now;
        _toasts.RemoveAll(x => (now - x.ShownAt).TotalSeconds >= x.Duration);
        if (_toasts.Count == 0) return;

        var dl = ImGui.GetForegroundDrawList();
        var font = ImGui.GetFont();
        var baseSize = ImGui.GetFontSize();
        if (baseSize <= 0) baseSize = 16f;
        float size = st.TextSize;
        float scale = size / baseSize;
        float pad = st.Padding;
        float lineH = (float)Math.Ceiling(size) + 4f;
        const float gap = 6f;

        (Vector2 size, List<string> rows) Measure(string text)
        {
            float maxTextW = st.MaxWidth > 0 ? Math.Max(10f, st.MaxWidth - pad * 2 - st.AccentWidth) : float.MaxValue;
            var rows = st.MaxWidth > 0 && ImGui.CalcTextSize(text).X * scale > maxTextW
                ? Text.Wrap(text, maxTextW, scale)
                : new List<string> { text };
            float contentW = 0f;
            foreach (var r in rows) contentW = Math.Max(contentW, ImGui.CalcTextSize(r).X * scale);
            return (new Vector2(contentW + pad * 2 + st.AccentWidth, rows.Count * lineH + pad * 2), rows);
        }

        // measure up front so an upward stack can place each box by its own height. <= Max toasts, so cheap.
        var boxes = new (Vector2 size, List<string> rows)[_toasts.Count];
        for (int i = 0; i < _toasts.Count; i++) boxes[i] = Measure(_toasts[i].Text);

        bool up = GrowsUp(st.Grow, topY, ImGui.GetIO().DisplaySize.Y);

        float cursor = topY;
        for (int idx = 0; idx < _toasts.Count; idx++)
        {
            // reading order stays oldest->newest top-to-bottom either way; up mode just walks the list backward
            // from the anchor so the block sits above topY.
            int i = up ? _toasts.Count - 1 - idx : idx;
            var t = _toasts[i];
            var (boxSize, rows) = boxes[i];
            float top = up ? cursor - boxSize.Y : cursor;
            cursor = up ? top - gap : top + boxSize.Y + gap;

            var remaining = t.Duration - (now - t.ShownAt).TotalSeconds;
            float alpha = FadeAlpha(remaining, st.FadeSeconds);

            var min = new Vector2(anchorX - boxSize.X / 2f, top);
            var max = min + boxSize;

            var bg = EColor.Fade(st.Background, alpha * st.Background.A / 255f);
            if (bg.A > 0) dl.AddRectFilled(min, max, EColor.U32(bg));
            dl.AddRectFilled(min, new Vector2(min.X + st.AccentWidth, max.Y),
                EColor.U32(EColor.Fade(Accent(t.Level), alpha)));
            if (st.BorderThickness > 0)
                dl.AddRect(min, max, EColor.U32(EColor.Fade(st.Border, alpha * st.Border.A / 255f)),
                    0f, ImDrawFlags.None, st.BorderThickness);

            uint textCol = EColor.U32(EColor.Fade(st.TextColor, alpha * st.TextColor.A / 255f));
            var p = new Vector2(min.X + st.AccentWidth + pad, min.Y + pad);
            foreach (var r in rows) { dl.AddText(font, size, p, textCol, r); p.Y += lineH; }
        }
    }
}

/// <summary>
/// The persistent cousin of a toast: a status strip sitting inline in the panel, with an accent bar
/// down its left edge in the level's colour. A toast is gone in four seconds; this one stays until the
/// thing it is complaining about is fixed.
/// <para>
/// Shares <see cref="ToastLevel"/> and <see cref="ToastHost.Accent"/> with the toast stack, so a
/// warning is the same amber in both places.
/// </para>
/// </summary>
public static class MessageBar
{
    /// <summary>
    /// Text wrap width inside the bar: the box, less its accent stripe, both paddings and whatever the
    /// right-hand buttons reserve. Pure, so the layout math is testable.
    /// </summary>
    /// <returns>Never under 1 - a wrap width of 0 makes ImGui break after every character.</returns>
    public static float WrapWidth(float total, float accent, float pad, float reserved) =>
        Math.Max(total - accent - pad * 2 - reserved, 1f);

    /// <summary>
    /// A bar that stays put. Use this one when the condition clears on its own and there is nothing
    /// for the user to dismiss.
    /// </summary>
    /// <param name="action">Optional button on the right - "Fix it", "Open folder". Null draws none.</param>
    /// <param name="width">0 fills the available width.</param>
    /// <returns>True the frame the action button is clicked.</returns>
    public static bool Draw(string id, ToastLevel level, string text, string action = null, float width = 0f)
    {
        bool open = true;
        return Core(id, level, text, ref open, false, action, width);
    }

    /// <summary>
    /// The same bar with a dismiss x, which clears <paramref name="open"/> when clicked. The caller
    /// owns that flag, so what "dismissed" means - this session, this map, forever - is the caller's
    /// call and not something this control decides by hiding state.
    /// </summary>
    /// <returns>True the frame the action button is clicked. Dismissal shows up in
    /// <paramref name="open"/>, so the two never collide.</returns>
    public static bool Draw(string id, ToastLevel level, string text, ref bool open,
        string action = null, float width = 0f) =>
        Core(id, level, text, ref open, true, action, width);

    static bool Core(string id, ToastLevel level, string text, ref bool open, bool dismissable,
        string action, float width)
    {
        if (!open) return false;
        var style = ImGui.GetStyle();
        string body = text ?? "";
        string act = string.IsNullOrEmpty(action) ? null : action;
        float pad = style.FramePadding.X + 2f;
        float accent = 3f;
        float total = width > 0 ? width : ImGui.GetContentRegionAvail().X;

        float actW = act != null ? ImGui.CalcTextSize(act).X + style.FramePadding.X * 2 : 0f;
        float xW = dismissable ? ImGui.GetFrameHeight() : 0f;
        // each button pays for its own leading gap, so zero buttons reserves nothing
        float reserved = actW + xW + (actW > 0f ? style.ItemSpacing.X : 0f) + (xW > 0f ? style.ItemSpacing.X : 0f);
        float wrap = WrapWidth(total, accent, pad, reserved);

        var ts = ImGui.CalcTextSize(body, false, wrap);
        float rowH = ImGui.GetFrameHeight();
        // the buttons set the floor: text shorter than one button still needs a bar tall enough to hold it
        float h = Math.Max(ts.Y, reserved > 0 ? rowH : 0f) + style.FramePadding.Y * 2;

        var org = ImGui.GetCursorScreenPos();
        var size = new Vector2(total, h);
        var dl = ImGui.GetWindowDrawList();
        SColor col = ToastHost.Accent(level);
        float r = style.FrameRounding;
        // tinted rather than filled: a solid amber slab under a settings page reads as an error itself
        dl.AddRectFilled(org, org + size, EColor.U32(EColor.Fade(col, 0.14f)), r);
        dl.AddRect(org, org + size, EColor.U32(EColor.Fade(col, 0.5f)), r);
        // the stripe is squared off on its right so it reads as an edge marker, not a rounded chip
        dl.AddRectFilled(org, new Vector2(org.X + accent + r, org.Y + h), EColor.U32(col), r);
        dl.AddRectFilled(new Vector2(org.X + accent, org.Y), new Vector2(org.X + accent + r, org.Y + h),
            EColor.U32(EColor.Fade(col, 0.14f)));
        // body in the theme's text colour, not the accent - amber body text on an amber tint is a squint
        dl.AddText(ImGui.GetFont(), ImGui.GetFontSize(),
            new Vector2(org.X + accent + pad, org.Y + style.FramePadding.Y),
            ImGui.GetColorU32(ImGuiCol.Text), body, wrap);

        bool clicked = false;
        ImGui.PushID(id);
        // real widgets on top of the drawn box, right aligned by hand - the box owns no cursor of its own
        float bx = org.X + total - pad;
        float by = org.Y + (h - rowH) * 0.5f;
        if (dismissable)
        {
            bx -= xW;
            ImGui.SetCursorScreenPos(new Vector2(bx, by));
            if (ImGui.Button("x##close", new Vector2(xW, rowH))) open = false;
            bx -= style.ItemSpacing.X;
        }
        if (act != null)
        {
            bx -= actW;
            ImGui.SetCursorScreenPos(new Vector2(bx, by));
            clicked = ImGui.Button(act + "##act", new Vector2(actW, rowH));
        }
        ImGui.PopID();

        // put the cursor back under the box, so the caller keeps stacking normally
        ImGui.SetCursorScreenPos(org);
        ImGui.Dummy(size);
        return clicked;
    }
}
