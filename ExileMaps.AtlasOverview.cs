// Atlas Overview: reachable-content tally, progress readout, node search ping.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using ExileCore2.Shared;
using ImGuiNET;
using ExileMaps.Classes;
using GameOffsets2.Native;

namespace ExileMaps;

public partial class ExileMapsCore
{
    // ---- Atlas stats (features 1 and 3) ----

    private struct AtlasStats
    {
        public int MapsRun;                 // visited
        public int MapsCompleted;           // IsCompleted
        public int AtlasPointsTotal;        // GivesAtlasPoint anywhere
        public int AtlasPointsReachable;    // GivesAtlasPoint within ReachableSteps
        public int AtlasQuestsReachable;    // HasAtlasQuest within ReachableSteps
        public int ReachableMaps;           // non-visited nodes within ReachableSteps
        // Read-only once returned; callers must not mutate (it is the cached instance).
        public Dictionary<string, (int count, Color color)> ContentCounts;
    }

    private AtlasStats? cachedAtlasStats;
    private (int ver, int wver, int n) cachedAtlasStatsSig = (-1, -1, -1);

    // Recomputed only when cache/weights version or the step window changes.
    private AtlasStats ComputeAtlasStats()
    {
        int n = Settings.AtlasOverview.ReachableSteps;
        var sig = (mapCacheVersion, weightsRecalcVersion, n);
        if (cachedAtlasStats.HasValue && cachedAtlasStatsSig.Equals(sig))
            return cachedAtlasStats.Value;

        var stepCounts = ComputeStepCounts();
        int Steps(Node node) => stepCounts.TryGetValue(node.Coordinates, out var s) ? s : int.MaxValue;

        var stats = new AtlasStats { ContentCounts = new Dictionary<string, (int, Color)>() };

        lock (mapCacheLock)
        {
            foreach (var node in mapCache.Values)
            {
                if (node.IsVisited) stats.MapsRun++;
                if (node.IsCompleted) stats.MapsCompleted++;
                if (node.GivesAtlasPoint) stats.AtlasPointsTotal++;

                if (node.IsVisited) continue;
                if (Steps(node) > n) continue;

                stats.ReachableMaps++;
                if (node.GivesAtlasPoint) stats.AtlasPointsReachable++;
                if (node.HasAtlasQuest) stats.AtlasQuestsReachable++;

                foreach (var (_, content) in node.Content)
                {
                    var key = content.Name ?? "";
                    if (key.Length == 0) continue;
                    if (stats.ContentCounts.TryGetValue(key, out var cur))
                        stats.ContentCounts[key] = (cur.count + 1, cur.color);
                    else
                        stats.ContentCounts[key] = (1, content.Color);
                }
            }
        }

        cachedAtlasStats = stats;
        cachedAtlasStatsSig = sig;
        return stats;
    }

    // ---- Atlas Overview panel (features 1 and 2) ----

    private bool atlasOverviewOpen = false;
    private string searchPingText = "";

