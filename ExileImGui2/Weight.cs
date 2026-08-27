using System;
using System.Collections.Generic;
using System.Globalization;
using ImGuiNET;
using SColor = System.Drawing.Color;

namespace ExileImGui2;

/// <summary>
/// Shared knobs for both weight controls. The defaults give a plain integer -10..10 slider in the
/// theme accent, with no rail. The range is set by you at the call site, never by the end user.
/// </summary>
public class WeightOpts
{
    /// <summary>Low end of the range.</summary>
    public float Min = -10f;
    /// <summary>High end of the range.</summary>
    public float Max = 10f;

    /// <summary>Readout precision. 0 gives an integer readout, 1 or more gives "4.3".</summary>
    public int Decimals;

    /// <summary>Drag granularity. 0 means 1 when <see cref="Decimals"/> is 0, otherwise continuous.</summary>
    public float Step;

    /// <summary>Flat grab colour. Null falls back to the theme's SliderGrab.</summary>
    public SColor? Flat;

    /// <summary>
    /// Colour ramp across Min..Max, lerped between N evenly spaced stops. Null falls back to
    /// <see cref="Flat"/>. See <see cref="Weight.Scales"/> for ready-made ramps.
    /// </summary>
    public SColor[] Scale;

    /// <summary>Fills from the left edge to the grab. Off by default - the grab is a marker, not a fill.</summary>
    public bool Rail;

    /// <summary>Alpha of the rail fill relative to the grab colour.</summary>
    public float RailAlpha = 0.35f;

    /// <summary>Width of the grab marker, in pixels.</summary>
    public float GrabWidth = 6f;

    /// <summary>Track height in pixels. 0 uses one frame height.</summary>
    public float TrackHeight;

    /// <summary>Readout is a typeable box. False gives plain text instead.</summary>
    public bool EditReadout = true;

    /// <summary>
    /// Shallow copy, for overriding min/max on one slider without scribbling on an opts object the
    /// caller is sharing across several.
    /// </summary>
    public WeightOpts Copy() => (WeightOpts)MemberwiseClone();
}

/// <summary>
/// List-only extras on top of <see cref="WeightOpts"/>. Every one is opt-in - a bare
/// <c>new WeightListOpts()</c> is just a label/value table.
/// </summary>
public sealed class WeightListOpts : WeightOpts
{
    /// <summary>Header text over the label column.</summary>
    public string NameHeader = "Name";
    /// <summary>Header text over the bar column.</summary>
    public string ValueHeader = "Weight";

    /// <summary>Name column width. 0 stretches it to fill; a width pins it instead.</summary>
    public float NameWidth;

    /// <summary>Bar column width, pinned by default. 0 makes it stretch and the name column pin.</summary>
    public float WeightWidth = 200f;

    /// <summary>Lets the user drag the column borders.</summary>
    public bool Resizable = true;

    /// <summary>
    /// Scroll past this height. 0 grows to fit and lets the parent window scroll instead. A scrolling
    /// list also clips: only the rows actually on screen are drawn, so a few thousand rows cost what a
    /// few dozen do. That needs every row the same height, so keep extra-column cells to one line.
    /// </summary>
    public float MaxHeight;

    /// <summary>Adds a filter box over the label column. Also matches the tooltip when there is one.</summary>
    public bool Search;

    /// <summary>Lets the user click the headers to sort. View only - your list is never touched.</summary>
    public bool Sortable;

    /// <summary>Adds row checkboxes plus a master slider that stamps every checked, visible row.</summary>
    public bool MultiSelect;
}

/// <summary>
/// Weight sliders and weight tables: the stock slider with the readout moved outside the track, a
/// thin grab, and an optional value-coloured rail.
/// </summary>
public static class Weight
{
    /// <summary>
    /// Ready-made ramps for <see cref="WeightOpts.Scale"/>, so the common case is one assignment
    /// rather than an array literal per call site.
    /// </summary>
    public static class Scales
    {
        /// <summary>Red at Min, green at Max.</summary>
        public static readonly SColor[] RedGreen =
            { SColor.FromArgb(255, 224, 103, 103), SColor.FromArgb(255, 111, 207, 122) };

