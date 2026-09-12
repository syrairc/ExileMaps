using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using ExileCore2.PoEMemory.Elements.AtlasElements;
using ExileMaps.Classes;

namespace ExileMaps;

public partial class ExileMapsCore
{
    private Patcher hackPatcher;
    private AtlasCamera atlasCamera;
    private int hackPid;
    private int hackVerifyTick;
    private (float, float) zoomLimitsWritten = (float.NaN, float.NaN);

    private bool HacksCameraPanReady
    {
        get
        {
            var p = hackPatcher;
            return Settings.Hacks.EnableMemoryWrites
                   && Settings.Hacks.AtlasCameraPan
                   && p is { Attached: true }
                   && p.IsApplied(PatchId.BlockCrashLog);
        }
    }

    private void TickHacks()
    {
        try
        {
            if (!Settings.Hacks.EnableMemoryWrites)
            {
                if (hackPatcher != null) ShutdownHacks();
                return;
            }

            var process = GameController?.Memory?.Process;
            if (process == null) return;

            if (hackPatcher == null || hackPid != process.Id)
            {
                ShutdownHacks();
                hackPid = process.Id;
                hackPatcher = new Patcher();
                atlasCamera = new AtlasCamera();
                zoomLimitsWritten = (float.NaN, float.NaN);

                if (!hackPatcher.Attach(hackPid, ResolveModuleBase(process)))
                    LogError($"ExileMaps hacks: {hackPatcher.LastError}");
            }

            var every = Math.Max(1, Settings.Hacks.VerifyEveryFrames);
            if (hackVerifyTick++ % every == 0)
                hackPatcher.Verify(PatchId.BlockCrashLog);
            hackPatcher.Set(PatchId.BlockCrashLog, true);

            if (!hackPatcher.IsApplied(PatchId.BlockCrashLog))
            {
                hackPatcher.Set(PatchId.KillAtlasFog, false);
                hackPatcher.Set(PatchId.ForceAtlasFog, false);
                hackPatcher.Set(PatchId.UnlockAtlasZoomOut, false);
                hackPatcher.Set(PatchId.UnlockAtlasZoomIn, false);
                return;
            }

            hackPatcher.Set(PatchId.KillAtlasFog, Settings.Hacks.AtlasFog);
            hackPatcher.Set(PatchId.ForceAtlasFog, Settings.Hacks.AtlasFogAll && !Settings.Hacks.AtlasFog);
            hackPatcher.Set(PatchId.UnlockAtlasZoomOut, Settings.Hacks.AtlasZoom);
            hackPatcher.Set(PatchId.UnlockAtlasZoomIn, Settings.Hacks.AtlasZoom);

            var wantLimits = (Settings.Hacks.ZoomOutFactor, Settings.Hacks.ZoomInFactor);
            if (Settings.Hacks.AtlasZoom && zoomLimitsWritten != wantLimits)
            {
                hackPatcher.SetZoomLimits(wantLimits.Item1, wantLimits.Item2);
                zoomLimitsWritten = wantLimits;
            }

            if (!Settings.Hacks.AtlasCameraPan) atlasCamera?.Cancel();
        }
        catch (Exception ex)
        {
            LogError($"ExileMaps hacks: {ex.Message}");
        }
    }

    private void RenderHacks()
    {
        if (!Settings.Hacks.EnableMemoryWrites || !Settings.Hacks.AtlasCameraPan) return;

        var camera = atlasCamera;
        var patcher = hackPatcher;
        if (camera == null || patcher == null) return;

        var wasBusy = camera.Busy;
        camera.Tick(AtlasPanel, patcher, screenCenter, Settings.Hacks.PanSpeed);

        if (!wasBusy || camera.Busy) return;

        if (!string.IsNullOrEmpty(camera.LastError))
            LogError($"ExileMaps camera pan: {camera.LastError}");

        if (Settings.Features.DebugLogging)
            foreach (var line in camera.Trace) LogMessage($"pan {line}");
    }

    private void GotoNode(Node node)
    {
        if (node == null || !HacksCameraPanReady) return;
        atlasCamera?.Goto(node);
    }

    private void ShutdownHacks()
    {
        try
        {
            atlasCamera?.Cancel();
            atlasCamera = null;
            hackPatcher?.Dispose();
            hackPatcher = null;
            hackPid = 0;
        }
        catch (Exception ex)
        {
            LogError($"ExileMaps hacks shutdown: {ex.Message}");
        }
    }

    private long ResolveModuleBase(Process process)
    {
        try
        {
            return process.MainModule?.BaseAddress.ToInt64() ?? 0;
        }
        catch (Exception)
        {
            return GameController?.Memory?.AddressOfProcess ?? 0;
        }
    }

    public override void OnClose() => ShutdownHacks();

    public override void OnPluginDestroyForHotReload() => ShutdownHacks();

    public override void Dispose()
    {
        ShutdownHacks();
        base.Dispose();
    }
}

internal sealed class AtlasCamera
{
    private const float DoneWithinPx = 10f;
    private const int StepEveryFrames = 3;
    private const int MaxSteps = 200;
    private const int MaxTraceLines = 14;
    private const int ReselectEvery = 4;
    private const int NoProgressSteps = 12;
    private const int NearestCount = 48;
    private const int MinSamples = 8;
    private const float Damping = 0.8f;
    private const float MaxStepFraction = 0.75f;
    private const float MaxDrift = 250000f;
    private const float TrimFloorPx = 15f;
    private const float AcceptMedianPx = 150f;
    private const float CentreProofPx = 60f;

    private Node _target;
    private int _steps;
    private int _frame;
    private long _lastTicks;
    private float _sign = 1f;
    private bool _flipped;

    private Vector2 _startCam;
    private bool _haveStart;
    private Patcher _restorePatcher;
    private long _restoreAddr;

    private float _bestErrorLen;
    private int _sinceImproved;

    private readonly List<AtlasNodeDescription> _samples = new();
    private int _sinceReselect;

    public readonly List<string> Trace = new();

