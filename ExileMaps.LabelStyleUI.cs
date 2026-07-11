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

    public void DrawLabelStyleSection()
    {
        ImGui.SeparatorText("Base Style");
        DrawBaseStyleControls(Settings.Labels.Base);

        ImGui.SeparatorText("Favorite Maps Override");
        DrawOverrideControls("fav", Settings.Labels.Favorite);

        ImGui.SeparatorText("Special Maps Override");
        DrawOverrideControls("special", Settings.Labels.Special);

        ImGui.SeparatorText("Content Overrides");
        DrawOverrideManager("content", Settings.Labels.Content,
            Settings.Maps.Content.ContentTypes.Keys);

        ImGui.SeparatorText("Biome Overrides");
        DrawOverrideManager("biome", Settings.Labels.Biome,
            Settings.Maps.Biomes.Biomes.Keys);
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
        Color bc = s.BoxColor;
        if (ColorRGB("Box Color##base", ref bc)) s.BoxColor = bc;
        int bo = s.BoxOpacity;
        if (ImGui.SliderInt("Box Opacity##base", ref bo, 0, 255)) s.BoxOpacity = bo;
        bool bw = s.BoxColorByWeight;
        if (ImGui.Checkbox("Box color by weight##base", ref bw)) s.BoxColorByWeight = bw;

        bool rv = s.BorderVisible;
        if (ImGui.Checkbox("Show border##base", ref rv)) s.BorderVisible = rv;
        Color rc = s.BorderColor;
        if (ColorRGB("Border Color##base", ref rc)) s.BorderColor = rc;
        int ro = s.BorderOpacity;
        if (ImGui.SliderInt("Border Opacity##base", ref ro, 0, 255)) s.BorderOpacity = ro;
        bool rw = s.BorderColorByWeight;
        if (ImGui.Checkbox("Border color by weight##base", ref rw)) s.BorderColorByWeight = rw;
        int rt = s.BorderThickness;
        if (ImGui.SliderInt("Border Thickness##base", ref rt, 1, 8)) s.BorderThickness = rt;
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

        { bool ovr = o.OverrideBoxColor; Row("bcol", ref ovr, () => {
            Color v = o.BoxColor; if (ColorRGB($"Box Color##{id}", ref v)) o.BoxColor = v; });
            o.OverrideBoxColor = ovr; }

        { bool ovr = o.OverrideBoxOpacity; Row("bop", ref ovr, () => {
            int v = o.BoxOpacity; if (ImGui.SliderInt($"Box Opacity##{id}", ref v, 0, 255)) o.BoxOpacity = v; });
            o.OverrideBoxOpacity = ovr; }

        { bool ovr = o.OverrideBoxColorByWeight; Row("bw", ref ovr, () => {
            bool v = o.BoxColorByWeight; if (ImGui.Checkbox($"Box color by weight##{id}", ref v)) o.BoxColorByWeight = v; });
            o.OverrideBoxColorByWeight = ovr; }

        { bool ovr = o.OverrideBorderVisible; Row("rvis", ref ovr, () => {
            bool v = o.BorderVisible; if (ImGui.Checkbox($"Show border##{id}", ref v)) o.BorderVisible = v; });
            o.OverrideBorderVisible = ovr; }

        { bool ovr = o.OverrideBorderColor; Row("rcol", ref ovr, () => {
            Color v = o.BorderColor; if (ColorRGB($"Border Color##{id}", ref v)) o.BorderColor = v; });
            o.OverrideBorderColor = ovr; }

        { bool ovr = o.OverrideBorderOpacity; Row("rop", ref ovr, () => {
            int v = o.BorderOpacity; if (ImGui.SliderInt($"Border Opacity##{id}", ref v, 0, 255)) o.BorderOpacity = v; });
            o.OverrideBorderOpacity = ovr; }

        { bool ovr = o.OverrideBorderColorByWeight; Row("rw", ref ovr, () => {
            bool v = o.BorderColorByWeight; if (ImGui.Checkbox($"Border color by weight##{id}", ref v)) o.BorderColorByWeight = v; });
            o.OverrideBorderColorByWeight = ovr; }

        { bool ovr = o.OverrideBorderThickness; Row("rt", ref ovr, () => {
            int v = o.BorderThickness; if (ImGui.SliderInt($"Border Thickness##{id}", ref v, 1, 8)) o.BorderThickness = v; });
            o.OverrideBorderThickness = ovr; }
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