        /// <summary>Red at Min, gold at the midpoint, green at Max.</summary>
        public static readonly SColor[] RedGoldGreen =
            { SColor.FromArgb(255, 224, 103, 103), SColor.FromArgb(255, 217, 193, 103), SColor.FromArgb(255, 111, 207, 122) };
    }

    /// <summary>
    /// The 0..1 position of <paramref name="v"/> in the range, clamped. A degenerate range pins left
    /// rather than dividing by zero.
    /// </summary>
    public static float Norm(float v, float min, float max)
    {
        if (max - min == 0f) return 0f;
        return Math.Clamp((v - min) / (max - min), 0f, 1f);
    }

    /// <summary>
    /// Rounds to the nearest multiple of <paramref name="step"/>. A step of 0 or less is the
    /// identity. Never returns negative zero, which the readout would print as "-0".
    /// </summary>
    public static float Snap(float v, float step)
    {
        if (step <= 0f) return v;
        float r = MathF.Round(v / step) * step;
        return r == 0f ? 0f : r;
    }

    /// <summary>
    /// The step actually in force: whatever the caller set, else 1 for integer opts, else continuous.
    /// </summary>
    public static float EffStep(WeightOpts o) =>
        o.Step > 0f ? o.Step : (o.Decimals == 0 ? 1f : 0f);

    /// <summary>
    /// Formats a value for the readout. Invariant culture on purpose - a comma decimal separator
    /// would look broken here.
    /// </summary>
    public static string Fmt(float v, int decimals) =>
        v.ToString("F" + Math.Max(0, decimals), CultureInfo.InvariantCulture);

    /// <summary>
    /// Lerps across N evenly spaced colour stops. <paramref name="t"/> is clamped: 0 is the first
    /// stop, 1 the last. An empty or null array gives a transparent colour.
    /// </summary>
    public static SColor Lerp(SColor[] stops, float t)
    {
        if (stops == null || stops.Length == 0) return SColor.FromArgb(0, 0, 0, 0);
        if (stops.Length == 1) return stops[0];
        t = Math.Clamp(t, 0f, 1f);
        float pos = t * (stops.Length - 1);
        int i = Math.Min((int)pos, stops.Length - 2);
        float f = pos - i;
        var a = stops[i];
        var b = stops[i + 1];
        return SColor.FromArgb(
            (byte)MathF.Round(a.A + (b.A - a.A) * f),
            (byte)MathF.Round(a.R + (b.R - a.R) * f),
            (byte)MathF.Round(a.G + (b.G - a.G) * f),
            (byte)MathF.Round(a.B + (b.B - a.B) * f));
    }

    /// <summary>
    /// Grab colour for a value: scale beats flat beats the theme. The scale case is the one spot the
    /// single-accent rule is off, since the colour is encoding the value itself.
    /// </summary>
    public static SColor GrabColor(float v, WeightOpts o)
    {
        if (o.Scale is { Length: > 1 }) return Lerp(o.Scale, Norm(v, o.Min, o.Max));
        if (o.Scale is { Length: 1 }) return o.Scale[0];
        if (o.Flat.HasValue) return o.Flat.Value;
        return EColor.FromVector4(ImGui.GetStyle().Colors[(int)ImGuiCol.SliderGrab]);
    }

    /// <summary>
    /// Indices of rows whose label matches the filter, in source order. Null labels never throw,
    /// they simply don't match.
    /// </summary>
    public static List<int> VisibleIndices<T>(IList<T> items, Func<T, string> label, string filter)
    {
        var outv = new List<int>();
        if (items == null) return outv;
        for (int i = 0; i < items.Count; i++)
            if (Text.Matches(filter, label?.Invoke(items[i]))) outv.Add(i);
        return outv;
    }

