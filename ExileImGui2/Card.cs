using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;

namespace ExileImGui2;

/// <summary>
/// What a <see cref="Card.Draw"/> did this frame.
/// </summary>
public readonly struct CardResult
{
    /// <summary>The body reported an edit. Fold this into your dirty flag.</summary>
    public readonly bool Changed;

    /// <summary>The close X fired this frame. The caller decides what to drop.</summary>
    public readonly bool Closed;

    /// <summary>Pairs the two flags a card reports.</summary>
    public CardResult(bool changed, bool closed) { Changed = changed; Closed = closed; }
}

/// <summary>
/// How a card's box is painted.
/// </summary>
public enum CardLook
{
    /// <summary>Border only, no fill. What <see cref="Card.Draw"/> has always drawn.</summary>
    Outline,
    /// <summary>A raised surface: filled a step away from the window colour, plus the border.</summary>
    Filled,
    /// <summary>Neither fill nor border - just the padding. For grouping without drawing a box.</summary>
    Subtle,
}

/// <summary>
/// Bordered, rounded settings "card": a title header over a caller-drawn body, optionally
/// collapsible and closeable. The boxed cousin of <see cref="ListEditor.Cards"/>, which uses a bare
/// header instead.
/// <para>
/// Three shapes, and they do not mix: <see cref="Draw"/> is title-plus-body in one call,
/// <see cref="BeginCard"/> is the collapsible one whose header row is the disclosure, and
/// <see cref="Surface"/> is a plain box with no header of its own, composed out of
/// <see cref="Header"/> and <see cref="Footer"/> the way Fluent's Card is.
/// </para>
/// </summary>
public static class Card
{
    // perceptual luma, same weights EColor.Contrast uses
    static float Luma(Vector4 c) => c.X * 0.299f + c.Y * 0.587f + c.Z * 0.114f;

    // move every channel by d, clamped, alpha untouched
    static Vector4 Shift(Vector4 c, float d) => new(
        Math.Clamp(c.X + d, 0f, 1f), Math.Clamp(c.Y + d, 0f, 1f), Math.Clamp(c.Z + d, 0f, 1f), c.W);

    /// <summary>
    /// Nudges a surface away from the window colour: lighter on a dark theme, darker on a light one,
    /// so a filled card reads as raised under either. Pure, so the rule is testable.
    /// </summary>
    /// <param name="amount">How far to move each channel, 0..1.</param>
    /// <returns>The shifted colour, clamped, with alpha untouched.</returns>
    public static Vector4 Raise(Vector4 bg, float amount) =>
        Shift(bg, Luma(bg) > 0.5f ? -amount : amount);

    /// <summary>
    /// Fill for a filled card: <see cref="Raise"/> off the window colour, then pushed further if that
    /// landed on the control background. Most themes put FrameBg a step off WindowBg in the same
    /// direction a card wants to go, so a plain raise lands on the colour the inputs inside the card
    /// already use and the whole thing reads as one flat slab. Pure.
    /// </summary>
    /// <param name="frame">The control background, normally <c>ImGuiCol.FrameBg</c>.</param>
    /// <param name="margin">How much luma has to separate the card from the controls sitting on it.</param>
    /// <returns>A fill clear of both, so the controls read as recessed into the card.</returns>
    public static Vector4 SurfaceFill(Vector4 window, Vector4 frame, float step = 0.055f, float margin = 0.05f)
    {
        float dir = Luma(window) > 0.5f ? -1f : 1f;
        var c = Shift(window, dir * step);
        float gap = (Luma(c) - Luma(frame)) * dir;   // positive once the card is past the frame colour
        return gap >= margin ? c : Shift(c, dir * (margin - gap));
    }

