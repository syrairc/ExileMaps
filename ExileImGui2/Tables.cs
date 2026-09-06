using System;
using ImGuiNET;

namespace ExileImGui2;

/// <summary>
/// Thin helpers for the compact settings-table pattern: fixed/stretch columns, a header row, and a
/// per-row scope that handles the two things everyone forgets - PushID per row, and the themed
/// selected-row background.
/// </summary>
public static class Tables
{
    /// <summary>
    /// Opens a table and sets up its columns.
    /// </summary>
    /// <param name="cols">One entry per column. A width above 0 pins the column to that many pixels;
    /// 0 or less makes it stretch.</param>
    /// <returns>False when the table is clipped. Skip the body and do NOT call <see cref="End"/> in
    /// that case - same contract as ImGui.BeginTable.</returns>
    public static bool Begin(string id, (string name, float width)[] cols, bool showHeader = true,
        ImGuiTableFlags flags = ImGuiTableFlags.RowBg)
    {
        if (!ImGui.BeginTable(id, cols.Length, flags)) return false;
        foreach (var (name, width) in cols)
            ImGui.TableSetupColumn(name,
                width > 0 ? ImGuiTableColumnFlags.WidthFixed : ImGuiTableColumnFlags.WidthStretch,
                width > 0 ? width : 0f);
        if (showHeader) ImGui.TableHeadersRow();
        return true;
    }

    /// <summary>Closes a table that <see cref="Begin"/> returned true for.</summary>
    public static void End() => ImGui.EndTable();

    /// <summary>
    /// Per-row scope: PushID plus TableNextRow plus the themed selection background. Use with
    /// <c>using</c>, then TableNextColumn per cell.
    /// </summary>
    /// <param name="id">Row index. Skipping this is the classic ImGui table bug - two rows of
    /// identical widgets end up sharing state.</param>
    public static RowScope Row(int id, bool selected = false) => new(id, selected);

    /// <summary>Scope returned by <see cref="Row"/>. Disposing pops the row id.</summary>
    public readonly struct RowScope : IDisposable
    {
        /// <summary>Pushes the row id, starts the row, and tints it when selected.</summary>
        public RowScope(int id, bool selected)
        {
            ImGui.PushID(id);
            ImGui.TableNextRow();
            if (selected) ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(ImGuiCol.Header));
        }

        /// <summary>Pops the row id.</summary>
        public void Dispose() => ImGui.PopID();
    }

    /// <summary>
    /// Dims a row's contents while keeping them interactive, unlike BeginDisabled. Use with
    /// <c>using</c>. This is how you grey out a filtered row in a list whose indices have to stay
    /// stable.
    /// </summary>
    public static DimScope Dim(bool on) => new(on);

    /// <summary>Scope returned by <see cref="Dim"/>. Disposing restores the alpha.</summary>
    public readonly struct DimScope : IDisposable
    {
        readonly bool _on;

        /// <summary>Pushes a reduced alpha when <paramref name="on"/>, otherwise does nothing.</summary>
        public DimScope(bool on)
        {
            _on = on;
            if (on) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.55f);
        }

        /// <summary>Pops the alpha if one was pushed.</summary>
        public void Dispose() { if (_on) ImGui.PopStyleVar(); }
    }
}
