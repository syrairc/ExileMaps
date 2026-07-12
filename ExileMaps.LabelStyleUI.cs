// ImGui for the label style section (base style + Favorite/Special/Content/Biome overrides).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using ExileMaps.Classes;

namespace ExileMaps;

public partial class ExileMapsCore
{
    // Per-manager search filter for the searchable add-combo.
    private readonly Dictionary<string, string> _labelMgrFilter = new();
    // Friendly combo labels for IconPosition, indexed by enum value (AboveIcon..RightOfLabel).
    private static readonly string[] IconPositionLabels = { "Above Icon", "Below Label", "Left of Label", "Right of Label" };
    // Last-serialized label config, to detect edits. Labels are plain POCO fields (not observable),
    // so they don't raise PropertyChanged like weights do - without this the profile never gets
    // marked dirty and LoadProfile clobbers the edits on the next launch.
    private string _labelsSnapshot;

    public void DrawLabelStyleSection()
    {
        if (Section("Base Style"))
            DrawBaseStyleControls(Settings.Labels.Base);

        if (ImGui.CollapsingHeader("How Overrides Work")) {
            ImGui.TextWrapped(
                "Base Style sets the look for every map name. Each override section below lets a " +
                "category (favorites, special maps, a content type, a biome) replace parts of that base look.");
            ImGui.Spacing();
            ImGui.TextWrapped(
                "Every override row starts with a checkbox. That checkbox is the OVERRIDE toggle: off means " +
                "\"leave this property at the base value\", on means \"use the value I set on this row instead\".");
            ImGui.Spacing();
            ImGui.TextWrapped(
                "So the by-weight rows show TWO checkboxes. The first (leftmost) decides whether this override " +
                "controls the setting at all. The second is the actual value being overridden - the on/off state " +
                "of \"color by weight\". Leftmost off = ignore this row entirely; leftmost on + second off = force " +
                "the setting off for this category.");
            ImGui.Spacing();
            ImGui.TextWrapped(
                "When several overrides apply to one map, higher priority wins per property. " +
                "Highest to lowest: Special, Content, Favorite, Map, Biome, Base.");
        }

        // Override sections collapsed by default - most users only touch a couple.
        // Ordered high-to-low priority (Special wins over Content wins over Favorite wins over Biome).
        if (ImGui.CollapsingHeader("Special Maps Override"))
            DrawOverrideControls("special", Settings.Labels.Special);

        if (ImGui.CollapsingHeader("Content Overrides"))
            DrawOverrideManager("content", Settings.Labels.Content,
                Settings.Maps.Content.ContentTypes.Select(
                    kv => (kv.Key, string.IsNullOrEmpty(kv.Value?.Name) ? kv.Key : kv.Value.Name)));

        if (ImGui.CollapsingHeader("Favorite Maps Override"))
            DrawOverrideControls("fav", Settings.Labels.Favorite);

        if (ImGui.CollapsingHeader("Map Overrides"))
            DrawOverrideManager("map", Settings.Labels.Map,
                Settings.Maps.Maps.Values
                    .Where(m => !string.IsNullOrEmpty(m.ShortestId))
                    .Select(m => (m.ShortestId, m.Name)));

        if (ImGui.CollapsingHeader("Biome Overrides"))
            DrawOverrideManager("biome", Settings.Labels.Biome,
                Settings.Maps.Biomes.Biomes.Keys.Select(k => (k, k)));

        // Any change to the label config marks the profile dirty so the periodic snapshot captures it
        // (same persistence path weights use via profileDirty). Catches field edits and override add/remove.
        try {
            var now = System.Text.Json.JsonSerializer.Serialize(Settings.Labels);
            if (_labelsSnapshot != null && now != _labelsSnapshot)
                profileDirty = true;
            _labelsSnapshot = now;
        } catch (Exception e) {
            LogError("Label snapshot compare failed: " + e.Message);
        }
    }

    // ---- shared small widgets ----

    private static bool ColorRGB(string id, ref Color c)
    {
        Vector3 v = new(c.R / 255f, c.G / 255f, c.B / 255f);
        if (ImGui.ColorEdit3(id, ref v, ImGuiColorEditFlags.NoInputs)) {
            c = Color.FromArgb(255, (int)(v.X * 255), (int)(v.Y * 255), (int)(v.Z * 255));
            return true;
        }
        return false;
    }