    public bool Busy => _target != null;
    public string LastError { get; private set; }

    public void Goto(Node node)
    {
        _target = node;
        _steps = 0;
        _frame = 0;
        _lastTicks = 0;
        _sign = 1f;
        _flipped = false;
        _haveStart = false;
        _restorePatcher = null;
        _restoreAddr = 0;
        _bestErrorLen = float.MaxValue;
        _sinceImproved = 0;
        _sinceReselect = 0;
        _samples.Clear();
        LastError = null;
        Trace.Clear();
    }

    public void Cancel() => _target = null;

    public void Tick(AtlasPanel panel, Patcher patcher, Vector2 screenCenter, float speed)
    {
        if (_target == null) return;

        if (panel == null || !panel.IsVisible || patcher == null || !patcher.Attached)
        {
            _target = null;
            return;
        }

        if (_frame++ % StepEveryFrames != 0) return;

        try { StepOnce(panel, patcher, screenCenter, speed); }
        catch (Exception ex)
        {
            LastError = "pan failed: " + ex.Message;
            Restore();
            _target = null;
        }
    }

    private void StepOnce(AtlasPanel panel, Patcher patcher, Vector2 screenCenter, float speed)
    {
        if (!TryNodeScreen(_target, out var center))
        {
            Fail("cannot work out where that node is - the atlas has not laid it out yet");
            return;
        }

        var error = screenCenter - center;
        if (Math.Abs(error.X) < DoneWithinPx && Math.Abs(error.Y) < DoneWithinPx)
        {
            _target = null;
            return;
        }

        if (++_steps > MaxSteps) { Fail("gave up, the view is not converging on the node"); return; }

        var world3 = _target.MapNode?.Description3D;
        if (world3 == null) { Fail("that node has no world position to aim at"); return; }

        var helper = panel.CameraHelper;
        var baseAddr = helper?.Address ?? 0;
        if (baseAddr == 0) { Fail("the atlas camera is not resolved yet"); return; }
        var addr = baseAddr + AtlasCameraHelper.PanOffset;

        var raw = patcher.ReadFloats(addr, 2);
        if (raw == null) { Fail("could not read the atlas camera pan"); return; }
        var cam = new Vector2(raw[0], raw[1]);

        _restorePatcher = patcher;
        _restoreAddr = addr;
        if (!_haveStart) { _startCam = cam; _haveStart = true; }

        var errorLen = error.Length();
        if (errorLen < _bestErrorLen - 2f) { _bestErrorLen = errorLen; _sinceImproved = 0; }
        else if (++_sinceImproved > NoProgressSteps)
        {
            Fail("the node is not getting any closer - the atlas has probably not loaded it yet");
            return;
        }

        if (_sinceReselect <= 0 || _samples.Count < MinSamples)
        {
            Reselect(panel, screenCenter);
            _sinceReselect = ReselectEvery;
        }
        _sinceReselect--;

        if (!Fit(screenCenter, out var fit, out var why))
        {
            Fail("could not solve the atlas projection: " + why);
            return;
        }

        if (!_flipped && errorLen > _bestErrorLen * 1.05f) { _sign = -_sign; _flipped = true; }

        var ticks = Stopwatch.GetTimestamp();
        var dt = _lastTicks == 0
            ? StepEveryFrames / 60f
            : (float)((ticks - _lastTicks) / (double)Stopwatch.Frequency);
        _lastTicks = ticks;
        dt = Math.Clamp(dt, 1f / 500f, 1f / 6f);

        var approach = (1f - MathF.Exp(-Math.Max(0.1f, speed) * dt)) * Damping;

        var camScreen = fit.Project(new Vector3(cam.X, cam.Y, world3.Position.Z));
        var centred = (camScreen - screenCenter).Length() <= CentreProofPx;

        Vector2 next;
        if (centred)
        {
            var goal = new Vector2(world3.Position.X, world3.Position.Y);
            next = cam + (goal - cam) * approach;
        }
        else
        {
            var want = error * approach;
            var cap = screenCenter.Length() * MaxStepFraction;
            if (want.Length() > cap) want *= cap / want.Length();
            next = cam - fit.SolveXY(want) * _sign;
        }

        if ((next - _startCam).Length() > MaxDrift)
        {
            Fail("the pan is running away from where it started, putting the view back");
            return;
        }

        if (Trace.Count < MaxTraceLines)
            Trace.Add($"{_steps}: node {center.X:F0},{center.Y:F0} err {error.X:F0},{error.Y:F0} " +
                      $"cam {cam.X:F1},{cam.Y:F1} -> {next.X:F1},{next.Y:F1} | fit {fit.Samples} nodes " +
                      $"median {fit.Median:F1}px worst {fit.Worst:F1}px, cam projects to " +
                      $"{camScreen.X:F0},{camScreen.Y:F0} {(centred ? "CENTRED" : "not centred")}");

        if (!panel.IsVisible) { _target = null; return; }

        Apply(patcher, addr, next);
    }

    private readonly struct Projection
    {
        private readonly float _a11, _a12, _a13, _a21, _a22, _a23;
        private readonly Vector2 _b;
        public readonly int Samples;
        public readonly float Median;
        public readonly float Worst;

        public Projection(float a11, float a12, float a13, float a21, float a22, float a23,
                          Vector2 b, int samples, float median, float worst)
        {
            _a11 = a11; _a12 = a12; _a13 = a13;
            _a21 = a21; _a22 = a22; _a23 = a23;
            _b = b; Samples = samples; Median = median; Worst = worst;
        }

        public Vector2 Project(Vector3 w) => new(
            _a11 * w.X + _a12 * w.Y + _a13 * w.Z + _b.X,
            _a21 * w.X + _a22 * w.Y + _a23 * w.Z + _b.Y);

        public Vector2 SolveXY(Vector2 v)
        {
            var det = _a11 * _a22 - _a12 * _a21;
            if (Math.Abs(det) < 1e-9f) return Vector2.Zero;
            return new Vector2((_a22 * v.X - _a12 * v.Y) / det, (_a11 * v.Y - _a21 * v.X) / det);
        }
    }

