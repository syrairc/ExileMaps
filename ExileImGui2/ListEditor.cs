using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;

namespace ExileImGui2;

/// <summary>
/// Describes how a <see cref="ListEditor"/> adds a new item: either a "+ Add" button backed by a
/// factory, or a searchable combo over a caller-supplied candidate pool. Build one with the static
/// helpers.
/// </summary>
public sealed class AddSource<T>
{
    internal Func<T> Factory;
    internal IEnumerable<(string key, string label)> Candidates;
    internal Func<string, T> Make;
    internal string Label;
    internal bool IsCombo;

    /// <summary>A plain button that appends whatever <paramref name="factory"/> returns.</summary>
    public static AddSource<T> Button(Func<T> factory, string label = "+ Add") =>
        new() { Factory = factory, Label = label, IsCombo = false };

    /// <summary>
    /// A searchable dropdown of candidates.
    /// </summary>
    /// <param name="candidates">(key, label) pairs. The label is drawn, the key is what gets picked.</param>
    /// <param name="make">Turns a picked key into the item to append.</param>
    public static AddSource<T> Combo(IEnumerable<(string key, string label)> candidates,
        Func<string, T> make, string label = "Add...") =>
        new() { Candidates = candidates, Make = make, Label = label, IsCombo = true };
}

/// <summary>
/// Knobs for <see cref="ListEditor.ReorderableList"/>. All optional - a bare <c>new ListOpts()</c>
/// gives numbered, draggable rows.
/// </summary>
public sealed class ListOpts
{
    /// <summary>Leading rows that can't move or be displaced - a max-priority row, say.</summary>
    public int PinnedHead;

    /// <summary>Trailing rows that can't move or be displaced - a catch-all row, say.</summary>
    public int PinnedTail;

    /// <summary>Off gives plain selectable rows and the right-click menu, with no drag and no ctrl+arrows.</summary>
    public bool Reorderable = true;

    /// <summary>Shows the 01..NN order column. The order IS the match order, so it is worth showing.</summary>
    public bool ShowIndex = true;

    /// <summary>Row height in pixels. 0 uses one frame height.</summary>
    public float RowHeight;

    /// <summary>
    /// Gates the keyboard move: while this is set, up/down reorder the selected row. Feed it
    /// <c>Input.GetKeyState(Keys.ControlKey)</c>, or whatever key your plugin binds - it is a
    /// parameter rather than a read of <c>io.KeyCtrl</c> so the chord is yours to choose, and so it
    /// comes from the same place as the rest of your hotkeys.
    /// </summary>
    public bool CtrlHeld;

    /// <summary>Right-click popup body for row i - rename, duplicate, delete and so on.</summary>
    public Action<int> Menu;

    /// <summary>
    /// Per-row searchable text. Null draws no filter box. Non-matches are dimmed, never hidden -
    /// hiding would shift every index after it while the drag math still addresses rows by position.
    /// </summary>
    public Func<int, string> FilterText;
}

/// <summary>
/// Add, delete and reorder over a caller-owned <c>List&lt;T&gt;</c>. Every entry point mutates the
/// list in place and returns a changed flag to fold into your own dirty flag.
/// <para>
/// One drag is active app-wide, so the drag state is static.
/// </para>
/// </summary>
public static class ListEditor
{
    static readonly Dictionary<string, string> _filters = new();   // combo-add filter text, per id
    static readonly Dictionary<string, int> _pendingDelete = new(); // confirm-delete target, per id
    static readonly Dictionary<string, string> _rowFilters = new(); // row-search filter text, per id - own dict, not _filters, those are two different search boxes on the same list
    // one drag active app-wide. _dragId names the list that owns it so other lists on screen don't steal it.
    static string _dragId;
    static int _dragFrom = -1, _dragTo = -1;
    // ReorderableList runs its own drag instead of imgui's active-item id: row ids there are index-based,
    // so the moment a row moves the held id belongs to whatever slid into its slot and the drag falls apart.
    static string _rowDragId;
    static int _rowDragFrom = -1;

