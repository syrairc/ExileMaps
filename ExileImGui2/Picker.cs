using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using ImGuiNET;
using SColor = System.Drawing.Color;

namespace ExileImGui2;

/// <summary>
/// Everything optional about a <see cref="Picker"/> popup. Build one once and cache it - the three
/// projections run per row per frame, so they want to be cheap and allocation free.
/// <para>
/// Only <see cref="Primary"/> has a default (<c>ToString()</c>). A null <see cref="Secondary"/> drops
/// the muted second line, and a null <see cref="Category"/> drops both the pill column and the
/// category filter - the pill is opt-in because most lists have nothing to put in it.
/// </para>
/// </summary>
/// <typeparam name="T">Your own row type. The picker never reshapes it, it just projects strings out
/// of it, so an existing record or tuple works as-is.</typeparam>
public sealed class PickerOpts<T>
{
    /// <summary>The main line, drawn in the normal text colour and word-wrapped. Defaults to
    /// <c>ToString()</c>.</summary>
    public Func<T, string> Primary;

    /// <summary>The muted line under it, also word-wrapped. Null draws no second line.</summary>
    public Func<T, string> Secondary;

    /// <summary>
    /// Pill text for the right-hand column - a group, a tier, an affix type. Null means no pill and
    /// no category filter, which is the default.
    /// </summary>
    public Func<T, string> Category;

    /// <summary>Pill dot colour. Null hashes the category name through
    /// <see cref="EColor.CategoryColor"/>, so the same group keeps the same colour run to run.</summary>
    public Func<T, SColor> CategoryColor;

    /// <summary>
    /// The text the search box matches against. Null matches each term against the primary, secondary
    /// and category lines separately, which needs no prebuilt string - pass one only if you already
    /// keep a folded search key on the row.
    /// </summary>
    public Func<T, string> Search;

    /// <summary>
    /// Identity for the ImGui id and for multi-select membership. Defaults to
    /// <see cref="Primary"/>, so rows whose primary text repeats need one of their own.
    /// </summary>
    public Func<T, string> Key;

    /// <summary>Draw the search box. False hides it, for a list short enough not to need one.</summary>
    public bool ShowSearch = true;

    /// <summary>Draw the category filter. Ignored when <see cref="Category"/> is null, and the row
    /// hides itself anyway when every item shares one category.</summary>
    public bool ShowCategoryFilter = true;

    /// <summary>
    /// While the filter is on "All", sort by category and head each run with its name. False keeps
    /// your own order and draws no headers.
    /// </summary>
    public bool GroupByCategory = true;

    /// <summary>Placeholder inside the search box.</summary>
    public string Hint = "Search...";

    /// <summary>Text behind the <c>(?)</c> next to the search box. Null uses the default blurb about
    /// the search rules.</summary>
    public string Tip;

    /// <summary>Shown in place of the rows when the source list is empty.</summary>
    public string EmptyText = "nothing to pick from.";

    /// <summary>
    /// Popup size, and a fixed one - a popup auto-fits its content, so there is no drag-to-resize to
    /// leave it to and no sensible "fill the rest" either. Pick a size that suits your rows.
    /// </summary>
    public Vector2 Size = new(560f, 420f);

    /// <summary>Width of the pill column.</summary>
    public float CategoryWidth = 130f;

    /// <summary>
    /// Row cap. Past it the list stops and says how many it dropped - there is no clipper here, so a
    /// list of thousands is drawn in full and costs it. Narrowing the search is the intended answer.
    /// </summary>
    public int MaxRows = 300;

    /// <summary>Label on the multi-select commit button. The count is appended.</summary>
    public string CommitLabel = "Add";
}

/// <summary>
/// A picker's caller-owned state: what is typed in the search box, which category is showing, and
/// what a multi-select has gathered so far. Declare one field per picker and pass it by ref -
/// <see cref="Picker"/> creates it on first use, so there is nothing to initialise.
/// </summary>
/// <typeparam name="T">Same row type as the <see cref="PickerOpts{T}"/> beside it.</typeparam>
public sealed class PickerState<T>
{
    /// <summary>Current search text.</summary>
    public string Filter = "";