    private void Reselect(AtlasPanel panel, Vector2 screenCenter)
    {
        _samples.Clear();

        var descriptions = panel.Descriptions;
        if (descriptions == null) return;

        var best = new List<(float Dist, AtlasNodeDescription Node)>(NearestCount + 1);
        var cutoff = float.MaxValue;

        foreach (var d in descriptions)
        {
            if (d?.Description3D == null || d.Element == null) continue;

            float dist;
            try
            {
                var c = d.Element.GetClientRectCache.Center;
                dist = Vector2.DistanceSquared(new Vector2(c.X, c.Y), screenCenter);
            }
            catch (Exception) { continue; }

            if (best.Count == NearestCount && dist >= cutoff) continue;

            var at = best.FindIndex(e => e.Dist > dist);
            if (at < 0) best.Add((dist, d)); else best.Insert(at, (dist, d));
            if (best.Count > NearestCount) best.RemoveAt(best.Count - 1);
            cutoff = best[^1].Dist;
        }

        foreach (var (_, node) in best) _samples.Add(node);
    }

    private bool Fit(Vector2 screenCenter, out Projection fit, out string why)
    {
        fit = default;
        why = null;

        var worlds = new List<Vector3>(_samples.Count);
        var screens = new List<Vector2>(_samples.Count);
        var threw = 0;

        foreach (var d in _samples)
        {
            try
            {
                var rect = d.Element.GetClientRect();
                if (rect.Width <= 0 && rect.Height <= 0) continue;
                screens.Add(new Vector2(rect.Center.X, rect.Center.Y));
                worlds.Add(d.Description3D.Position);
            }
            catch (Exception) { threw++; }
        }

        var counts = $"{worlds.Count} of {_samples.Count} held samples ({threw} threw)";
        if (worlds.Count < MinSamples) { why = counts; return false; }

        if (!Solve(worlds, screens, out fit)) { why = "the samples are degenerate. " + counts; return false; }

        var cut = Math.Max(TrimFloorPx, fit.Median * 3f);
        var keptW = new List<Vector3>(worlds.Count);
        var keptS = new List<Vector2>(screens.Count);
        for (var i = 0; i < worlds.Count; i++)
        {
            if ((fit.Project(worlds[i]) - screens[i]).Length() > cut) continue;
            keptW.Add(worlds[i]);
            keptS.Add(screens[i]);
        }

        if (keptW.Count >= MinSamples && keptW.Count < worlds.Count)
            Solve(keptW, keptS, out fit);

        if (fit.Median <= AcceptMedianPx) return true;

        why = $"median residual {fit.Median:F0}px, worst {fit.Worst:F0}px over {counts}";
        return false;
    }

    private static bool Solve(List<Vector3> worlds, List<Vector2> screens, out Projection fit)
    {
        fit = default;
        var n = worlds.Count;

        var wBar = Vector3.Zero;
        var sBar = Vector2.Zero;
        for (var i = 0; i < n; i++) { wBar += worlds[i]; sBar += screens[i]; }
        wBar /= n;
        sBar /= n;

        var m = new float[3, 3];
        var rx = new float[3];
        var ry = new float[3];
        for (var i = 0; i < n; i++)
        {
            var dw = worlds[i] - wBar;
            var ds = screens[i] - sBar;
            var w = new[] { dw.X, dw.Y, dw.Z };
            for (var r = 0; r < 3; r++)
            {
                for (var c = 0; c < 3; c++) m[r, c] += w[r] * w[c];
                rx[r] += ds.X * w[r];
                ry[r] += ds.Y * w[r];
            }
        }

        var ridge = 1e-6f * (m[0, 0] + m[1, 1] + m[2, 2]) + 1e-3f;
        for (var r = 0; r < 3; r++) m[r, r] += ridge;

        if (!Invert3(m, out var inv)) return false;

        var a = Apply3(inv, rx);
        var b = Apply3(inv, ry);
        var offset = sBar - new Vector2(
            a[0] * wBar.X + a[1] * wBar.Y + a[2] * wBar.Z,
            b[0] * wBar.X + b[1] * wBar.Y + b[2] * wBar.Z);

        var candidate = new Projection(a[0], a[1], a[2], b[0], b[1], b[2], offset, n, 0f, 0f);

        var residuals = new float[n];
        var worst = 0f;
        for (var i = 0; i < n; i++)
        {
            residuals[i] = (candidate.Project(worlds[i]) - screens[i]).Length();
            if (residuals[i] > worst) worst = residuals[i];
        }
        Array.Sort(residuals);

        fit = new Projection(a[0], a[1], a[2], b[0], b[1], b[2], offset, n, residuals[n / 2], worst);
        return true;
    }

