using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;

namespace ExileImGui2;

/// <summary>
/// One column of a <see cref="SortableTable"/> table: a header, a width, an optional sort key, and
/// whatever the cell itself draws.
/// </summary>
public sealed class TableColumn<T>
{
    /// <summary>Header text, and the label ImGui shows the sort arrow on when this column is active.</summary>
    public string Header = "";

    /// <summary>0 stretches to fill; a positive width pins the column instead.</summary>
    public float Width;

    /// <summary>
    /// Null disables sorting for this column (its header still shows, just without a sort arrow).
    /// Return whatever <see cref="IComparable"/> the column should order by - a string, a double,
    /// whatever the row naturally sorts on.
    /// </summary>
    public Func<T, IComparable> SortKey;

    /// <summary>
    /// Draws the cell body. Called with the row's item and its index in the SOURCE list, not its
    /// position in the current view - so it survives a sort or a filter, and you can index straight
    /// back into your own list with it.
    /// </summary>
    public Action<T, int> Draw;
}

/// <summary>
/// A plain data table over the caller's own list: optional search box, click-to-sort headers, any
/// number of columns, each drawing whatever it likes (text, a checkbox, a button). Unlike
/// <c>Weight.List</c> this has no built-in slider or selection - the caller owns both, so it fits
/// any read-only or row-actionable grid instead of just a weight-tuning list.
/// <para>Standalone: this file needs nothing else from the toolkit.</para>
/// </summary>
public static class SortableTable
{
    /// <summary>
    /// Draws the table. Pass <paramref name="filterText"/> to add a search box above it (plain
    /// case-insensitive substring, same rule as the rest of the toolkit); null draws no box and
    /// shows every row.
    /// </summary>
    /// <param name="id">Scopes the ImGui ids. Give each table on screen its own.</param>
    /// <param name="items">Source rows. Never reordered or mutated - only a filtered, sorted index
    /// list is shuffled.</param>
    /// <param name="columns">Left-to-right column definitions.</param>
    /// <param name="filter">Search box text, retained by the caller like any other ImGui field.</param>
    /// <param name="maxHeight">Scrolls past this height. 0 grows to fit and lets the parent window
    /// scroll instead. A scrolling table also clips: only the rows actually on screen are drawn, so a
    /// few thousand rows cost the same as a few dozen. That needs every row the same height - a cell
    /// that wraps or stacks will misplace the ones after it, so keep cell content to one line.</param>
    public static void Draw<T>(string id, IList<T> items, TableColumn<T>[] columns,
        ref string filter, Func<T, string> filterText = null, float maxHeight = 0f)
    {
        if (items == null || columns == null || columns.Length == 0) return;

        ImGui.PushID(id);

        if (filterText != null)
        {
            var f = filter ?? "";
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputTextWithHint("##filter", "Search...", ref f, 128);
            filter = f;
        }

        // the search box above ate into what is left, so a caller that passed "the rest of the window"
        // now overflows it by exactly one box and the parent grows a scrollbar. clamp here rather than
        // making every caller subtract a box height it did not ask for.
        if (maxHeight > 0f)
        {
            float avail = ImGui.GetContentRegionAvail().Y;
            if (avail > 0f && avail < maxHeight) maxHeight = avail;
        }

        var visible = VisibleIndices(items, filterText, filter ?? "");

        // NoSavedSettings or imgui persists column widths per table id in its ini and REPLAYS them over
        // the widths the caller declared - so editing a Width in code silently does nothing, and adding or
        // removing a column reapplies the old widths at the new indices. dragging a border still works, it
        // just does not outlive the session. widths here are authored in code, so code wins.
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersInnerV
                    | ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortTristate
                    | ImGuiTableFlags.NoSavedSettings;
        if (maxHeight > 0f) flags |= ImGuiTableFlags.ScrollY;

        if (!ImGui.BeginTable("t", columns.Length, flags, new Vector2(0f, maxHeight)))
        {
            ImGui.PopID();
            return;
        }

        foreach (var col in columns)
            ImGui.TableSetupColumn(col.Header,
                (col.Width > 0f ? ImGuiTableColumnFlags.WidthFixed : ImGuiTableColumnFlags.WidthStretch) |
                (col.SortKey == null ? ImGuiTableColumnFlags.NoSort : ImGuiTableColumnFlags.None),
                col.Width);
        if (maxHeight > 0f) ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        var specs = ImGui.TableGetSortSpecs();
        if (specs.SpecsCount > 0)
        {
            var s = specs.Specs;
            SortIndices(visible, items, columns[s.ColumnIndex].SortKey,
                s.SortDirection == ImGuiSortDirection.Ascending);
        }

        // only a scrolling table can clip - without ScrollY there is no viewport to clip against, and
        // the clipper would hide everything past the first screenful
        if (maxHeight > 0f)
        {
            ImGuiListClipperPtr clipper;
            unsafe { clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper()); }
            clipper.Begin(visible.Count);
            while (clipper.Step())
                for (int k = clipper.DisplayStart; k < clipper.DisplayEnd; k++)
                    DrawRow(items, columns, visible[k]);
            clipper.End();
            clipper.Destroy();
        }
        else
        {
            foreach (int i in visible)
                DrawRow(items, columns, i);
        }

        ImGui.EndTable();
        ImGui.PopID();
    }

    private static void DrawRow<T>(IList<T> items, TableColumn<T>[] columns, int i)
    {
        // per-row PushID or two rows of identical widgets share state. source index, not view
        // position, so a row keeps its id across a sort.
        ImGui.PushID(i);
        ImGui.TableNextRow();
        for (int c = 0; c < columns.Length; c++)
        {
            ImGui.TableNextColumn();
            // and per-column too, since Draw is a free callback - two columns sharing one cell
            // helper would otherwise draw the same widget id twice in a row and share its state
            ImGui.PushID(c);
            columns[c].Draw?.Invoke(items[i], i);
            ImGui.PopID();
        }
        ImGui.PopID();
    }

    /// <summary>
    /// Reorders <paramref name="idx"/> by <paramref name="key"/>. A null key leaves the order alone,
    /// which is what a NoSort column gets. Rows whose key comes back null sort to the front.
    /// </summary>
    internal static void SortIndices<T>(List<int> idx, IList<T> items, Func<T, IComparable> key, bool asc)
    {
        if (idx == null || items == null || key == null) return;
        // tie goes to source order, always ascending even when the outer sort is flipped -
        // otherwise Sort's instability scatters the ties instead of keeping them put
        idx.Sort((a, b) =>
        {
            var ka = key(items[a]);
            var kb = key(items[b]);
            int c = ka == null ? (kb == null ? 0 : -1) : kb == null ? 1 : ka.CompareTo(kb);
            return c != 0 ? (asc ? c : -c) : a.CompareTo(b);
        });
    }

    /// <summary>
    /// Indices of rows whose <paramref name="label"/> contains <paramref name="filter"/>, in source
    /// order. A null label never throws, it just doesn't match. Null label func means no filtering,
    /// so every row is visible.
    /// </summary>
    internal static List<int> VisibleIndices<T>(IList<T> items, Func<T, string> label, string filter)
    {
        var outv = new List<int>();
        if (items == null) return outv;
        bool all = label == null || string.IsNullOrWhiteSpace(filter);
        for (int i = 0; i < items.Count; i++)
            if (all || (label(items[i]) ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase))
                outv.Add(i);
        return outv;
    }
}
