using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;

namespace ExileImGui2;

/// <summary>
/// Dropdowns, including a searchable one. The filter string is always caller-owned so it can sit
/// next to the value it narrows.
/// </summary>
public static class Combo
{
    /// <summary>
    /// The case-insensitive contains rule every picker in this library filters with. Same call as
    /// <see cref="Text.Matches"/>, which is where it lives now.
    /// </summary>
    /// <returns>True when <paramref name="text"/> contains <paramref name="filter"/>. An empty or
    /// whitespace filter matches everything; a null text matches nothing but never throws.</returns>
    public static bool Matches(string filter, string text) => Text.Matches(filter, text);

    // Enum.GetNames/GetValues both allocate a fresh array per call, and this runs every frame. a
    // generic static is per closed type and initialized once, so it costs neither the allocation nor
    // a dictionary lookup to avoid it. enum members can't change at runtime, so caching is safe.
    static class EnumCache<T> where T : struct, System.Enum
    {
        public static readonly string[] Names = System.Enum.GetNames(typeof(T));
        public static readonly T[] Values = (T[])System.Enum.GetValues(typeof(T));
        public static readonly string Id = typeof(T).Name;
    }

    /// <summary>
    /// Dropdown over an enum's names. Same control as <see cref="Controls.EnumCombo{T}"/>, which
    /// forwards here.
    /// </summary>
    /// <param name="id">Defaults to the enum type name, so two combos over the same enum in one
    /// scope need one of their own.</param>
    /// <returns>True the frame the pick changes.</returns>
    public static bool Enum<T>(ref T value, string id = null, float width = 120f) where T : struct, System.Enum
    {
        id ??= EnumCache<T>.Id;
        var names = EnumCache<T>.Names;
        var vals = EnumCache<T>.Values;
        int cur = Array.IndexOf(vals, value);
        if (cur < 0) cur = 0;
        ImGui.SetNextItemWidth(width);
        if (ImGui.Combo("##" + id, ref cur, names, names.Length)) { value = vals[cur]; return true; }
        return false;
    }

    /// <summary>
    /// Dropdown over a label array, writing the picked index back.
    /// </summary>
    /// <param name="id">Defaults to the first label.</param>
    /// <returns>True the frame the pick changes.</returns>
    public static bool Option(ref int index, string[] labels, string id = null, float width = 120f)
    {
        id ??= labels.Length > 0 ? labels[0] : "opt";
        ImGui.SetNextItemWidth(width);
        return ImGui.Combo("##" + id, ref index, labels, labels.Length);
    }

    /// <summary>
    /// Width-pinned dropdown with a filter box, where each candidate's label doubles as its search key.
    /// </summary>
    /// <param name="candidates">(key, label) pairs: the label is drawn, the key comes back on a pick.</param>
    /// <param name="filter">Caller-owned search text. Cleared on a pick and when the popup closes.</param>
    /// <param name="picked">The key that was picked, or null.</param>
    /// <returns>True the frame something is picked.</returns>
    public static bool SearchCombo(string id, string preview,
        IEnumerable<(string key, string label)> candidates,
        ref string filter, out string picked, float width = 250f) =>
        SearchCombo(id, preview, candidates.Select(c => (c.key, c.label, c.label)), ref filter, out picked, width);

    /// <summary>
    /// Same picker with a separate search key, for when the text to match on isn't the text to show.
    /// </summary>
    /// <param name="candidates">(key, label, search) triples.</param>
    /// <param name="width">Pins both the combo and its popup. Negative fills the available width.</param>
    /// <returns>True the frame something is picked.</returns>
    public static bool SearchCombo(string id, string preview,
        IEnumerable<(string key, string label, string search)> candidates,
        ref string filter, out string picked, float width = 250f)
    {
        picked = null;
        bool changed = false;
        if (width < 0) width = ImGui.GetContentRegionAvail().X;
        ImGui.SetNextItemWidth(width);
        ImGui.SetNextWindowSizeConstraints(new Vector2(width, 0), new Vector2(width, 400));
        if (ImGui.BeginCombo("##" + id, preview))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##" + id + "_f", ref filter, 128);
            foreach (var c in candidates)
            {
                if (!Matches(filter, c.search)) continue;
                if (ImGui.Selectable(c.label + "##" + c.key)) { picked = c.key; changed = true; }
            }
            if (changed) ImGui.CloseCurrentPopup();
            ImGui.EndCombo();
        }
        else if (filter.Length > 0)
        {
            filter = ""; // popup closed, drop any stale filter text
        }
        if (changed) filter = "";
        return changed;
    }
}