    // RGBA picker - the alpha bar is the box/border opacity (no separate slider).
    private static bool ColorRGBA(string id, ref Color c)
    {
        Vector4 v = new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);
        if (ImGui.ColorEdit4(id, ref v, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar)) {
            c = Color.FromArgb((int)(v.W * 255), (int)(v.X * 255), (int)(v.Y * 255), (int)(v.Z * 255));
            return true;
        }
        return false;
    }

    // ---- base style: plain controls, no toggles ----

    private void DrawBaseStyleControls(LabelStyle s)
    {
        float scale = s.TextScale;
        if (ImGui.SliderFloat("Text Scale##base", ref scale, 0.5f, 3.0f)) s.TextScale = scale;

        Color tc = s.TextColor;
        if (ColorRGB("Text Color##base", ref tc)) s.TextColor = tc;
        bool tw = s.TextColorByWeight;
        if (ImGui.Checkbox("Text color by weight##base", ref tw)) s.TextColorByWeight = tw;

        bool st = s.StrokeEnabled;
        if (ImGui.Checkbox("Text stroke##base", ref st)) s.StrokeEnabled = st;
        ImGui.SameLine();
        Color sc = s.StrokeColor;
        if (ColorRGB("Stroke Color##base", ref sc)) s.StrokeColor = sc;

        bool bv = s.BoxVisible;
        if (ImGui.Checkbox("Enable Background##base", ref bv)) s.BoxVisible = bv;
        if (s.BoxVisible) {
            Color bc = s.BoxColor;
            if (ColorRGBA("Background Color##base", ref bc)) s.BoxColor = bc;
            bool bw = s.BoxColorByWeight;
            if (ImGui.Checkbox("Background color by weight##base", ref bw)) s.BoxColorByWeight = bw;
        }

        // Border is its own frame - independent of the background.
        bool rv = s.BorderVisible;
        if (ImGui.Checkbox("Enable Border##base", ref rv)) s.BorderVisible = rv;
        if (s.BorderVisible) {
            Color rc = s.BorderColor;
            if (ColorRGBA("Border Color##base", ref rc)) s.BorderColor = rc;
            bool rw = s.BorderColorByWeight;
            if (ImGui.Checkbox("Border color by weight##base", ref rw)) s.BorderColorByWeight = rw;
            int rt = s.BorderThickness;
            if (ImGui.SliderInt("Border Thickness##base", ref rt, 1, 8)) s.BorderThickness = rt;
        }
    }

    // ---- override block: each row = an Override checkbox + its control ----

    private void DrawOverrideControls(string id, LabelStyleOverride o)
    {
        // one helper per row keeps the enable-checkbox + control paired and disabled when off.
        void Row(string label, ref bool ovr, Action control)
        {
            bool e = ovr;
            if (ImGui.Checkbox($"##{id}_{label}_ovr", ref e)) ovr = e;
            ImGui.SameLine();
            if (!e) ImGui.BeginDisabled();
            control();
            if (!e) ImGui.EndDisabled();
        }

        { bool ovr = o.OverrideTextScale; Row("scale", ref ovr, () => {
            float v = o.TextScale; if (ImGui.SliderFloat($"Text Scale##{id}", ref v, 0.5f, 3.0f)) o.TextScale = v; });
            o.OverrideTextScale = ovr; }

        { bool ovr = o.OverrideTextColor; Row("tcol", ref ovr, () => {
            Color v = o.TextColor; if (ColorRGB($"Text Color##{id}", ref v)) o.TextColor = v; });
            o.OverrideTextColor = ovr; }

        { bool ovr = o.OverrideTextColorByWeight; Row("tw", ref ovr, () => {
            bool v = o.TextColorByWeight; if (ImGui.Checkbox($"Text color by weight##{id}", ref v)) o.TextColorByWeight = v; });
            o.OverrideTextColorByWeight = ovr; }

        { bool ovr = o.OverrideStroke; Row("stroke", ref ovr, () => {
            bool en = o.StrokeEnabled; if (ImGui.Checkbox($"Stroke##{id}", ref en)) o.StrokeEnabled = en;
            ImGui.SameLine(); Color v = o.StrokeColor; if (ColorRGB($"Stroke Color##{id}", ref v)) o.StrokeColor = v; });
            o.OverrideStroke = ovr; }

        // "(?)" marker with hover tooltip - reused for the nested-visibility hint below.
        void Hint(string text)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(text);
        }

        { bool ovr = o.OverrideBoxVisible; Row("bvis", ref ovr, () => {
            bool v = o.BoxVisible; if (ImGui.Checkbox($"Enable Background##{id}", ref v)) o.BoxVisible = v; });
            o.OverrideBoxVisible = ovr; }
        Hint("Background color options only show while the background is enabled here.");

        // Background color rows only when the box is shown for this override (mirrors Base Style).
        if (o.BoxVisible) {
            { bool ovr = o.OverrideBoxColor; Row("bcol", ref ovr, () => {
                Color v = o.BoxColor; if (ColorRGBA($"Background Color##{id}", ref v)) o.BoxColor = v; });
                o.OverrideBoxColor = ovr; }

            { bool ovr = o.OverrideBoxColorByWeight; Row("bw", ref ovr, () => {
                bool v = o.BoxColorByWeight; if (ImGui.Checkbox($"Background color by weight##{id}", ref v)) o.BoxColorByWeight = v; });
                o.OverrideBoxColorByWeight = ovr; }
        }

        // Border is its own frame - independent of the background.
        { bool ovr = o.OverrideBorderVisible; Row("rvis", ref ovr, () => {
            bool v = o.BorderVisible; if (ImGui.Checkbox($"Enable Border##{id}", ref v)) o.BorderVisible = v; });
            o.OverrideBorderVisible = ovr; }
        Hint("Border color and thickness options only show while the border is enabled here.");

        if (o.BorderVisible) {
            { bool ovr = o.OverrideBorderColor; Row("rcol", ref ovr, () => {
                Color v = o.BorderColor; if (ColorRGBA($"Border Color##{id}", ref v)) o.BorderColor = v; });
                o.OverrideBorderColor = ovr; }

            { bool ovr = o.OverrideBorderColorByWeight; Row("rw", ref ovr, () => {
                bool v = o.BorderColorByWeight; if (ImGui.Checkbox($"Border color by weight##{id}", ref v)) o.BorderColorByWeight = v; });
                o.OverrideBorderColorByWeight = ovr; }

            { bool ovr = o.OverrideBorderThickness; Row("rt", ref ovr, () => {
                int v = o.BorderThickness; if (ImGui.SliderInt($"Border Thickness##{id}", ref v, 1, 8)) o.BorderThickness = v; });
                o.OverrideBorderThickness = ovr; }
        }

        // ---- icon (override-only). IconEnabled is the gate; no Override* toggle. ----
        ImGui.Separator();
        bool iconOn = o.IconEnabled;
        if (ImGui.Checkbox($"Icon##{id}_iconon", ref iconOn)) o.IconEnabled = iconOn;
        if (iconOn) {
            int iconIdx = (int)o.Icon;
            var iconNames = Enum.GetNames(typeof(SpriteIcon));
            ImGui.SetNextItemWidth(160);
            if (ImGui.Combo($"Glyph##{id}_icon", ref iconIdx, iconNames, iconNames.Length))
                o.Icon = (SpriteIcon)iconIdx;

            bool repl = o.IconReplacesNode;
            if (ImGui.Checkbox($"Replaces node icon##{id}_iconrepl", ref repl)) o.IconReplacesNode = repl;

            Color it = o.IconTint;
            if (ColorRGB($"Icon Tint##{id}_icontint", ref it)) o.IconTint = it;
            float isz = o.IconSize;
            if (ImGui.SliderFloat($"Icon Size##{id}_iconsize", ref isz, 8f, 64f)) o.IconSize = isz;

            // Position + offset only matter when NOT replacing the node icon (replace = exact center).
            if (!o.IconReplacesNode) {
                int posIdx = (int)o.IconPosition;
                ImGui.SetNextItemWidth(160);
                if (ImGui.Combo($"Icon Position##{id}_iconpos", ref posIdx, IconPositionLabels, IconPositionLabels.Length))
                    o.IconPosition = (IconPosition)posIdx;
                float ox = o.IconOffsetX;
                if (ImGui.SliderFloat($"Icon Offset X##{id}_iconox", ref ox, -100f, 100f)) o.IconOffsetX = ox;
                float oy = o.IconOffsetY;
                if (ImGui.SliderFloat($"Icon Offset Y##{id}_iconoy", ref oy, -100f, 100f)) o.IconOffsetY = oy;
            }
        }
    }

    // ---- dynamic per-type manager (Option A: add-combo + collapsible cards) ----

    private void DrawOverrideManager(string id, Dictionary<string, LabelStyleOverride> dict,
        IEnumerable<(string key, string label)> types)
    {
        var all = types.Where(t => !string.IsNullOrEmpty(t.key))
            .GroupBy(t => t.key).Select(g => g.First()).ToList();
        var labelByKey = all.ToDictionary(t => t.key, t => t.label);

        var available = all.Where(t => !dict.ContainsKey(t.key)).OrderBy(t => t.label).ToArray();

        if (available.Length > 0) {
            var filter = _labelMgrFilter.TryGetValue(id, out var f) ? f : "";
            ImGui.SetNextItemWidth(250);
            if (ImGui.BeginCombo($"##{id}_add_combo", "Add override...", ImGuiComboFlags.HeightLarge)) {
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint($"##{id}_add_filter", "Search...", ref filter, 100);
                _labelMgrFilter[id] = filter;
                foreach (var (key, label) in available) {
                    if (!string.IsNullOrEmpty(filter)
                        && !label.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (ImGui.Selectable($"{label}##{id}_add_{key}")) {
                        // Seed from the base look (a type resolves to base absent other layers/node weight).
                        dict[key] = LabelStyleOverride.FromStyle(Settings.Labels.Base);
                        _labelMgrFilter[id] = "";
                    }
                }
                ImGui.EndCombo();
            }
        } else {
            ImGui.TextDisabled("All types have an override.");
        }

        string toRemove = null;
        foreach (var kv in dict.OrderBy(k => labelByKey.TryGetValue(k.Key, out var l) ? l : k.Key).ToList()) {
            string header = labelByKey.TryGetValue(kv.Key, out var lbl) ? lbl : kv.Key;
            bool open = SettingsHelpers.SubHeader($"{header}##{id}_{kv.Key}");
            if (open) {
                DrawOverrideControls($"{id}_{kv.Key}", kv.Value);
                if (ImGui.Button($"Remove##{id}_{kv.Key}"))
                    toRemove = kv.Key;
                ImGui.Separator();
            }
            SettingsHelpers.SubHeaderEnd();
        }
        if (toRemove != null)
            dict.Remove(toRemove);
    }
}