    private void DrawAtlasOverviewPanel()
    {
        try
        {
            bool justOpened = !overviewWasOpen; overviewWasOpen = true;
            BeginPersistedWindow(Settings.AtlasOverview.PanelRect, justOpened, screenCenter, new Vector2(340, 440));
            ImGui.SetNextWindowSizeConstraints(new Vector2(300, 220), new Vector2(float.MaxValue, float.MaxValue));
            ImGui.SetNextWindowBgAlpha(0.9f);

            if (ImGui.Begin("Atlas Overview###atlasoverview", ref atlasOverviewOpen,
                    ImGuiWindowFlags.NoCollapse))
            {
                SavePersistedWindow(Settings.AtlasOverview.PanelRect);

                // --- Search ping box (feature 2; drawing added in Task 6) ---
                ImGui.SetNextItemWidth(220);
                ImGui.InputTextWithHint("##atlassearch", "Search nodes...", ref searchPingText, 64);
                ImGui.SameLine();
                if (ImGui.Button("Clear##atlassearch")) searchPingText = "";
                if (!string.IsNullOrEmpty(searchPingText))
                {
                    var (total, onscreen) = CountSearchMatches();
                    ImGui.TextDisabled($"{total} matches ({Math.Max(0, total - onscreen)} off-screen)");
                }

                ImGui.Separator();

                // --- Reachable-content tally (feature 1) ---
                int n = Settings.AtlasOverview.ReachableSteps;
                ImGui.SetNextItemWidth(160);
                if (ImGui.SliderInt("Reachable within (steps)", ref n, 1, 50))
                    Settings.AtlasOverview.ReachableSteps.Value = n;

                var s = ComputeAtlasStats();
                if (s.ReachableMaps == 0)
                {
                    ImGui.TextDisabled("No reachable maps in range (open the Atlas / widen the step window).");
                }
                else if (ImGui.BeginTable("atlas_overview_content", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
                {
                    ImGui.TableSetupColumn("##swatch", ImGuiTableColumnFlags.WidthFixed, 18);
                    ImGui.TableSetupColumn("Content", ImGuiTableColumnFlags.WidthStretch, 200);
                    ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthFixed, 50);
                    foreach (var kv in s.ContentCounts.OrderByDescending(x => x.Value.count))
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        var c = kv.Value.color;
                        ImGui.ColorButton($"##c_{kv.Key}", new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, 1f),
                            ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoInputs, new Vector2(14, 14));
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(kv.Key);
                        ImGui.TableNextColumn();
                        ImGui.Text(kv.Value.count.ToString());
                    }
                    ImGui.EndTable();
                }

                ImGui.Separator();
                ImGui.Text($"Atlas points: {s.AtlasPointsReachable}");
                ImGui.Text($"Atlas quests: {s.AtlasQuestsReachable}");
                ImGui.Text($"Reachable maps: {s.ReachableMaps}");

                var visibleTours = Settings.Tours.Tours.Values
                    .Where(t => Settings.Tours.ShowTours && t.Show && t.Stops.Count > 0).ToList();
                if (visibleTours.Count > 0)
                {
                    ImGui.Separator();
                    ImGui.Text("Visible tours:");
                    foreach (var t in visibleTours)
                    {
                        var c = t.Color;
                        ImGui.ColorButton($"##tc_{t.Id}", new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, 1f),
                            ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoInputs, new Vector2(14, 14));
                        ImGui.SameLine();
                        ImGui.TextUnformatted($"{t.Name}  ({t.Stops.Count} stops)");
                        if (t.Skipped.Count > 0)
                            ImGui.TextDisabled($"   skipped: {string.Join(", ", t.Skipped)}");
                    }
                }

                ImGui.Separator();
                if (ImGui.Button("Close##atlasoverview")) atlasOverviewOpen = false;
            }
            ImGui.End();
        }
        catch (Exception e)
        {
            LogError("Error drawing atlas overview panel: " + e.Message);
            atlasOverviewOpen = false;
        }
    }

    // Floating search box drawn directly on the atlas (top-center). Drives the same searchPingText
    // as the Atlas Overview panel, so DrawSearchPing highlights matches either way.
    private void DrawAtlasSearchBox()
    {
        if (!Settings.Features.ShowAtlasSearchBox) return;

        try
        {
            var r = cachedScreenRect;
            const float w = 280f;
            // Sit centered just below the panel button bar (whose bottom is r.Bottom - 64; see DrawPanelButtonBar).
            float y = r.Bottom - 64f + 8f;
            Vector2 pos = new(r.Center.X - w / 2f, y);

            ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(w, 0), ImGuiCond.Always);
            ImGui.SetNextWindowBgAlpha(0.55f);

            var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                      | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse
                      | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing
                      | ImGuiWindowFlags.AlwaysAutoResize;

            if (ImGui.Begin("##atlassearchbox", flags))
            {
                ImGui.SetNextItemWidth(190);
                ImGui.InputTextWithHint("##atlassearchbar", "Search nodes...", ref searchPingText, 64);
                ImGui.SameLine();
                if (ImGui.Button("Clear##atlassearchbar")) searchPingText = "";
                if (!string.IsNullOrEmpty(searchPingText))
                {
                    var (total, onscreen) = CountSearchMatches();
                    ImGui.TextDisabled($"{total} matches ({Math.Max(0, total - onscreen)} off-screen)");
                }
            }
            ImGui.End();
        }
        catch (Exception e) { DebugSwallow("DrawAtlasSearchBox", e); }
    }

    // Match by map name (contains) or any content name (contains), case-insensitive.
    private static bool MatchesSearch(Node node, string text)
    {
        if (node == null || string.IsNullOrEmpty(text)) return false;
        if (node.Name != null && node.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            return true;
        foreach (var (_, content) in node.Content)
            if (content.Name != null && content.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase))
                return true;
        return false;
    }

    private (int total, int onscreen) cachedSearchCount = (0, 0);
    private (int ver, string text) cachedSearchCountSig = (-1, "");
    private int searchOnScreenThisFrame = 0;

    // Total matches memoized over the cache; on-screen count comes from the last DrawSearchPing pass.
    private (int total, int onscreen) CountSearchMatches()
    {
        var text = searchPingText;
        var sig = (mapCacheVersion, text ?? "");
        if (!cachedSearchCountSig.Equals(sig))
        {
            int total = 0;
            lock (mapCacheLock)
                foreach (var node in mapCache.Values)
                    if (MatchesSearch(node, text)) total++;
            cachedSearchCount = (total, searchOnScreenThisFrame);
            cachedSearchCountSig = sig;
        }
        return (cachedSearchCount.total, searchOnScreenThisFrame);
    }

    // Pulsing ring on each on-screen node matching the search box. Uses the already-resolved
    // nodePositions (no extra cache scan for drawing).
    private void DrawSearchPing()
    {
        if (string.IsNullOrEmpty(searchPingText)) { searchOnScreenThisFrame = 0; return; }
        // Minimap mode never repopulates nodePositions; skip so we don't ping stale positions.
        if (ShowMinimap) { searchOnScreenThisFrame = 0; return; }

        int onscreen = 0;
        // Pulse 0..1 via wall-clock seconds (smooth under frame-time jitter); radius and alpha breathe.
        // 9 rad/s ~= the old 0.15/frame at 60fps.
        float pulse = (MathF.Sin(AnimSeconds * 9f) + 1f) * 0.5f; // 0..1
        Color baseColor = Settings.AtlasOverview.SearchPingColor;
        int alpha = (int)(120 + 135 * pulse);
        var color = Color.FromArgb(Math.Clamp(alpha, 0, 255), baseColor.R, baseColor.G, baseColor.B);

        foreach (var (node, rect) in nodePositions)
        {
            try
            {
                if (!MatchesSearch(node, searchPingText)) continue;
                onscreen++;
                var center = rect.Center;
                float radius = MathF.Max(rect.Width, rect.Height) * (0.55f + 0.25f * pulse);
                Graphics.DrawCircle(center, radius, color, 3f, 24);
            }
            catch (Exception e) { DebugSwallow("DrawSearchPing", e); }
        }
        searchOnScreenThisFrame = onscreen;
    }

    // ---- Progress readout (feature 3) ----

    private void DrawProgressReadout()
    {
        if (!Settings.AtlasOverview.ShowProgressReadout) return;

        try
        {
            var s = ComputeAtlasStats();
            // ASCII only (ImGui/Graphics font renders ASCII only).
            string line = $"Run {s.MapsRun}  Done {s.MapsCompleted}  Atlas pts {s.AtlasPointsReachable}/{s.AtlasPointsTotal} reachable";

            var box = Graphics.MeasureText(line) + new Vector2(12, 8);
            const float margin = 12f;
            var r = cachedScreenRect;
            Vector2 pos = Settings.AtlasOverview.ReadoutCorner switch
            {
                ReadoutCorner.TopRight    => new Vector2(r.Right - box.X - margin, r.Top + margin),
                ReadoutCorner.BottomLeft  => new Vector2(r.Left + margin,          r.Bottom - box.Y - margin),
                ReadoutCorner.BottomRight => new Vector2(r.Right - box.X - margin, r.Bottom - box.Y - margin),
                _                         => new Vector2(r.Left + margin,          r.Top + margin),
            };

            // Draw box+text directly (DrawCenteredTextWithBackground centers and culls; we want a fixed corner).
            Graphics.DrawBox(pos, pos + box, Settings.Graphics.BackgroundColor, 5.0f);
            Graphics.DrawText(line, pos + new Vector2(6, 4), Settings.Graphics.FontColor);
        }
        catch (Exception e) { DebugSwallow("DrawProgressReadout", e); }
    }

}
