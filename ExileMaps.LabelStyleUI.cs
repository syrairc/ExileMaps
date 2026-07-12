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
    // Per-manager combo selection (idPrefix -> selected index into its available-types list).
    private readonly Dictionary<string, int> _labelMgrSel = new();
    // Last-serialized label config, to detect edits. Labels are plain POCO fields (not observable),
    // so they don't raise PropertyChanged like weights do - without this the profile never gets
    // marked dirty and LoadProfile clobbers the edits on the next launch.
    private string _labelsSnapshot;

    public void DrawLabelStyleSection()
    {
        if (Section("Base Style"))
            DrawBaseStyleControls(Settings.Labels.Base);

        // Override sections collapsed by default - most users only touch a couple.
        if (ImGui.CollapsingHeader("Favorite Maps Override"))
            DrawOverrideControls("fav", Settings.Labels.Favorite);

        if (ImGui.CollapsingHeader("Special Maps Override"))
            DrawOverrideControls("special", Settings.Labels.Special);

        if (ImGui.CollapsingHeader("Content Overrides"))
            DrawOverrideManager("content", Settings.Labels.Content,
                Settings.Maps.Content.ContentTypes.Keys);

        if (ImGui.CollapsingHeader("Biome Overrides"))
            DrawOverrideManager("biome", Settings.Labels.Biome,
                Settings.Maps.Biomes.Biomes.Keys);

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
        if (ImGui.Checkbox("Show box##base", ref bv)) s.BoxVisible = bv;
        // Border lives on the box: no box, no border.
        if (s.BoxVisible) {
            Color bc = s.BoxColor;
            if (ColorRGBA("Box Color##base", ref bc)) s.BoxColor = bc;
            bool bw = s.BoxColorByWeight;
            if (ImGui.Checkbox("Box color by weight##base", ref bw)) s.BoxColorByWeight = bw;

            bool rv = s.BorderVisible;
            if (ImGui.Checkbox("Show border##base", ref rv)) s.BorderVisible = rv;
            if (s.BorderVisible) {
                Color rc = s.BorderColor;
                if (ColorRGBA("Border Color##base", ref rc)) s.BorderColor = rc;
                bool rw = s.BorderColorByWeight;
                if (ImGui.Checkbox("Border color by weight##base", ref rw)) s.BorderColorByWeight = rw;
                int rt = s.BorderThickness;
                if (ImGui.SliderInt("Border Thickness##base", ref rt, 1, 8)) s.BorderThickness = rt;
            }
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

        { bool ovr = o.OverrideBoxVisible; Row("bvis", ref ovr, () => {
            bool v = o.BoxVisible; if (ImGui.Checkbox($"Show box##{id}", ref v)) o.BoxVisible = v; });
            o.OverrideBoxVisible = ovr; }

        // Box color/opacity rows only when the box is shown for this override.
        if (o.BoxVisible) {
            { bool ovr = o.OverrideBoxColor; Row("bcol", ref ovr, () => {
                Color v = o.BoxColor; if (ColorRGBA($"Box Color##{id}", ref v)) o.BoxColor = v; });
                o.OverrideBoxColor = ovr; }

            { bool ovr = o.OverrideBoxColorByWeight; Row("bw", ref ovr, () => {
                bool v = o.BoxColorByWeight; if (ImGui.Checkbox($"Box color by weight##{id}", ref v)) o.BoxColorByWeight = v; });
                o.OverrideBoxColorByWeight = ovr; }

            // Border lives on the box: no box, no border.
            { bool ovr = o.OverrideBorderVisible; Row("rvis", ref ovr, () => {
                bool v = o.BorderVisible; if (ImGui.Checkbox($"Show border##{id}", ref v)) o.BorderVisible = v; });
                o.OverrideBorderVisible = ovr; }

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
        }
    }

    // ---- dynamic per-type manager (Option A: add-combo + collapsible cards) ----

    private void DrawOverrideManager(string id, Dictionary<string, LabelStyleOverride> dict,
        IEnumerable<string> allTypes)
    {
        var available = allTypes.Where(t => !string.IsNullOrEmpty(t) && !dict.ContainsKey(t))
            .OrderBy(t => t).ToArray();

        if (available.Length > 0) {
            if (!_labelMgrSel.TryGetValue(id, out int sel) || sel >= available.Length) sel = 0;
            ImGui.SetNextItemWidth(200);
            if (ImGui.Combo($"##{id}_add_combo", ref sel, available, available.Length))
                _labelMgrSel[id] = sel;
            ImGui.SameLine();
            if (ImGui.Button($"+ Add##{id}")) {
                var type = available[Math.Clamp(sel, 0, available.Length - 1)];
                // Seed from the base look (a type resolves to base absent other layers/node weight).
                dict[type] = LabelStyleOverride.FromStyle(Settings.Labels.Base);
                _labelMgrSel[id] = 0;
            }
        } else {
            ImGui.TextDisabled("All types have an override.");
        }

        string toRemove = null;
        foreach (var kv in dict.OrderBy(k => k.Key).ToList()) {
            if (ImGui.CollapsingHeader($"{kv.Key}##{id}_{kv.Key}")) {
                DrawOverrideControls($"{id}_{kv.Key}", kv.Value);
                if (ImGui.Button($"Remove##{id}_{kv.Key}"))
                    toRemove = kv.Key;
                ImGui.Separator();
            }
        }
        if (toRemove != null)
            dict.Remove(toRemove);
    }
}