    /// <summary>
    /// Reorders the index list in place. The caller's items are never touched, so any authored order
    /// survives a sort.
    /// <para>
    /// Ties fall back to source order, always ascending even when the outer sort is flipped -
    /// otherwise List.Sort's instability scatters equal rows instead of keeping them put.
    /// </para>
    /// </summary>
    /// <param name="col">0 is the name column, 1 is the weight column. Anything else is a no-op.</param>
    public static void SortIndices<T>(List<int> idx, IList<T> items, Func<T, string> label,
        Func<T, float> get, int col, bool asc)
    {
        if (idx == null || items == null || (col != 0 && col != 1)) return;
        Comparison<int> cmp = col == 0
            ? (a, b) => string.Compare(label(items[a]) ?? "", label(items[b]) ?? "", StringComparison.OrdinalIgnoreCase)
            : (a, b) => get(items[a]).CompareTo(get(items[b]));
        // tie goes to source order, always ascending, even when the outer sort is flipped -
        // otherwise Sort's instability scatters the ties instead of keeping them put
        int Tiebroken(int a, int b)
        {
            int c = asc ? cmp(a, b) : cmp(b, a);
            return c != 0 ? c : a.CompareTo(b);
        }
        idx.Sort(Tiebroken);
    }

    /// <summary>
    /// Reorders <paramref name="visible"/> to match <paramref name="cached"/>, with anything not in
    /// the cache going to the back in its current order. Used to hold a sort steady while a drag is
    /// live, so a row being dragged can't re-rank itself out from under the cursor.
    /// </summary>
    public static void ApplyCachedOrder(List<int> visible, List<int> cached)
    {
        if (visible == null || cached == null || cached.Count == 0) return;
        var rest = new HashSet<int>(visible);
        var order = new List<int>(visible.Count);
        foreach (int i in cached)
            if (rest.Remove(i)) order.Add(i);
        foreach (int i in visible)
            if (rest.Contains(i)) order.Add(i);
        visible.Clear();
        visible.AddRange(order);
    }

    /// <summary>
    /// Header select-all checkbox state: are all currently visible rows checked. An empty visible set
    /// reads as unchecked, not "vacuously all".
    /// </summary>
    public static bool AllChecked(List<int> visible, HashSet<int> sel)
    {
        if (visible == null || visible.Count == 0 || sel == null) return false;
        foreach (int i in visible)
            if (!sel.Contains(i)) return false;
        return true;
    }

    /// <summary>
    /// Value the master bar should show: its cached value while the anchor row holds, reseeded from
    /// the row when the selection's first checked row moves. Without this a stale value gets stamped
    /// onto a brand new selection.
    /// </summary>
    public static float MasterValue(float cached, int seedIdx, int firstIdx, float rowValue) =>
        seedIdx == firstIdx ? cached : rowValue;

    // the whole visual. invisible button owns the hit area, everything else is drawlist.
    // readout is NOT drawn here, callers place it (standalone puts it inline, the list puts it
    // in its own column).
    static bool Bar(string id, ref float v, WeightOpts o, float width)
    {
        float h = o.TrackHeight > 0 ? o.TrackHeight : ImGui.GetFrameHeight();
        if (width < 1f) width = 1f;

        var p = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##" + id, new System.Numerics.Vector2(width, h));
        bool hovered = ImGui.IsItemHovered();
        bool active = ImGui.IsItemActive();
        _barActive = active;

        // travel excludes the grab's own width so it never overhangs either end. hoisted above the
        // drag block so the drag mapping and the paint below agree on the same geometry.
        float travel = MathF.Max(0f, width - o.GrabWidth - 2f);

        bool changed = false;
        if (active)
        {
            // cursor offset is measured from the grab's centre, not the track's left edge, else the
            // grab leads the cursor at the left end and lags it at the right end
            float t = travel <= 0f
                ? 0f
                : Math.Clamp((ImGui.GetIO().MousePos.X - (p.X + 1f + o.GrabWidth * 0.5f)) / travel, 0f, 1f);
            float nv = Snap(o.Min + t * (o.Max - o.Min), EffStep(o));
            nv = Math.Clamp(nv, MathF.Min(o.Min, o.Max), MathF.Max(o.Min, o.Max));
            if (nv != v) { v = nv; changed = true; }
        }

        var dl = ImGui.GetWindowDrawList();
        var min = p;
        var max = new System.Numerics.Vector2(p.X + width, p.Y + h);
        var col = GrabColor(v, o);

        // hover lightening matches the stock Slider frame, the mock has none but consistency wins
        dl.AddRectFilled(min, max, ImGui.GetColorU32(hovered ? ImGuiCol.FrameBgHovered : ImGuiCol.FrameBg));
        dl.AddRect(min, max, ImGui.GetColorU32(ImGuiCol.Border));

        float gx = min.X + 1f + travel * Norm(v, o.Min, o.Max);
        if (o.Rail)
            dl.AddRectFilled(new System.Numerics.Vector2(min.X + 1f, min.Y + 2f),
                new System.Numerics.Vector2(gx, max.Y - 2f),
                EColor.U32(EColor.Fade(col, o.RailAlpha)));
        dl.AddRectFilled(new System.Numerics.Vector2(gx, min.Y + 2f),
            new System.Numerics.Vector2(gx + o.GrabWidth, max.Y - 2f), EColor.U32(col));

        return changed;
    }