    // pushes the box style for a look and reports how many style COLOURS went with it, so the matching
    // pop count is never guessed at. shared by Draw and Surface.
    static int PushLook(CardLook look, float padX, float padY, float rounding)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(padX, padY));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, rounding);
        if (look != CardLook.Filled) return 0;
        // measured off BOTH the window and the control background - plenty of themes park WindowBg,
        // ChildBg and FrameBg within a few percent of each other, and a card that lands between them
        // reads as the same slab as the inputs sitting on it.
        var c = ImGui.GetStyle().Colors;
        ImGui.PushStyleColor(ImGuiCol.ChildBg,
            SurfaceFill(c[(int)ImGuiCol.WindowBg], c[(int)ImGuiCol.FrameBg]));
        return 1;
    }

    static ImGuiChildFlags LookFlags(CardLook look) =>
        (look == CardLook.Subtle ? ImGuiChildFlags.None : ImGuiChildFlags.Border)
        // AutoResizeY -> height hugs the content; x=0 still fills the available width. AlwaysAutoResize keeps
        // measuring while the card is scrolled out of view, otherwise imgui culls the body and it folds flat.
        | ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.AlwaysAutoResize;

    /// <summary>
    /// One card. The header floats above the box; see <see cref="BeginCard"/> for the variant where
    /// the header row is itself the disclosure.
    /// </summary>
    /// <param name="body">Runs when expanded and returns its own changed flag.</param>
    /// <param name="collapsible">Turns the title into a collapsing header.</param>
    /// <param name="closeable">Adds the close X that sets <see cref="CardResult.Closed"/>.</param>
    /// <param name="look">Defaults to the outline this has always drawn, so existing calls are
    /// untouched.</param>
    public static CardResult Draw(string id, string title, Func<bool> body,
        bool collapsible = false, bool closeable = false, bool defaultOpen = true,
        CardLook look = CardLook.Outline)
    {
        bool changed = false, closed = false;
        ImGui.PushID(id);

        int cols = PushLook(look, 8f, 6f, 4f);
        ImGui.BeginChild("##c", Vector2.Zero, LookFlags(look));

        if (DrawHeader(title, collapsible, closeable, defaultOpen, ref closed))
            changed = body();

        ImGui.EndChild();
        ImGui.PopStyleColor(cols);
        ImGui.PopStyleVar(2);
        ImGui.PopID();
        return new CardResult(changed, closed);
    }

    // ---- plain surface, composed like fluent's Card / CardHeader / CardFooter ----

    /// <summary>
    /// Closes a <see cref="Surface"/>. Held by the <c>using</c>, so there is no End call to forget and
    /// no way to pair it with the wrong one.
    /// </summary>
    public readonly struct CardScope : IDisposable
    {
        readonly int _colors;

        internal CardScope(int colors) { _colors = colors; }

        /// <summary>Ends the card box and pops exactly what <see cref="Surface"/> pushed.</summary>
        public void Dispose()
        {
            ImGui.EndChild();
            ImGui.PopStyleColor(_colors);
            ImGui.PopStyleVar(2);
            ImGui.PopID();
        }
    }

    /// <summary>
    /// A plain card: a padded, rounded box with no header, no disclosure arrow and no close X. You draw
    /// the inside, usually starting with <see cref="Header"/> and ending with <see cref="Footer"/>,
    /// which is how Fluent's Card is put together.
    /// <para>
    /// Scoped rather than Begin/End on purpose - <see cref="BeginCard"/> and this one push different
    /// things, and a <c>using</c> can't be paired with the wrong closer.
    /// </para>
    /// <code>
    /// using (Card.Surface("stats", CardLook.Filled))
    /// {
    ///     Card.Header("Loot stats", "counted since the last town trip");
    ///     changed |= Controls.SliderInt("Window", ref _window, 1, 60);
    ///     Card.Footer(() => { if (ImGui.SmallButton("Reset")) Reset(); });
    /// }
    /// </code>
    /// </summary>
    /// <param name="width">0 fills the available width, which is what a settings column wants.</param>
    /// <param name="padding">Inset on all four sides. Fluent's is roomier than a stock ImGui frame.</param>
    public static CardScope Surface(string id, CardLook look = CardLook.Filled, float width = 0f,
        float padding = 10f)
    {
        ImGui.PushID(id);
        int cols = PushLook(look, padding, padding, 6f);
        ImGui.BeginChild("##surface", new Vector2(width, 0f), LookFlags(look));
        return new CardScope(cols);
    }

    /// <summary>
    /// Title line, an optional dim description under it, and an optional right-aligned action slot.
    /// Fluent's CardHeader, minus the avatar.
    /// </summary>
    /// <param name="description">Wraps at the card's inner edge. Null draws no second line.</param>
    /// <param name="action">Widgets pinned to the right of the TITLE row - an icon button, a badge, a
    /// menu button. Null draws none.</param>
    public static void Header(string title, string description = null, Action action = null)
    {
        ImGui.TextUnformatted(title ?? "");
        if (action != null)
        {
            Layout.BeginTrailing("##cardhead", 0f);
            action();
            Layout.EndTrailing();
        }
        if (string.IsNullOrEmpty(description)) return;
        // TextUnformatted, not TextDisabled/TextWrapped - those are printf-style and would eat a % in a
        // caller's string. this one honours the wrap pos and takes the text as-is.
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled));
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(description);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }

    /// <summary>
    /// A rule and then your action row, at the bottom of a <see cref="Surface"/>. Fluent's CardFooter.
    /// </summary>
    public static void Footer(Action body)
    {
        if (body == null) return;
        ImGui.Dummy(new Vector2(0f, 2f));
        ImGui.Separator();
        ImGui.Dummy(new Vector2(0f, 2f));
        body();
    }

    /// <summary>
    /// One boxed card per item, where the header X deletes that item. No drag-reorder on purpose -
    /// reach for <see cref="ListEditor.Cards"/> when you need reordering.
    /// </summary>
    /// <param name="title">Header text for an item.</param>
    /// <param name="body">Draws one item's fields, given the item and its index.</param>
    /// <param name="add">Optional add row. Null draws none.</param>
    /// <param name="confirmDelete">Routes deletes through a confirm modal.</param>
    /// <returns>True when anything was added, edited or removed.</returns>
    public static bool List<T>(string id, List<T> list, Func<T, string> title,
        Func<T, int, bool> body, AddSource<T> add = null,
        bool collapsible = false, bool confirmDelete = false)
    {
        bool d = false;
        ImGui.PushID(id);
        d |= ListEditor.DrawAdd(id, list, add);

        int toRemove = -1;
        for (int i = 0; i < list.Count; i++)
        {
            // body runs synchronously inside Draw, so capturing the loop i here is safe.
            var r = Draw(i.ToString(), title(list[i]), () => body(list[i], i),
                collapsible: collapsible, closeable: true);
            d |= r.Changed;
            if (r.Closed) toRemove = i;
        }

        d |= ListEditor.ApplyDelete(id, list, toRemove, confirmDelete);
        ImGui.PopID();
        return d;
    }

    // open state per BeginCard, so EndCard knows whether it has body padding to undo. a stack because
    // cards nest in principle; in practice it's one deep.
    static readonly Stack<bool> _open = new();

    /// <summary>
    /// A card whose FIRST ROW is the disclosure: arrow, title, and a right-aligned trailing slot, with
    /// the body inside the same bordered box, so header and body read as one object. Collapsed leaves
    /// just the header row.
    /// <para>
    /// Begin/End contract, same as <see cref="Windows.Begin"/>: ALWAYS call <see cref="EndCard"/>,
    /// including when this returns false.
    /// </para>
    /// </summary>
    /// <param name="trailing">Widgets pinned to the right of the header - chips, a size readout,
    /// latch toggles. Stays clickable; the header does not swallow the click.</param>
    /// <param name="menu">Right-click popup body for the header.</param>
    /// <param name="setOpen">Drives the open state from your own set, which is what makes expand-all
    /// and collapse-all one line each. The return value is the state AFTER a click, so write it
    /// straight back. Null lets ImGui remember it instead.</param>
    /// <returns>True when the body should draw.</returns>
    public static bool BeginCard(string id, string title, Action trailing = null, Action menu = null,
        bool? setOpen = null, bool defaultOpen = false)
    {
        ImGui.PushID(id);
        // zero window padding so the framed header fills the card edge to edge; the body gets its own indent.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        // AlwaysAutoResize: same story as Card.Draw, a culled child measures as empty and the box folds flat.
        ImGui.BeginChild("##card", Vector2.Zero,
            ImGuiChildFlags.Border | ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.AlwaysAutoResize);

        // Framed makes the header a filled bar; SpanFullWidth widens the hit area to the whole card;
        // AllowOverlap keeps the trailing widgets clickable instead of the header swallowing the click;
        // NoTreePushOnOpen means there's no TreePop to pair.
        var flags = ImGuiTreeNodeFlags.Framed | ImGuiTreeNodeFlags.SpanFullWidth
                  | ImGuiTreeNodeFlags.AllowOverlap | ImGuiTreeNodeFlags.NoTreePushOnOpen;
        if (defaultOpen) flags |= ImGuiTreeNodeFlags.DefaultOpen;
        if (setOpen.HasValue) ImGui.SetNextItemOpen(setOpen.Value);

        // ###h keys the node on "h" so renaming the card doesn't reset its open state mid-type.
        bool open = ImGui.TreeNodeEx(title + "###h", flags);
        if (menu != null && ImGui.BeginPopupContextItem("##ctx")) { menu(); ImGui.EndPopup(); }
        if (trailing != null)
        {
            Layout.BeginTrailing(id + "_trail", 6f);
            trailing();
            Layout.EndTrailing();
        }

        _open.Push(open);
        if (open) { ImGui.Indent(8f); ImGui.Dummy(new Vector2(0, 2f)); }
        return open;
    }

    /// <summary>
    /// Closes a <see cref="BeginCard"/>. Must be called whatever BeginCard returned, or the card's
    /// open-state stack desyncs for the rest of the session.
    /// </summary>
    public static void EndCard()
    {
        if (_open.Count > 0 && _open.Pop()) { ImGui.Dummy(new Vector2(0, 4f)); ImGui.Unindent(8f); }
        ImGui.EndChild();
        ImGui.PopStyleVar(1);
        ImGui.PopID();
    }

    // draws the header and returns whether the body should draw. sets closed if the x fired this frame.
    static bool DrawHeader(string title, bool collapsible, bool closeable, bool defaultOpen, ref bool closed)
    {
        var flags = defaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;

        if (collapsible)
        {
            // ###h keys the header on "h" so editing the title doesn't reset the collapse state mid-frame.
            if (closeable)
            {
                bool keep = true;
                bool open = ImGui.CollapsingHeader(title + "###h", ref keep, flags);
                if (!keep) closed = true;
                return open;
            }
            return ImGui.CollapsingHeader(title + "###h", flags);
        }

        // static title: right-align the x, then a rule under it to split header from body.
        ImGui.TextUnformatted(title);
        if (closeable)
        {
            ImGui.SameLine(ImGui.GetContentRegionMax().X - ImGui.GetFrameHeight());
            if (ImGui.SmallButton("x")) closed = true;
        }
        ImGui.Separator();
        return true;
    }
}