    /// <summary>
    /// Compact rows: an optional drag grip, your row content, and an inline delete X. The simple tier.
    /// </summary>
    /// <param name="drawRow">Draws one item's controls, given the item and its index.</param>
    /// <param name="filterText">When given, adds a search box above the rows and dims non-matches.</param>
    /// <returns>True when anything was added, edited, removed or reordered.</returns>
    public static bool Compact<T>(string id, List<T> list, Func<T, int, bool> drawRow,
        AddSource<T> add = null, bool reorderable = false, bool confirmDelete = false,
        Func<T, string> filterText = null)
    {
        bool d = false;
        ImGui.PushID(id);
        d |= DrawAdd(id, list, add);

        string filter = DrawRowFilter(id, filterText != null);

        int toRemove = -1;
        for (int i = 0; i < list.Count; i++)
        {
            ImGui.PushID(i);
            using (Tables.Dim(filterText != null && !Text.Matches(filter, filterText(list[i]))))
            {
                if (reorderable) { DragGrip(id, i); ImGui.SameLine(); }
                d |= drawRow(list[i], i);
                ImGui.SameLine();
                if (ImGui.SmallButton("x")) toRemove = i;
            }
            ImGui.PopID();
        }

        d |= ApplyDelete(id, list, toRemove, confirmDelete);
        if (reorderable) d |= ApplyReorder(id, list);
        ImGui.PopID();
        return d;
    }

    /// <summary>
    /// One collapsible card per item, for objects with more than a row's worth of fields. The header
    /// X deletes. The advanced tier.
    /// </summary>
    /// <param name="label">Header text for an item.</param>
    /// <param name="drawBody">Draws the expanded body, given the item and its index.</param>
    /// <param name="filterText">When given, adds a search box above the cards and dims non-matches.</param>
    /// <returns>True when anything was added, edited, removed or reordered.</returns>
    public static bool Cards<T>(string id, List<T> list, Func<T, string> label, Func<T, int, bool> drawBody,
        AddSource<T> add = null, bool reorderable = false, bool confirmDelete = false,
        Func<T, string> filterText = null)
    {
        bool d = false;
        ImGui.PushID(id);
        d |= DrawAdd(id, list, add);

        string filter = DrawRowFilter(id, filterText != null);

        int toRemove = -1;
        for (int i = 0; i < list.Count; i++)
        {
            ImGui.PushID(i);
            using (Tables.Dim(filterText != null && !Text.Matches(filter, filterText(list[i]))))
            {
                bool keep = true;
                // ###h keys the header id on "h" only, not the label. otherwise editing the label changes the
                // id each frame -> header resets to closed -> the body (and any focused input) vanishes mid-type.
                bool open = ImGui.CollapsingHeader(label(list[i]) + "###h", ref keep);
                if (reorderable) DragSourceTarget(id, i, label(list[i]));
                if (open)
                {
                    ImGui.Indent();
                    d |= drawBody(list[i], i);
                    ImGui.Unindent();
                }
                if (!keep) toRemove = i;   // header X
            }
            ImGui.PopID();
        }

        d |= ApplyDelete(id, list, toRemove, confirmDelete);
        if (reorderable) d |= ApplyReorder(id, list);
        ImGui.PopID();
        return d;
    }