    private static bool Invert3(float[,] m, out float[,] inv)
    {
        inv = new float[3, 3];

        var c00 = m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1];
        var c01 = m[1, 2] * m[2, 0] - m[1, 0] * m[2, 2];
        var c02 = m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0];

        var det = m[0, 0] * c00 + m[0, 1] * c01 + m[0, 2] * c02;
        if (Math.Abs(det) < 1e-12f) return false;

        inv[0, 0] = c00 / det;
        inv[1, 0] = c01 / det;
        inv[2, 0] = c02 / det;
        inv[0, 1] = (m[0, 2] * m[2, 1] - m[0, 1] * m[2, 2]) / det;
        inv[1, 1] = (m[0, 0] * m[2, 2] - m[0, 2] * m[2, 0]) / det;
        inv[2, 1] = (m[0, 1] * m[2, 0] - m[0, 0] * m[2, 1]) / det;
        inv[0, 2] = (m[0, 1] * m[1, 2] - m[0, 2] * m[1, 1]) / det;
        inv[1, 2] = (m[0, 2] * m[1, 0] - m[0, 0] * m[1, 2]) / det;
        inv[2, 2] = (m[0, 0] * m[1, 1] - m[0, 1] * m[1, 0]) / det;
        return true;
    }

    private static float[] Apply3(float[,] m, float[] v) => new[]
    {
        m[0, 0] * v[0] + m[0, 1] * v[1] + m[0, 2] * v[2],
        m[1, 0] * v[0] + m[1, 1] * v[1] + m[1, 2] * v[2],
        m[2, 0] * v[0] + m[2, 1] * v[1] + m[2, 2] * v[2],
    };

    private static bool TryNodeScreen(Node node, out Vector2 pos)
    {
        pos = default;

        var element = node.MapNode?.Element;
        if (element == null) return false;

        var rect = element.GetClientRect();
        if (rect.Width <= 0 && rect.Height <= 0) return false;

        pos = new Vector2(rect.Center.X, rect.Center.Y);
        return true;
    }

    private void Apply(Patcher patcher, long addr, Vector2 pan)
    {
        if (float.IsNaN(pan.X) || float.IsNaN(pan.Y) ||
            float.IsInfinity(pan.X) || float.IsInfinity(pan.Y))
        {
            Fail("computed a nonsense camera pan, not writing it");
            return;
        }

        if (!patcher.WriteFloats(addr, pan.X, pan.Y))
            Fail(patcher.LastError ?? "camera pan write failed");
    }

    private void Restore()
    {
        if (!_haveStart || _restorePatcher == null || _restoreAddr == 0) return;
        _restorePatcher.WriteFloats(_restoreAddr, _startCam.X, _startCam.Y);
        _haveStart = false;
    }

    private void Fail(string why)
    {
        LastError = why;
        Restore();
        _target = null;
    }
}


internal enum PatchId
{
    KillAtlasFog,
    ForceAtlasFog,
    UnlockAtlasZoomOut,
    UnlockAtlasZoomIn,
    BlockCrashLog,
}

internal enum PatchState
{
    Off,
    Patched,
    Failed,
}

internal enum PatchKind
{
    Nop,
    RetargetFloat,
    RetAtEntry,
    Replace,
}

internal sealed class PatchSite
{
    public PatchId Id { get; init; }
    public PatchKind Kind { get; init; }

    public string Signature { get; init; }
    public string Anchor { get; init; }

    public int SigOffset { get; init; }
    public int Length { get; init; }

    public int Slot { get; init; } = -1;
    public byte[] Guard { get; init; }
    public byte[] From { get; init; }
    public byte[] To { get; init; }

    public long ReferenceAddress { get; init; }

    public string Note { get; init; }

    public IntPtr Address;
    public byte[] Original;
    public byte[] Patched;
    public float Stock;
    public bool Usable;
    public bool IsPatched;
    public bool Preexisting;

    public bool AppliedByUs;
}

internal sealed class Patcher : IDisposable
{
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint PageExecuteReadWrite = 0x40;