    /// <summary>Current category, or <see cref="Picker.AnyCategory"/> for all of them.</summary>
    public string Category = Picker.AnyCategory;

    /// <summary>What a multi-select picker has ticked. Empty in single-select.</summary>
    public readonly List<T> Selected = new();
}

/// <summary>
/// A searchable pick list in a popup: search box, optional category filter, and a scrolling list of
/// two-line rows with an optional coloured pill on the right. Both text lines wrap, because these
/// lists hold whole sentences and truncating them makes them unreadable.
/// <para>
/// A popup rather than a child panel, so it draws over whatever is behind it and does not push the
/// page around. Open it with <c>ImGui.OpenPopup(id)</c> and call <see cref="Popup{T}"/> every frame,
/// or let <see cref="Button{T}"/> do both.
/// </para>
/// <code>
/// // one line, when the rows are strings and all you want is search
/// if (Picker.Button("Add mod", _modNames, ref _pick, out var name)) _rules.Add(name);
///
/// // the full shape: two lines per row and a coloured group pill
/// static readonly PickerOpts&lt;Mod&gt; ModPick = new()
/// {
///     Primary   = m =&gt; m.Text,
///     Secondary = m =&gt; m.Affix + " - " + m.Id,
///     Category  = m =&gt; m.Group,
///     Hint      = "Search mods...",
/// };
///
/// if (ImGui.Button("Add mod")) ImGui.OpenPopup("addmod");
/// if (Picker.Popup("addmod", _mods, ref _pick, out var mod, ModPick)) _rules.Add(mod.Id);
/// </code>
/// </summary>
public static class Picker
{
    /// <summary>The category filter's "everything" entry.</summary>
    public const string AnyCategory = "All";

    const string DefaultTip = "Each space-separated word has to match somewhere in the row, in any " +
                              "order. A word is treated as a regex when it is one.";

    // one shared all-defaults opts per T, so passing null doesn't allocate every frame
    static class Defaults<T>
    {
        public static readonly PickerOpts<T> Value = new();
    }

    /// <summary>
    /// The search rule: every space-separated word has to match somewhere, in any order. A word that
    /// compiles as a regex is used as one, and a half-typed pattern falls back to a plain
    /// case-insensitive contains so the list doesn't blank out mid-keystroke.
    /// </summary>
    /// <returns>True when <paramref name="text"/> satisfies every word in <paramref name="filter"/>.
    /// An empty filter matches everything; a null text matches nothing but never throws.</returns>
    public static bool Matches(string filter, string text)
    {
        foreach (var m in Terms(filter))
            if (!m(text ?? "")) return false;
        return true;
    }

    /// <summary>
    /// The picker popup, single-select. Clicking a row commits it and closes.
    /// </summary>
    /// <param name="id">Popup id. The same string you passed to <c>ImGui.OpenPopup</c>.</param>
    /// <param name="items">Your rows, in your own order. Never mutated or reordered.</param>
    /// <param name="state">Caller-owned search and filter state. Created on first use.</param>
    /// <param name="picked">The row that was clicked, or <c>default</c>.</param>
    /// <param name="opts">Null takes every default: one line per row, no pill, no category filter.</param>
    /// <returns>True the frame a row is picked.</returns>
    public static bool Popup<T>(string id, IEnumerable<T> items, ref PickerState<T> state, out T picked,
        PickerOpts<T> opts = null) =>
        Draw(id, items, ref state, false, opts, out picked, out _);

    /// <summary>
    /// The same popup in multi-select: rows toggle instead of committing, and a footer holds the
    /// commit button, a Clear and the running selection.
    /// </summary>
    /// <param name="picked">A copy of everything ticked, on the frame the commit button is pressed.
    /// Null otherwise.</param>
    /// <returns>True the frame the commit button is pressed.</returns>
    public static bool PopupMulti<T>(string id, IEnumerable<T> items, ref PickerState<T> state, out List<T> picked,
        PickerOpts<T> opts = null) =>
        Draw(id, items, ref state, true, opts, out _, out picked);