    /// <summary>
    /// Selectable rows that reorder by dragging the row itself, inserting live under the cursor rather
    /// than swapping. There are no per-row up/down/delete buttons - those move into the right-click
    /// menu (<see cref="ListOpts.Menu"/>) and ctrl+up/down.
    /// </summary>
    /// <param name="selected">Selected row index, written back on a click or a move.</param>
    /// <param name="drawRow">Draws whatever goes after the index cell, usually just the name.</param>
    /// <returns>True when the ORDER changed. A selection or filter change alone returns false.</returns>
    public static bool ReorderableList<T>(string id, IList<T> items, ref int selected,
        Action<T, int> drawRow, ListOpts opts = null)
    {
        opts ??= new ListOpts();
        bool changed = false;
        float rowH = opts.RowHeight > 0 ? opts.RowHeight : ImGui.GetFrameHeight();

        ImGui.PushID(id);
        string filter = DrawRowFilter(id, opts.FilterText != null);

        // the movable band is [head, movable). anything outside it is pinned. Reorderable=false pins
        // everything, which is the same thing an ordering-free list wants.
        int head = opts.Reorderable ? Math.Min(Math.Max(0, opts.PinnedHead), items.Count) : items.Count;
        int movable = Math.Max(head, items.Count - opts.PinnedTail);

        var mouse = ImGui.GetMousePos();
        var dl = ImGui.GetWindowDrawList();
        int drop = -1;                       // movable row the mouse is currently over
        float firstTop = 0f, lastBottom = 0f;// movable band, so dragging past either end still lands

        for (int i = 0; i < items.Count; i++)
        {
            ImGui.PushID(i);
            // dims non-matches instead of hiding them - hiding would shift every index after it while
            // the drag math above still addresses rows by position (same reasoning as Compact/Cards).
            using var _dim = Tables.Dim(opts.FilterText != null && !Text.Matches(filter, opts.FilterText(i)));
            float rowTop = ImGui.GetCursorPosY();

            // one full-width invisible selectable IS the row: it selects, it drags, it owns the popup.
            // AllowOverlap lets the cells we draw on top of it keep their own clicks.
            if (ImGui.Selectable("##row", selected == i, ImGuiSelectableFlags.AllowOverlap, new Vector2(0, rowH)))
                selected = i;

            // press starts a drag; the drop target is whichever row rect the mouse is inside right now, so
            // a fast drag across several rows lands where you actually let go.
            bool movableRow = i >= head && i < movable;
            if (ImGui.IsItemActivated() && movableRow) { _rowDragId = id; _rowDragFrom = i; selected = i; }
            var rmin = ImGui.GetItemRectMin();
            var rmax = ImGui.GetItemRectMax();
            if (_rowDragId == id && movableRow)
            {
                if (i == head) firstTop = rmin.Y;
                lastBottom = rmax.Y;
                if (mouse.Y >= rmin.Y && mouse.Y < rmax.Y) drop = i;
                // outline what you're carrying. no ghost row, the list reorders live under the cursor.
                if (i == _rowDragFrom)
                    dl.AddRect(rmin, rmax, ImGui.GetColorU32(ImGuiCol.DragDropTarget));
            }

            // right-click menu. selecting first means the menu always acts on the row you aimed at.
            if (opts.Menu != null)
            {
                if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) selected = i;
                if (ImGui.BeginPopupContextItem("##ctx")) { opts.Menu(i); ImGui.EndPopup(); }
            }

            // cells, drawn back over the selectable. SameLine(x) with a positive x returns to the row at
            // that offset; the y nudge centres the text in a row taller than the text.
            ImGui.SameLine(6f);
            ImGui.SetCursorPosY(rowTop + (rowH - ImGui.GetTextLineHeight()) * 0.5f);
            bool pinned = !movableRow;
            // no drag grip: the whole row is the hit area, and a per-row affordance only repeats what the
            // one hint under the list already says.
            if (opts.ShowIndex) { ImGui.TextDisabled(pinned ? "--" : (i + 1).ToString("00")); ImGui.SameLine(); }
            drawRow(items[i], i);
            ImGui.PopID();
        }