    public static readonly PatchSite[] Table =
    {
        new()
        {
            Id = PatchId.KillAtlasFog,
            Kind = PatchKind.Nop,
            Signature = "83 B8 ?? ?? ?? ?? 23 75 08 48 8B CB E8 ?? ?? ?? ??",
            SigOffset = 12,
            Length = 5,
            ReferenceAddress = 0x140B99891L,
            Note = "call that builds the fog material, in WorldMapEndgamePage_FrameMove",
        },

        new()
        {
            Id = PatchId.ForceAtlasFog,
            Kind = PatchKind.Replace,
            Signature = "C7 45 60 FF FF 7F FF 49 8B 5E 50 E8 ?? ?? ?? ?? 48 8B 48 08 48 8B 83 ?? ?? ?? ??",
            SigOffset = 23,
            Length = 4,
            From = new byte[] { 0x18, 0x1C, 0x00, 0x00 },
            To = new byte[] { 0x20, 0x1C, 0x00, 0x00 },
            ReferenceAddress = 0x140B9A836L,
            Note = "reveal-circle branch of WorldMapFowShape_SetupMaterial, repointed from the "
                 + "WorldMap FoW Reveal C object at page+0x1C18 to WorldMap FoW Hide Sq at page+0x1C20, "
                 + "so every reveal shape paints fog instead of clearing it",
        },

        new()
        {
            Id = PatchId.UnlockAtlasZoomOut,
            Kind = PatchKind.RetargetFloat,
            Signature = "F3 0F 5C C8 F3 0F 11 4C 24 ?? 75 ?? F3 0F 10 05 ?? ?? ?? ?? 48 8D 54 24 ?? 0F 2F C8",
            SigOffset = 12,
            Length = 8,
            Slot = 0,
            ReferenceAddress = 0x140BA9467L,
            Note = "wheel handler floor, the one that actually binds",
        },
        new()
        {
            Id = PatchId.UnlockAtlasZoomOut,
            Kind = PatchKind.RetargetFloat,
            Signature = "48 8B 4A 08 0F 28 C8 80 B9 ?? ?? ?? ?? 0A 75 ?? F3 0F 10 05 ?? ?? ?? ??",
            SigOffset = 16,
            Length = 8,
            Slot = 1,
            ReferenceAddress = 0x140BEE321L,
            Note = "ZoomStepCmd floor, a cmov not a maxss, so NOPing it would pin the scale instead",
        },
        new()
        {
            Id = PatchId.UnlockAtlasZoomOut,
            Kind = PatchKind.Nop,
            Signature = "0F 28 C8 F3 0F 5F F1 F3 0F 5D F0 48 89 AC 24",
            SigOffset = 3,
            Length = 4,
            ReferenceAddress = 0x140BB374CL,
            Note = "ZoomAtCursor maxss, redundant re-clamp",
        },
        new()
        {
            Id = PatchId.UnlockAtlasZoomOut,
            Kind = PatchKind.Nop,
            Signature = "0F 28 C8 F3 0F 5F F1 F3 0F 5D F0 0F 28 DE",
            SigOffset = 3,
            Length = 4,
            ReferenceAddress = 0x140BA9E55L,
            Note = "TravelToNode maxss, redundant re-clamp",
        },

        new()
        {
            Id = PatchId.UnlockAtlasZoomIn,
            Kind = PatchKind.RetargetFloat,
            Signature = "F3 0F 10 15 ?? ?? ?? ?? F3 0F 10 0A 41 B1 01 4D 8B 43 ?? F3 0F 5D CA",
            SigOffset = 0,
            Length = 8,
            Slot = 2,
            ReferenceAddress = 0x140BA94D2L,
            Note = "wheel handler ceiling load, retargeted so the clamp stays and we own its value",
        },
        new()
        {
            Id = PatchId.UnlockAtlasZoomIn,
            Kind = PatchKind.Nop,
            Signature = "0F 28 C8 F3 0F 5F F1 F3 0F 5D F0 48 89 AC 24",
            SigOffset = 7,
            Length = 4,
            ReferenceAddress = 0x140BB3750L,
            Note = "ZoomAtCursor minss, redundant re-clamp",
        },
        new()
        {
            Id = PatchId.UnlockAtlasZoomIn,
            Kind = PatchKind.Nop,
            Signature = "0F 28 C8 F3 0F 5F F1 F3 0F 5D F0 0F 28 DE",
            SigOffset = 7,
            Length = 4,
            ReferenceAddress = 0x140BA9E59L,
            Note = "TravelToNode minss, redundant re-clamp",
        },
        new()
        {
            Id = PatchId.UnlockAtlasZoomIn,
            Kind = PatchKind.Nop,
            Signature = "41 0F 2F 00 48 8D 44 24 ?? F3 0F 11 44 24 ?? 49 0F 46 C0 F3 0F 10 18 F3 0F 5D D9",
            SigOffset = 23,
            Length = 4,
            ReferenceAddress = 0x140BEE367L,
            Note = "ZoomStepCmd ceiling",
        },

        new()
        {
            Id = PatchId.BlockCrashLog,
            Kind = PatchKind.RetAtEntry,
            Anchor = "PoE-%ld-%ld.dmp",
            Length = 15,
            Guard = new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x18, 0x44, 0x88, 0x4C, 0x24, 0x20, 0x48, 0x89, 0x54, 0x24, 0x10 },
            ReferenceAddress = 0x142125240L,
            Note = "crash reporter, found from the dump filename literal. blocks the lot: the "
                 + "account-tagged .dmp.txt, the session log copy, the .poecap packet capture, the "
                 + "minidump, the Config.ini upload, and the reporter process it would spawn",
        },
    };

    public const float MaxZoomOutFactor = 3.4f;
    public const float MaxZoomInFactor = 10f;

    private const int ScratchSlots = 4;
    private const uint MemCommitReserve = 0x3000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;

    private readonly Dictionary<PatchId, bool> _lastAttempt = new();
    private IntPtr _handle = IntPtr.Zero;
    private IntPtr _scratch = IntPtr.Zero;

    private bool EnsureScratch(long moduleBase)
    {
        if (_scratch != IntPtr.Zero) return true;

        for (long delta = 0x10000; delta < 0x40000000L; delta += 0x10000)
        {
            var up = VirtualAllocEx(_handle, new IntPtr(moduleBase + delta), new UIntPtr(4096), MemCommitReserve, PageReadWrite);
            if (up != IntPtr.Zero) { _scratch = up; return true; }

            var down = VirtualAllocEx(_handle, new IntPtr(moduleBase - delta), new UIntPtr(4096), MemCommitReserve, PageReadWrite);
            if (down != IntPtr.Zero) { _scratch = down; return true; }
        }

        LastError = "could not reserve a scratch page within rip relative reach of the client image";
        return false;
    }

    private long SlotAddress(PatchSite site) => _scratch.ToInt64() + site.Slot * 4;

    private static bool IsSlotSite(PatchSite site) =>
        site.Kind == PatchKind.RetargetFloat && site.Usable && site.Slot >= 0;

    public bool SetZoomLimits(float zoomOutFactor, float zoomInFactor)
    {
        if (!Attached || _scratch == IntPtr.Zero) return false;

        var outF = Math.Clamp(zoomOutFactor, 1f, MaxZoomOutFactor);
        var inF = Math.Clamp(zoomInFactor, 1f, MaxZoomInFactor);

        var ok = true;
        foreach (var site in Table)
        {
            if (!IsSlotSite(site)) continue;
            var want = site.Id == PatchId.UnlockAtlasZoomIn ? site.Stock * inF : site.Stock / outF;
            ok &= WriteFloats(SlotAddress(site), want);
        }

        return ok;
    }

    private void SeedScratch()
    {
        foreach (var site in Table)
            if (IsSlotSite(site)) WriteFloats(SlotAddress(site), site.Stock);
    }

    public bool Attached { get; private set; }
    public string LastError { get; private set; }
    public string LastNote { get; private set; }

    public bool Attach(int processId, long moduleBase)
    {
        Detach();

        if (moduleBase == 0)
        {
            LastError = "could not resolve the client module base";
            return false;
        }

        _handle = OpenProcess(ProcessVmOperation | ProcessVmRead | ProcessVmWrite, false, processId);
        if (_handle == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            LastError = err == 5
                ? "access denied opening the client (win32 5). run ExileCore2 as administrator."
                : "OpenProcess failed: " + new Win32Exception(err).Message;
            return false;
        }

        Attached = true;

        foreach (var site in Table)
        {
            site.Usable = false;
            site.IsPatched = false;
            site.AppliedByUs = false;
            site.Preexisting = false;
            site.Address = IntPtr.Zero;
            site.Original = null;
            site.Patched = null;
        }

        var image = ClientImage.Load(ReadForResolver, moduleBase, out var loadWhy);
        if (image == null)
        {
            LastError = "could not read the client image: " + loadWhy;
            return false;
        }

        LastNote = null;
        var adopted = 0;
        var problems = new List<string>();
        foreach (var site in Table)
        {
            var why = Prepare(site, image, moduleBase);
            if (why != null) { problems.Add($"{site.Id} ({site.Note}): {why}"); continue; }

            var live = Read(site.Address, site.Length);
            if (live == null) { problems.Add($"{site.Id}: could not read the site"); continue; }

            if (Same(live, site.Original)) site.Usable = true;
            else if (Same(live, site.Patched)) { site.Usable = true; site.IsPatched = true; }
            else problems.Add($"{site.Id}: site holds unexpected bytes {Hex(live)}");

            if (site.Usable && site.Preexisting) adopted++;
        }

        if (adopted > 0)
            LastNote = $"{adopted} site(s) were already patched when we attached, most likely by another "
                     + "plugin running the same table, so they were adopted rather than rewritten. We will "
                     + "not revert them on the way out, and turning them off here may need a client restart.";

        SeedScratch();

        if (problems.Count > 0)
        {
            LastError = $"{problems.Count} of {Table.Length} sites unusable. " + string.Join(" | ", problems);
            return false;
        }

        LastError = null;
        return true;
    }

    private string Prepare(PatchSite site, ClientImage image, long moduleBase)
    {
        long rva;
        string why;

        if (site.Signature != null)
        {
            rva = image.ResolveSignature(site.Signature, out why);

            if (rva == 0 && site.Kind == PatchKind.Nop)
            {
                var already = image.ResolveSignature(site.Signature, site.SigOffset, site.Length, out _);
                if (already != 0) { rva = already; site.Preexisting = true; why = null; }
            }

            if (rva == 0) return why;
            rva += site.SigOffset;
        }
        else
        {
            rva = image.ResolveAnchor(site.Anchor, out why);
            if (rva == 0) return why;
        }

        site.Address = new IntPtr(moduleBase + rva);

        var original = Read(site.Address, site.Length);
        if (original == null) return "could not read the site";

        switch (site.Kind)
        {
            case PatchKind.Nop:
                site.Patched = new byte[site.Length];
                for (var i = 0; i < site.Length; i++) site.Patched[i] = 0x90;

                if (!site.Preexisting) { site.Original = original; return null; }

                if (!Same(original, site.Patched))
                    return $"only matched with the patch window masked, but the bytes there are {Hex(original)}";

                site.Original = PatternWindow(site);
                return null;

            case PatchKind.RetargetFloat:
            {
                if (site.Length != 8) return "a float retarget site must be 8 bytes";
                if (original[0] != 0xF3 || original[1] != 0x0F || original[2] != 0x10 || (original[3] & 0xC7) != 0x05)
                    return $"expected movss xmm,[rip+d32], got {Hex(original)}";
                if (site.Slot < 0 || site.Slot >= ScratchSlots) return "float retarget site has no scratch slot";
                if (!EnsureScratch(moduleBase)) return LastError;

                var next = moduleBase + rva + 8;
                var stock = Read(new IntPtr(next + BitConverter.ToInt32(original, 4)), 4);
                if (stock == null) return "could not read the stock constant";
                site.Stock = BitConverter.ToSingle(stock, 0);

                var disp = _scratch.ToInt64() + site.Slot * 4 - next;
                if (disp > int.MaxValue || disp < int.MinValue) return "scratch page is out of rip relative range";

                site.Original = original;
                site.Patched = new byte[8];
                Buffer.BlockCopy(original, 0, site.Patched, 0, 4);
                Buffer.BlockCopy(BitConverter.GetBytes((int)disp), 0, site.Patched, 4, 4);
                return null;
            }

            case PatchKind.Replace:
            {
                if (site.From == null || site.To == null
                    || site.From.Length != site.Length || site.To.Length != site.Length)
                    return "a replace site needs From and To of exactly Length bytes";

                site.Original = (byte[])site.From.Clone();
                site.Patched = (byte[])site.To.Clone();
                return null;
            }

            case PatchKind.RetAtEntry:
            {
                var restored = Same(original, site.Guard);
                var alreadyPatched = original[0] == 0xC3 && TailMatches(original, site.Guard);
                if (!restored && !alreadyPatched) return $"prologue mismatch, got {Hex(original)}";

                site.Original = (byte[])site.Guard.Clone();
                site.Patched = (byte[])site.Guard.Clone();
                site.Patched[0] = 0xC3;
                return null;
            }
        }

        return "unknown patch kind";
    }

    private static byte[] PatternWindow(PatchSite site)
    {
        var toks = site.Signature.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[site.Length];
        for (var i = 0; i < site.Length; i++)
        {
            var t = site.SigOffset + i;
            if (t >= toks.Length || toks[t] == "??") return null;
            bytes[i] = Convert.ToByte(toks[t], 16);
        }

        return bytes;
    }

    private static bool TailMatches(byte[] live, byte[] guard)
    {
        for (var i = 1; i < guard.Length; i++)
            if (live[i] != guard[i]) return false;
        return true;
    }

    private static string Hex(byte[] b) => b == null ? "<null>" : BitConverter.ToString(b).Replace('-', ' ');

    public bool Set(PatchId id, bool enabled)
    {
        if (!Attached) return false;

        if (_lastAttempt.TryGetValue(id, out var prev) && prev == enabled) return true;
        _lastAttempt[id] = enabled;

        var ok = true;
        foreach (var site in Table)
        {
            if (site.Id != id || !site.Usable) continue;
            ok &= WriteSite(site, enabled);
        }

        return ok;
    }

    public void RestoreAll()
    {
        if (!Attached) return;

        foreach (var site in Table)
        {
            if (site.Usable && site.IsPatched && site.AppliedByUs)
                WriteSite(site, false);
        }

        _lastAttempt.Clear();
    }

    public bool AnyPatched()
    {
        foreach (var site in Table)
            if (site.IsPatched) return true;
        return false;
    }

    public void Verify(PatchId id)
    {
        if (!Attached) return;

        foreach (var site in Table)
        {
            if (site.Id != id || site.Original == null) continue;

            var live = Read(site.Address, site.Original.Length);
            if (live == null) continue;

            bool patched;
            if (Same(live, site.Patched)) patched = true;
            else if (Same(live, site.Original)) patched = false;
            else { site.Usable = false; continue; }

            if (patched == site.IsPatched) continue;

            site.IsPatched = patched;
            site.AppliedByUs = false;
            _lastAttempt.Remove(id);
        }
    }

    public bool IsApplied(PatchId id)
    {
        var any = false;
        foreach (var site in Table)
        {
            if (site.Id != id) continue;
            if (!site.IsPatched) return false;
            any = true;
        }

        return any;
    }

    public float[] ReadFloats(long address, int count)
    {
        var raw = Read(new IntPtr(address), count * 4);
        if (raw == null) return null;

        var vals = new float[count];
        for (var i = 0; i < count; i++) vals[i] = BitConverter.ToSingle(raw, i * 4);
        return vals;
    }

    public bool WriteFloats(long address, params float[] values)
    {
        if (!Attached) return false;

        var raw = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
            Buffer.BlockCopy(BitConverter.GetBytes(values[i]), 0, raw, i * 4, 4);

        if (WriteProcessMemory(_handle, new IntPtr(address), raw, raw.Length, out var wrote)
            && wrote.ToInt64() == raw.Length)
            return true;

        LastError = $"data write at {address:X} failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
        return false;
    }

    private static readonly (PatchId Id, string Label)[] StatusLabels =
    {
        (PatchId.BlockCrashLog,      "Crash report"),
        (PatchId.KillAtlasFog,       "Atlas fog"),
        (PatchId.ForceAtlasFog,      "Atlas full fog"),
        (PatchId.UnlockAtlasZoomOut, "Atlas zoom out"),
        (PatchId.UnlockAtlasZoomIn,  "Atlas zoom in"),
    };

    public IEnumerable<(string Label, PatchState State)> Status()
    {
        foreach (var (id, label) in StatusLabels)
            yield return (label, StateOf(id));
    }

    private PatchState StateOf(PatchId id)
    {
        var found = false;
        var allPatched = true;
        foreach (var site in Table)
        {
            if (site.Id != id) continue;
            found = true;
            if (!site.Usable) return PatchState.Failed;
            if (!site.IsPatched) allPatched = false;
        }

        if (!found) return PatchState.Failed;
        return allPatched ? PatchState.Patched : PatchState.Off;
    }

    public void Dispose()
    {
        RestoreAll();
        Detach();
    }

    private void Detach()
    {
        if (_handle != IntPtr.Zero)
        {
            if (_scratch != IntPtr.Zero)
            {
                var stillPointed = false;
                foreach (var site in Table)
                    if (site.Kind == PatchKind.RetargetFloat && site.IsPatched) stillPointed = true;

                if (!stillPointed) VirtualFreeEx(_handle, _scratch, UIntPtr.Zero, MemRelease);
                _scratch = IntPtr.Zero;
            }

            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }

        Attached = false;
        _lastAttempt.Clear();
    }

    private bool WriteSite(PatchSite site, bool enabled)
    {
        var want = enabled ? site.Patched : site.Original;
        var from = enabled ? site.Original : site.Patched;

        if (want == null)
        {
            LastError = $"cannot restore {site.Id}, it was already patched when we attached and the "
                      + "original bytes are not recoverable from the signature. restart the client.";
            return false;
        }

        var live = Read(site.Address, want.Length);
        if (live == null) return false;

        if (Same(live, want))
        {
            site.IsPatched = enabled;
            return true;
        }

        if (!Same(live, from))
        {
            site.Usable = false;
            LastError = $"refusing to write {site.Id} @ {site.Address.ToInt64():X}, the bytes there are not what we expect";
            return false;
        }

        if (!Write(site.Address, want)) return false;

        site.IsPatched = enabled;
        site.AppliedByUs = enabled;
        return true;
    }

    private byte[] ReadForResolver(long address, int size)
    {
        var buffer = new byte[size];
        return ReadProcessMemory(_handle, new IntPtr(address), buffer, size, out var got)
               && got.ToInt64() == size
            ? buffer
            : null;
    }

    private byte[] Read(IntPtr address, int size)
    {
        var buffer = new byte[size];
        if (ReadProcessMemory(_handle, address, buffer, size, out var read) && read.ToInt64() == size)
            return buffer;

        LastError = $"read at {address.ToInt64():X} failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
        return null;
    }

    private bool Write(IntPtr address, byte[] bytes)
    {
        var size = new UIntPtr((uint)bytes.Length);
        if (!VirtualProtectEx(_handle, address, size, PageExecuteReadWrite, out var old))
        {
            LastError = $"VirtualProtectEx at {address.ToInt64():X} failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        var wrote = WriteProcessMemory(_handle, address, bytes, bytes.Length, out var written)
                    && written.ToInt64() == bytes.Length;
        var err = wrote ? 0 : Marshal.GetLastWin32Error();

        VirtualProtectEx(_handle, address, size, old, out _);
        FlushInstructionCache(_handle, address, size);

        if (!wrote)
        {
            LastError = err == 5
                ? "write denied (win32 5). run ExileCore2 as administrator."
                : $"write at {address.ToInt64():X} failed: " + new Win32Exception(err).Message;
        }

        return wrote;
    }

    private static bool Same(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr bytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtectEx(IntPtr process, IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint freeType);
}

internal sealed class ClientImage
{
    private readonly Func<long, int, byte[]> _read;
    private readonly long _base;
    private readonly List<(uint Rva, int Size, bool Exec, bool Write)> _sections = new();
    private readonly Dictionary<uint, byte[]> _cache = new();

    private uint _textRva;
    private int _textSize;
    private uint _pdataRva;
    private int _pdataSize;

    private ClientImage(Func<long, int, byte[]> read, long moduleBase)
    {
        _read = read;
        _base = moduleBase;
    }

    public static ClientImage Load(Func<long, int, byte[]> read, long moduleBase, out string why)
    {
        why = null;
        var img = new ClientImage(read, moduleBase);

        var hdr = read(moduleBase, 0x1000);
        if (hdr == null || hdr.Length < 0x1000) { why = "could not read the PE headers"; return null; }

        var peOff = BitConverter.ToInt32(hdr, 0x3C);
        if (peOff <= 0 || peOff > 0xF00 || BitConverter.ToUInt32(hdr, peOff) != 0x00004550)
        { why = "bad PE signature"; return null; }

        var coff = peOff + 4;
        int nsec = BitConverter.ToUInt16(hdr, coff + 2);
        int optsz = BitConverter.ToUInt16(hdr, coff + 16);
        var opt = coff + 20;
        if (BitConverter.ToUInt16(hdr, opt) != 0x20B) { why = "not a PE32+ image"; return null; }

        var excDir = opt + 112 + 3 * 8;
        img._pdataRva = BitConverter.ToUInt32(hdr, excDir);
        img._pdataSize = (int)BitConverter.ToUInt32(hdr, excDir + 4);
        if (img._pdataRva == 0 || img._pdataSize < 12) { why = "no exception directory"; return null; }

        var sh = opt + optsz;
        for (var i = 0; i < nsec; i++)
        {
            var o = sh + i * 40;
            if (o + 40 > hdr.Length) break;
            var vsize = (int)BitConverter.ToUInt32(hdr, o + 8);
            var vaddr = BitConverter.ToUInt32(hdr, o + 12);
            var chars = BitConverter.ToUInt32(hdr, o + 36);
            if (vsize <= 0) continue;
            var exec = (chars & 0x20000000) != 0;
            var write = (chars & 0x80000000) != 0;
            img._sections.Add((vaddr, vsize, exec, write));
            if (exec && img._textSize == 0) { img._textRva = vaddr; img._textSize = vsize; }
        }

        if (img._textSize == 0) { why = "no executable section"; return null; }
        if (img.Section(img._textRva) == null) { why = "could not read the code section"; return null; }
        return img;
    }

    public long ResolveSignature(string pattern, out string why) => ResolveSignature(pattern, -1, 0, out why);

    public long ResolveSignature(string pattern, int maskFrom, int maskLen, out string why)
    {
        why = null;
        var toks = pattern.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var need = new int[toks.Length];
        for (var i = 0; i < toks.Length; i++)
            need[i] = toks[i] == "??" ? -1 : Convert.ToInt32(toks[i], 16);

        for (var i = maskFrom; maskFrom >= 0 && i < maskFrom + maskLen && i < need.Length; i++)
            need[i] = -1;

        if (need.Length == 0 || need[0] < 0) { why = "pattern must start with a fixed byte"; return 0; }

        var code = Section(_textRva);
        if (code == null) { why = "could not read the code section"; return 0; }

        long hit = 0;
        var hits = 0;
        var first = (byte)need[0];
        for (var i = 0; i + need.Length <= code.Length; i++)
        {
            if (code[i] != first) continue;
            var ok = true;
            for (var j = 1; j < need.Length; j++)
                if (need[j] >= 0 && code[i + j] != need[j]) { ok = false; break; }
            if (!ok) continue;
            if (++hits > 1) { why = $"pattern matched {hits}+ times, wanted exactly 1"; return 0; }
            hit = _textRva + i;
        }

        if (hits != 1) { why = "pattern not found"; return 0; }
        return hit;
    }

    public long ResolveAnchor(string anchor, out string why)
    {
        why = null;

        var needle = Encoding.Unicode.GetBytes(anchor);
        var strRvas = new List<long>();
        foreach (var s in _sections)
        {
            if (s.Exec) continue;
            var buf = Section(s.Rva);
            if (buf == null) continue;
            for (var at = IndexOf(buf, needle, 0); at >= 0; at = IndexOf(buf, needle, at + 1))
                strRvas.Add(s.Rva + at);
        }
        if (strRvas.Count == 0) { why = $"anchor \"{anchor}\" not found"; return 0; }

        var code = Section(_textRva);
        if (code == null) { why = "could not read the code section"; return 0; }

        long leaRva = 0;
        var leaHits = 0;
        for (var k = 0; k + 7 <= code.Length; k++)
        {
            if (code[k] != 0x48 && code[k] != 0x4C) continue;
            if (code[k + 1] != 0x8D) continue;
            if ((code[k + 2] & 0xC7) != 0x05) continue;
            var disp = BitConverter.ToInt32(code, k + 3);
            if (!strRvas.Contains(_textRva + k + 7 + disp)) continue;
            leaHits++;
            leaRva = _textRva + k;
        }
        if (leaHits != 1)
        {
            why = $"anchor \"{anchor}\" referenced by {leaHits} lea sites, wanted exactly 1";
            return 0;
        }

        var pdata = Section(_pdataRva);
        if (pdata == null) { why = "could not read .pdata"; return 0; }

        var lo = 0;
        var hi = _pdataSize / 12 - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var begin = BitConverter.ToUInt32(pdata, mid * 12);
            var end = BitConverter.ToUInt32(pdata, mid * 12 + 4);
            if (leaRva < begin) hi = mid - 1;
            else if (leaRva >= end) lo = mid + 1;
            else return begin;
        }

        why = $"no .pdata entry contains the lea at {leaRva:X}";
        return 0;
    }

    private byte[] Section(uint rva)
    {
        if (_cache.TryGetValue(rva, out var got)) return got;

        var size = 0;
        foreach (var s in _sections)
            if (s.Rva == rva) { size = s.Size; break; }
        if (rva == _pdataRva && size == 0) size = _pdataSize;
        if (size == 0) return null;

        var buf = ReadBig(_base + rva, size);
        _cache[rva] = buf;
        return buf;
    }

    private byte[] ReadBig(long addr, int size)
    {
        const int chunk = 4 << 20;
        var buf = new byte[size];
        for (var o = 0; o < size; o += chunk)
        {
            var n = Math.Min(chunk, size - o);
            var part = _read(addr + o, n);
            if (part == null || part.Length < n) return null;
            Buffer.BlockCopy(part, 0, buf, o, n);
        }
        return buf;
    }

    private static int IndexOf(byte[] hay, byte[] needle, int from)
    {
        var last = hay.Length - needle.Length;
        for (var i = from; i <= last; i++)
        {
            var j = 0;
            while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }
}