    /// <summary>
    /// What a typed value becomes once it lands: snapped to the step and clamped to the range. An
    /// inverted Min/Max still clamps by the real bounds.
    /// </summary>
    public static float Commit(float typed, WeightOpts o) =>
        Math.Clamp(Snap(typed, EffStep(o)), MathF.Min(o.Min, o.Max), MathF.Max(o.Min, o.Max));

    // the value slot after the track. an input box by default, plain text when EditReadout is off.
    // no mono font exists in the overlay so this is the normal font, just kept in a predictable slot.
    static bool Readout(ref float v, WeightOpts o, float width = -1f)
    {
        if (!o.EditReadout)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(Fmt(v, o.Decimals));
            return false;
        }

        // imgui owns the text buffer while the box is focused, so mid-typing garbage never reaches
        // the caller - we only take the value once the edit is done.
        float t = v;
        ImGui.SetNextItemWidth(width);
        ImGui.InputFloat("##ro", ref t, 0f, 0f, "%." + Math.Max(0, o.Decimals) + "f");
        if (!ImGui.IsItemDeactivatedAfterEdit()) return false;
        t = Commit(t, o);
        if (t == v) return false;
        v = t;
        return true;
    }

    // inline flavour for the standalone slider and the master bar. in a table cell the readout
    // fills its own column instead, so only this one needs a fixed width.
    static bool ReadoutInline(ref float v, WeightOpts o)
    {
        ImGui.SameLine(0f, ReadoutGap);
        return Readout(ref v, o, ReadoutWidth);
    }

    // wide enough for a box holding "-10.5" plus its frame padding
    const float ReadoutWidth = 64f;

    // the gap ReadoutInline's SameLine forces before the box. bar-width math below has to reserve
    // this too, or the row asks for 10px more than the window has, which any AlwaysAutoResize host
    // (Quick Edit) reads as "still too small" and grows by that much every single frame, forever.
    const float ReadoutGap = 10f;

    /// <summary>
    /// Standalone weight slider: label, track, readout.
    /// </summary>
    /// <param name="o">Null uses the defaults - integer, -10..10, theme accent, no rail.</param>
    /// <param name="id">Defaults to the label. Pass one when the label is empty or repeats.</param>
    /// <returns>True the frame the value moves, whether by drag or by typing into the readout.</returns>
    public static bool Slider(string label, ref float v, WeightOpts o = null, string id = null)
    {
        o ??= new WeightOpts();
        ImGui.PushID(id ?? label);
        if (!string.IsNullOrEmpty(label))
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(label);
            ImGui.SameLine();
        }
        float w = MathF.Max(40f, ImGui.GetContentRegionAvail().X - ReadoutWidth - ReadoutGap);
        bool changed = Bar("bar", ref v, o, w);
        changed |= ReadoutInline(ref v, o);
        ImGui.PopID();
        return changed;
    }

    /// <summary>
    /// Same slider bound through accessors, for a value that isn't a field.
    /// </summary>
    public static bool Slider(string label, Func<float> get, Action<float> set, WeightOpts o = null, string id = null)
    {
        float v = get();
        if (Slider(label, ref v, o, id)) { set(v); return true; }
        return false;
    }

    // per-id view state. transient, never persisted, same pattern ListEditor uses.
    static readonly Dictionary<string, string> _search = new();
    static readonly Dictionary<string, HashSet<int>> _sel = new();
    static readonly Dictionary<string, float> _master = new();
    static readonly Dictionary<string, int> _masterSeed = new();
    static readonly Dictionary<string, List<int>> _order = new();

    // Bar writes this right after its invisible button so List can tell whether one of ITS rows is
    // being dragged. global imgui active-state would freeze every list on screen instead.
    static bool _barActive;
    static readonly Dictionary<string, bool> _dragged = new();

    /// <summary>
    /// A table of weight rows over the caller's own collection, with optional search, view-only sort,
    /// row checkboxes and a master slider.
    /// <para>
    /// Select-all and the master slider only ever touch rows that are currently VISIBLE, so filtering
    /// and dragging the master cannot change a row you can't see. Selection is tracked by row
    /// position, not identity, so a list rebuilt or reordered between frames can leave a checked row
    /// pointing at a different item; out-of-range entries are dropped automatically.
    /// </para>
    /// </summary>
    /// <param name="id">Scopes the ImGui ids AND the retained search text, sort order and selection,
    /// so two lists sharing an id share that state. Give each list its own, even across tabs.</param>
    /// <param name="label">Row label, and the text search matches on.</param>
    /// <param name="get">Reads a row's value.</param>
    /// <param name="set">Writes a row's value back.</param>
    /// <param name="tooltip">Optional per-row tooltip on the name cell. Search matches it too.</param>
    /// <param name="extra">Columns inserted between the name and the bar, so a weight table can carry
    /// the row's other settings instead of needing a second table beside it - and so the bar and its
    /// readout stay pinned right at the same width whether a list has them or not. A column with a
    /// SortKey is click-sortable like the built-in two. Keep each cell to one line - see MaxHeight.</param>
    /// <returns>True only when a VALUE changed. Search, sort and selection are view state and never
    /// set the flag. An <paramref name="extra"/> cell reports its own edits however the caller
    /// chooses; this does not see them.</returns>
    public static bool List<T>(string id, IList<T> items, Func<T, string> label,
        Func<T, float> get, Action<T, float> set, WeightListOpts o = null, Func<T, string> tooltip = null,
        TableColumn<T>[] extra = null)
    {
        o ??= new WeightListOpts();
        if (items == null || label == null || get == null || set == null) return false;

        bool changed = false;
        ImGui.PushID(id);

        string filter = o.Search && _search.TryGetValue(id, out var sf) ? sf : "";
        if (o.Search)
        {
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputTextWithHint("##search", "Search...", ref filter, 128);
            _search[id] = filter;
        }

        var visible = tooltip == null
            ? VisibleIndices(items, label, filter)
            : VisibleIndices(items, t => label(t) + " " + tooltip(t), filter);

        var sel = o.MultiSelect
            ? (_sel.TryGetValue(id, out var s0) ? s0 : _sel[id] = new HashSet<int>())
            : null;

        // the caller's list can shrink between frames. bounds only - we can't tell if index 3 is
        // still the same row, just that it still exists.
        sel?.RemoveWhere(i => i >= items.Count);

        // master bar: only when something visible is checked. seeded from the first checked row so
        // the first drag starts where that row already sits instead of jumping.
        if (o.MultiSelect)
        {
            int picked = 0, firstIdx = -1;
            foreach (int i in visible)
                if (sel.Contains(i)) { picked++; if (firstIdx < 0) firstIdx = i; }

            if (picked > 0)
            {
                float mv = _master.TryGetValue(id, out float cached) && _masterSeed.TryGetValue(id, out int seedIdx)
                    ? MasterValue(cached, seedIdx, firstIdx, get(items[firstIdx]))
                    : get(items[firstIdx]);
                _masterSeed[id] = firstIdx;
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(picked + " selected");
                ImGui.SameLine();
                float mw = MathF.Max(40f, ImGui.GetContentRegionAvail().X - ReadoutWidth - ReadoutGap);
                if (Bar("master", ref mv, o, mw))
                {
                    // visible only. a row filtered off screen never moves.
                    foreach (int i in visible)
                        if (sel.Contains(i)) set(items[i], mv);
                    changed = true;
                }
                // typing into the master's box stamps the selection the same way a drag does
                if (ReadoutInline(ref mv, o))
                {
                    foreach (int i in visible)
                        if (sel.Contains(i)) set(items[i], mv);
                    changed = true;
                }
                _master[id] = mv;
            }
            else { _master.Remove(id); _masterSeed.Remove(id); }
        }

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersInnerV;
        if (o.Resizable) flags |= ImGuiTableFlags.Resizable;
        if (o.MaxHeight > 0f) flags |= ImGuiTableFlags.ScrollY;
        // tristate or imgui force-sorts col 0 on frame one and the caller's authored order is gone
        if (o.Sortable) flags |= ImGuiTableFlags.Sortable | ImGuiTableFlags.SortTristate;
        // a table short enough to fit its rows shows no scrollbar, so its stretch column comes out a
        // scrollbar wider than a sibling table that does scroll. reserve the width either way, so a
        // stack of these line up down the right edge.
        float outerW = 0f;
        if (o.MaxHeight > 0f && ImGui.GetFrameHeightWithSpacing() * (visible.Count + 1) <= o.MaxHeight)
            outerW = -ImGui.GetStyle().ScrollbarSize;
        var size = new System.Numerics.Vector2(outerW, o.MaxHeight);

        int nextra = extra?.Length ?? 0;
        int ncols = (o.MultiSelect ? 4 : 3) + nextra;
        if (ImGui.BeginTable("t", ncols, flags, size))
        {
            if (o.MultiSelect)
                ImGui.TableSetupColumn("##ck", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, 24f);
            // 0 means stretch, a width means pinned. default is a pinned bar column and a name
            // column that soaks up whatever the window gives it.
            ImGui.TableSetupColumn(o.NameHeader,
                o.NameWidth > 0f ? ImGuiTableColumnFlags.WidthFixed : ImGuiTableColumnFlags.WidthStretch,
                o.NameWidth);
            // extras sit between the name and the bar, so the bar and its readout stay pinned to the
            // right edge at the same width whether a list has extra columns or not
            for (int c = 0; c < nextra; c++)
                ImGui.TableSetupColumn(extra[c].Header,
                    (extra[c].Width > 0f ? ImGuiTableColumnFlags.WidthFixed : ImGuiTableColumnFlags.WidthStretch) |
                    (extra[c].SortKey == null ? ImGuiTableColumnFlags.NoSort : ImGuiTableColumnFlags.None),
                    extra[c].Width);
            ImGui.TableSetupColumn(o.ValueHeader,
                o.WeightWidth > 0f ? ImGuiTableColumnFlags.WidthFixed : ImGuiTableColumnFlags.WidthStretch,
                o.WeightWidth);
            ImGui.TableSetupColumn("##ro", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, ReadoutWidth);
            if (o.MaxHeight > 0f) ImGui.TableSetupScrollFreeze(0, 1);

            // hand-drawn header: TableHeadersRow can't hold a checkbox. TableHeader still gives us
            // the sort arrow and click target on the other columns.
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            int hc = 0;
            if (o.MultiSelect)
            {
                ImGui.TableSetColumnIndex(hc++);
                bool all = AllChecked(visible, sel);
                if (ImGui.Checkbox("##all", ref all))
                {
                    foreach (int i in visible)
                        if (all) sel.Add(i); else sel.Remove(i);
                }
            }
            ImGui.TableSetColumnIndex(hc++);
            ImGui.TableHeader(o.NameHeader);
            for (int c = 0; c < nextra; c++)
            {
                ImGui.TableSetColumnIndex(hc++);
                ImGui.TableHeader(extra[c].Header);
            }
            ImGui.TableSetColumnIndex(hc++);
            ImGui.TableHeader(o.ValueHeader);
            ImGui.TableSetColumnIndex(hc++);
            ImGui.TableHeader("##ro");

            // imgui hands us the spec, we only ever reorder our own index list
            if (o.Sortable)
            {
                var specs = ImGui.TableGetSortSpecs();
                if (specs.SpecsCount > 0)
                {
                    // freeze the order while one of THIS list's own rows is being dragged, else sorting
                    // by weight re-ranks the row under the cursor and the drag runs away from it. keyed
                    // off last frame's drag flag, not ImGui.IsAnyItemActive - that one is global to the
                    // frame and would freeze every list on screen for a drag in any of them
                    if (_dragged.TryGetValue(id, out bool held) && held
                        && _order.TryGetValue(id, out var cached))
                        ApplyCachedOrder(visible, cached);
                    else
                    {
                        var s = specs.Specs;
                        // name/weight are cols 0/1 to SortIndices, but the checkbox column shifts them
                        int col = s.ColumnIndex - (o.MultiSelect ? 1 : 0);
                        bool asc = s.SortDirection == ImGuiSortDirection.Ascending;
                        // layout is name, extras, bar, readout. SortIndices only knows name=0 and
                        // weight=1, so anything in between is an extra and the bar maps back to 1
                        if (col >= 1 && col <= nextra)
                            SortableTable.SortIndices(visible, items, extra[col - 1].SortKey, asc);
                        else
                            SortIndices(visible, items, label, get, col == 0 ? 0 : 1, asc);
                        _order[id] = new List<int>(visible);
                    }
                }
                else { _order.Remove(id); _dragged.Remove(id); }
            }

            bool rowHeld = false;

            void Row(int i)
            {
                using (Tables.Row(i))
                {
                    if (o.MultiSelect)
                    {
                        ImGui.TableNextColumn();
                        bool ck = sel.Contains(i);
                        if (ImGui.Checkbox("##ck", ref ck))
                        {
                            if (ck) sel.Add(i); else sel.Remove(i);
                        }
                    }

                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextUnformatted(label(items[i]) ?? "");
                    string tip = tooltip?.Invoke(items[i]);
                    if (!string.IsNullOrWhiteSpace(tip) && ImGui.BeginItemTooltip())
                    {
                        // row tooltips can run long (e.g. a rumour's full effect text) - wrap
                        // rather than let one row stretch a tooltip across the whole screen.
                        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 25f);
                        ImGui.TextUnformatted(tip);
                        ImGui.PopTextWrapPos();
                        ImGui.EndTooltip();
                    }

                    for (int c = 0; c < nextra; c++)
                    {
                        ImGui.TableNextColumn();
                        // Draw is a free callback, so per-column ids too or two columns sharing one
                        // cell helper draw the same widget id twice in a row and share its state
                        ImGui.PushID(c);
                        extra[c].Draw?.Invoke(items[i], i);
                        ImGui.PopID();
                    }

                    ImGui.TableNextColumn();
                    float v = get(items[i]);
                    if (Bar("b", ref v, o, ImGui.GetContentRegionAvail().X)) { set(items[i], v); changed = true; }
                    if (_barActive) rowHeld = true;

                    ImGui.TableNextColumn();
                    if (Readout(ref v, o)) { set(items[i], v); changed = true; }
                }
            }

            // only a scrolling table can clip - without ScrollY there is no viewport to clip against.
            // needs uniform row heights, which a weight row has as long as an extra cell stays one line
            if (o.MaxHeight > 0f)
            {
                ImGuiListClipperPtr clipper;
                unsafe { clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper()); }
                clipper.Begin(visible.Count);
                while (clipper.Step())
                    for (int k = clipper.DisplayStart; k < clipper.DisplayEnd; k++)
                        Row(visible[k]);
                clipper.End();
                clipper.Destroy();
            }
            else
            {
                foreach (int i in visible) Row(i);
            }

            _dragged[id] = rowHeld;
            ImGui.EndTable();
        }

        ImGui.PopID();
        return changed;
    }
}