        // apply the drag once per frame, after every row rect is known. release ends it.
        if (_rowDragId == id)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if (drop < 0 && movable > head)
                    drop = mouse.Y < firstTop ? head : mouse.Y > lastBottom ? movable - 1 : -1;
                if (drop >= 0 && drop != _rowDragFrom && _rowDragFrom >= head && _rowDragFrom < movable)
                {
                    Move(items, _rowDragFrom, drop);   // insert, not swap - a 3-row jump keeps the others in order
                    _rowDragFrom = drop;
                    selected = drop;
                    changed = true;
                }
            }
            else { _rowDragId = null; _rowDragFrom = -1; }
        }

        // keyboard move. imgui gives us the arrows; the held-key half comes from the caller so the
        // chord is theirs to pick (same deal as Overlay.MoveHeld).
        if (opts.CtrlHeld && selected >= 0)
        {
            int dir = ImGui.IsKeyPressed(ImGuiKey.UpArrow) ? -1 : ImGui.IsKeyPressed(ImGuiKey.DownArrow) ? 1 : 0;
            int target = SwapTarget(selected, dir, items.Count, opts.PinnedTail, opts.PinnedHead);
            if (target >= 0)
            {
                (items[selected], items[target]) = (items[target], items[selected]);
                selected = target;
                changed = true;
            }
        }

        ImGui.PopID();
        return changed;
    }

    /// <summary>
    /// Neighbour index for a one-step move. Pure, so the ordering rules can be asserted without a frame.
    /// </summary>
    /// <param name="dir">-1 for up, 1 for down. 0 is never a move.</param>
    /// <returns>-1 when the move is illegal: off either end, or into or out of a pinned head or tail.</returns>
    public static int SwapTarget(int from, int dir, int count, int pinnedTail, int pinnedHead = 0)
    {
        if (dir == 0 || from < 0 || from >= count) return -1;
        int head = Math.Min(Math.Max(0, pinnedHead), count);
        int movable = Math.Max(head, count - pinnedTail);
        if (from < head || from >= movable) return -1;       // pinned rows never move
        int to = from + dir;
        if (to < head || to >= movable) return -1;           // and never get displaced
        return to;
    }

    /// <summary>
    /// Pure reorder: pulls <paramref name="from"/> out and re-inserts it at <paramref name="to"/>,
    /// keeping everything between in order. A no-op when either index is out of range or they match.
    /// </summary>
    public static void Move<T>(IList<T> list, int from, int to)
    {
        if (from < 0 || to < 0 || from >= list.Count || to >= list.Count || from == to) return;
        var moved = list[from];
        list.RemoveAt(from);
        list.Insert(to, moved);
    }

    // one filter box, id-scoped, shared by Compact/Cards/ReorderableList. enabled false = no-op, no box drawn.
    static string DrawRowFilter(string id, bool enabled)
    {
        if (!enabled) return "";
        var filter = _rowFilters.TryGetValue(id, out var f) ? f : "";
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##rowfilter", "filter...", ref filter, 128);
        _rowFilters[id] = filter;
        return filter;
    }

    // draws whichever add affordance the AddSource describes, and appends on use. null draws nothing.
    internal static bool DrawAdd<T>(string id, List<T> list, AddSource<T> add)
    {
        if (add == null) return false;
        if (!add.IsCombo)
        {
            if (ImGui.Button(add.Label)) { list.Add(add.Factory()); return true; }
            return false;
        }
        var filter = _filters.TryGetValue(id, out var f) ? f : "";
        bool changed = Combo.SearchCombo(id + "_add", add.Label, add.Candidates, ref filter, out var picked);
        _filters[id] = filter;
        if (changed) { list.Add(add.Make(picked)); return true; }
        return false;
    }

    // a "grip" button that acts as both drag source and drop target for the row at `index`.
    static void DragGrip(string id, int index)
    {
        ImGui.Button(":::");
        DragSourceTarget(id, index, null);
    }

    // attach drag source + drop target to the last-submitted item (header or grip). the source stamps _dragId
    // so only the owning list applies the reorder.
    static void DragSourceTarget(string id, int index, string label)
    {
        if (ImGui.BeginDragDropSource())
        {
            _dragId = id;
            _dragFrom = index;
            ImGui.SetDragDropPayload("EILIST", IntPtr.Zero, 0);
            if (label != null) ImGui.TextUnformatted(label);
            ImGui.EndDragDropSource();
        }
        if (ImGui.BeginDragDropTarget())
        {
            if (_dragId == id) _dragTo = index;   // ignore hovers from a drag that started in another list
            ImGui.EndDragDropTarget();
        }
    }

    // requested = index the user clicked X on this frame (or -1). immediate remove, or route through
    // Windows.ConfirmModal when confirmDelete is set. index re-guarded since it can shift.
    internal static bool ApplyDelete<T>(string id, List<T> list, int requested, bool confirm)
    {
        if (!confirm)
        {
            if (requested >= 0 && requested < list.Count) { list.RemoveAt(requested); return true; }
            return false;
        }

        if (requested >= 0) { _pendingDelete[id] = requested; ImGui.OpenPopup("eidel##" + id); }

        bool d = false;
        if (Windows.ConfirmModal("eidel##" + id, "Delete this item?", out var ok))
        {
            if (ok && _pendingDelete.TryGetValue(id, out var pi) && pi >= 0 && pi < list.Count)
            {
                list.RemoveAt(pi);
                d = true;
            }
            _pendingDelete.Remove(id);
        }
        return d;
    }

    // apply a pending drag once the mouse releases, but only for the list that owns the drag. guards indices
    // since a delete can shift them.
    static bool ApplyReorder<T>(string id, List<T> list)
    {
        if (_dragId != id || !ImGui.IsMouseReleased(ImGuiMouseButton.Left)) return false;
        bool d = false;
        if (_dragFrom >= 0 && _dragTo >= 0 && _dragFrom != _dragTo
            && _dragFrom < list.Count && _dragTo < list.Count)
        {
            Move(list, _dragFrom, _dragTo);
            d = true;
        }
        _dragFrom = _dragTo = -1;
        _dragId = null;
        return d;
    }
}