    /// <summary>
    /// A button that opens the picker, for the common "Add ..." row. Same control as
    /// <see cref="Popup{T}"/> with the <c>OpenPopup</c> wired up.
    /// </summary>
    /// <param name="id">Defaults to the label, so two buttons with the same text need one.</param>
    /// <returns>True the frame a row is picked.</returns>
    public static bool Button<T>(string label, IEnumerable<T> items, ref PickerState<T> state, out T picked,
        PickerOpts<T> opts = null, string id = null)
    {
        id ??= label;
        if (ImGui.Button(label + "##" + id)) ImGui.OpenPopup(id);
        return Popup(id, items, ref state, out picked, opts);
    }

    /// <summary>
    /// The same button over <see cref="PopupMulti{T}"/>.
    /// </summary>
    /// <returns>True the frame the commit button is pressed.</returns>
    public static bool ButtonMulti<T>(string label, IEnumerable<T> items, ref PickerState<T> state, out List<T> picked,
        PickerOpts<T> opts = null, string id = null)
    {
        id ??= label;
        if (ImGui.Button(label + "##" + id)) ImGui.OpenPopup(id);
        return PopupMulti(id, items, ref state, out picked, opts);
    }

    static bool Draw<T>(string id, IEnumerable<T> items, ref PickerState<T> state, bool multi,
        PickerOpts<T> o, out T picked, out List<T> committed)
    {
        picked = default;
        committed = null;
        state ??= new PickerState<T>();
        o ??= Defaults<T>.Value;

        // style pushed outside the popup so the popup's own rounding takes it too. the using disposes
        // on every exit below, including the early one.
        using var _style = new Controls.PanelStyleScope();
        if (!ImGui.BeginPopup(id)) return false;

        // a reopened picker starts clean rather than resuming a search nobody remembers typing. read off
        // the popup, before the child below - a child has an appearing frame of its own.
        bool appearing = ImGui.IsWindowAppearing();
        if (appearing)
        {
            state.Filter = "";
            state.Category = AnyCategory;
            state.Selected.Clear();
        }

        // BeginPopup forces AlwaysAutoResize, so the window measures its content every frame. anything
        // inside that sizes itself off the available region is then circular and walks a few pixels per
        // frame - which is why the whole body sits in one child of a fixed size. everything below can
        // ask for "the rest" safely because this child is what it's asking about.
        ImGui.BeginChild("##body", o.Size);

        var all = items as IReadOnlyList<T> ?? items?.ToList() ?? (IReadOnlyList<T>)Array.Empty<T>();
        var prim = o.Primary ?? (x => x?.ToString() ?? "");
        var key = o.Key ?? prim;
        bool hasCat = o.Category != null;

        if (all.Count == 0)
        {
            ImGui.TextDisabled(o.EmptyText);
            ImGui.EndChild();
            ImGui.EndPopup();
            return false;
        }

        var matches = Header(all, state, o, prim, hasCat, appearing);

        bool acted = false;
        float footer = multi ? ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y : 0f;
        ImGui.BeginChild("##rows", new Vector2(0f, -footer));
        Rows(matches, state, o, prim, key, hasCat, multi, ref picked, ref acted);
        ImGui.EndChild();
        if (acted) ImGui.CloseCurrentPopup();

        if (multi)
        {
            ImGui.BeginDisabled(state.Selected.Count == 0);
            if (ImGui.Button(o.CommitLabel + " (" + state.Selected.Count + ")"))
            {
                committed = new List<T>(state.Selected);
                acted = true;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Clear")) state.Selected.Clear();
            ImGui.SameLine();
            if (ImGui.Button("Close")) ImGui.CloseCurrentPopup();
            if (state.Selected.Count > 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(Esc(string.Join(", ", state.Selected.Select(key))));
            }
        }

        ImGui.EndChild();
        ImGui.EndPopup();
        return acted;
    }

    // search box, count and category filter. returns the rows that survive, in draw order.
    static List<T> Header<T>(IReadOnlyList<T> all, PickerState<T> state, PickerOpts<T> o,
        Func<T, string> prim, bool hasCat, bool appearing)
    {
        if (o.ShowSearch)
        {
            ImGui.SetNextItemWidth(-90f);
            if (appearing) ImGui.SetKeyboardFocusHere();
            ImGui.InputTextWithHint("##pickfilter", o.Hint, ref state.Filter, 128);
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            Controls.Tip(o.Tip ?? DefaultTip);
        }

        var cats = hasCat && o.ShowCategoryFilter ? Categories(all, o.Category) : null;
        if (cats != null && cats.Length > 2)
        {
            // a handful of short groups read better as one row of buttons than as a dropdown
            bool seg = cats.Length <= 4 && cats.All(c => c.Length <= 12);
            if (seg) Controls.Segmented("##pickcat", ref state.Category, cats.Select(c => (c, c)).ToArray());
            else
            {
                ImGui.SetNextItemWidth(200f);
                if (ImGui.BeginCombo("##pickcat", state.Category))
                {
                    foreach (var c in cats)
                        if (ImGui.Selectable(Esc(c), c == state.Category)) state.Category = c;
                    ImGui.EndCombo();
                }
            }
            ImGui.SameLine();
        }
        else if (o.ShowSearch) ImGui.SameLine();

        var matches = Filter(all, state, o, hasCat);
        ImGui.TextDisabled(matches.Count + " of " + all.Count);
        ImGui.Separator();

        // grouped view sorts by category so a run of one group draws under one header
        if (hasCat && o.GroupByCategory && state.Category == AnyCategory)
            matches.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(o.Category(a) ?? "", o.Category(b) ?? "");
                return c != 0 ? c : string.CompareOrdinal(prim(a) ?? "", prim(b) ?? "");
            });
        return matches;
    }

    static List<T> Filter<T>(IReadOnlyList<T> all, PickerState<T> state, PickerOpts<T> o, bool hasCat)
    {
        var terms = Terms(state.Filter);
        bool anyCat = !hasCat || state.Category == AnyCategory;
        var outp = new List<T>();
        foreach (var it in all)
        {
            if (!anyCat && (o.Category(it) ?? "") != state.Category) continue;
            if (!RowMatches(it, o, terms)) continue;
            outp.Add(it);
        }
        return outp;
    }

    // no prebuilt search key needed: a term may land on any one of the three lines
    static bool RowMatches<T>(T it, PickerOpts<T> o, List<Func<string, bool>> terms)
    {
        if (terms.Count == 0) return true;
        if (o.Search != null)
        {
            var s = o.Search(it) ?? "";
            foreach (var m in terms)
                if (!m(s)) return false;
            return true;
        }
        var a = (o.Primary != null ? o.Primary(it) : it?.ToString()) ?? "";
        var b = o.Secondary?.Invoke(it) ?? "";
        var c = o.Category?.Invoke(it) ?? "";
        foreach (var m in terms)
            if (!m(a) && !m(b) && !m(c)) return false;
        return true;
    }

    static void Rows<T>(List<T> matches, PickerState<T> state, PickerOpts<T> o, Func<T, string> prim,
        Func<T, string> key, bool hasCat, bool multi, ref T picked, ref bool acted)
    {
        // the child above owns the scrolling, the table just does the banding and the row rules
        int cols = hasCat ? 2 : 1;
        if (!ImGui.BeginTable("##pickrows", cols, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
            return;
        ImGui.TableSetupColumn("row", ImGuiTableColumnFlags.WidthStretch);
        if (hasCat) ImGui.TableSetupColumn("cat", ImGuiTableColumnFlags.WidthFixed, o.CategoryWidth);

        bool grouped = hasCat && o.GroupByCategory && state.Category == AnyCategory;
        string curGroup = null;
        int shown = 0;
        foreach (var it in matches)
        {
            if (shown++ >= o.MaxRows) break;
            var cat = hasCat ? o.Category(it) ?? "" : null;
            if (grouped && cat != curGroup)
            {
                curGroup = cat;
                if (cat.Length > 0) Note(cat.ToUpperInvariant());   // uncategorised rows get no header
            }

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.PushID(key(it) ?? "");
            if (Row(it, prim, o, cat, multi && IndexOf(state.Selected, it, key) >= 0))
            {
                // the close is left to the caller above: this runs inside the scrolling child, and
                // CloseCurrentPopup wants to be on the popup itself
                if (!multi) { picked = it; acted = true; }
                else
                {
                    int at = IndexOf(state.Selected, it, key);
                    if (at >= 0) state.Selected.RemoveAt(at);
                    else state.Selected.Add(it);
                }
            }
            ImGui.PopID();
        }

        if (matches.Count > o.MaxRows) Note((matches.Count - o.MaxRows) + " more - narrow the search.");
        if (matches.Count == 0) Note("nothing matches that.");
        ImGui.EndTable();
    }

    // one row: wrapped primary, wrapped muted secondary, pill right-aligned in its own column. the
    // whole thing is a zero-label selectable sized to the measured text, with the text drawn back
    // over the top of it.
    static bool Row<T>(T it, Func<T, string> prim, PickerOpts<T> o, string cat, bool selected)
    {
        var a = prim(it) ?? "";
        var b = o.Secondary?.Invoke(it) ?? "";
        float wrap = Math.Max(80f, ImGui.GetContentRegionAvail().X - 8f);

        // measured on the raw text: TextWrapped expands %% back to one % before it wraps
        float h = ImGui.CalcTextSize(a, false, wrap).Y;
        if (b.Length > 0) h += ImGui.CalcTextSize(b, false, wrap).Y;

        var start = ImGui.GetCursorPos();
        bool hit = ImGui.Selectable("##r", selected, ImGuiSelectableFlags.SpanAllColumns, new Vector2(0f, h));

        ImGui.SetCursorPos(start);
        ImGui.PushTextWrapPos(start.X + wrap);
        ImGui.TextWrapped(Esc(a));
        if (b.Length > 0)
            using (new EColor.StyleColorScope((ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled))))
                ImGui.TextWrapped(Esc(b));
        ImGui.PopTextWrapPos();

        if (cat != null && cat.Length > 0)
        {
            ImGui.TableSetColumnIndex(1);
            float pw = Chips.BadgeWidth(cat, dot: true);
            float avail = ImGui.GetContentRegionAvail().X;
            if (avail > pw) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (avail - pw));
            Chips.Badge(cat, o.CategoryColor != null ? o.CategoryColor(it) : EColor.CategoryColor(cat), dot: true);
        }
        return hit;
    }

    static void Note(string text)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextDisabled(Esc(text));
    }

    /// <summary>
    /// The category filter's entries: "All" then every distinct category, sorted. Pure, so the
    /// dedup and the leading entry can be asserted without a frame.
    /// </summary>
    internal static string[] Categories<T>(IReadOnlyList<T> all, Func<T, string> category)
    {
        // runs every frame the popup is open, so the dedup is a set rather than a list scan
        var uniq = new HashSet<string>();
        var seen = new List<string>();
        foreach (var it in all)
        {
            var c = category(it) ?? "";
            if (c.Length > 0 && uniq.Add(c)) seen.Add(c);
        }
        seen.Sort(StringComparer.OrdinalIgnoreCase);
        seen.Insert(0, AnyCategory);
        return seen.ToArray();
    }

    // space-separated words, all of which have to match, in any order
    static List<Func<string, bool>> Terms(string filter)
    {
        var outp = new List<Func<string, bool>>();
        if (string.IsNullOrWhiteSpace(filter)) return outp;
        foreach (var w in filter.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            outp.Add(TermMatcher(w));
        return outp;
    }

    /// <summary>
    /// One search word as a predicate: the word compiled as a regex when it is one, and a plain
    /// case-insensitive contains when it isn't - which is what a half-typed pattern looks like.
    /// </summary>
    internal static Func<string, bool> TermMatcher(string term)
    {
        try
        {
            var rx = new Regex(term, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return s => rx.IsMatch(s);
        }
        catch (ArgumentException)
        {
            return s => s.Contains(term, StringComparison.OrdinalIgnoreCase);
        }
    }

    static int IndexOf<T>(List<T> list, T item, Func<T, string> key)
    {
        var k = key(item) ?? "";
        for (int i = 0; i < list.Count; i++)
            if ((key(list[i]) ?? "") == k) return i;
        return -1;
    }

    static string Esc(string s) => Controls.EscPct(s ?? "");
}
