using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ImGuiNET;
using Newtonsoft.Json;

namespace ExileImGui2;

/// <summary>
/// One tickable group in <see cref="Profiles{T}"/>'s "Copy from..." popup. Declare these once, next
/// to the profile type, and hand the array to <see cref="Profiles{T}.Bar"/>.
/// </summary>
/// <remarks>
/// <paramref name="apply"/> gets (from, into): <c>from</c> is already a private clone of the source
/// profile, so assigning its nested objects straight across is safe - nothing ends up aliased.
/// </remarks>
public sealed class CopyGroup<T>
{
    public readonly string Label;
    public readonly Action<T, T> Apply;

    /// <summary>Whether the box starts ticked. Off for a slice most copies shouldn't drag along.</summary>
    public readonly bool DefaultOn;

    public CopyGroup(string label, Action<T, T> apply, bool defaultOn = true)
    {
        Label = label;
        Apply = apply;
        DefaultOn = defaultOn;
    }
}

/// <summary>
/// Named, user-switchable presets of a settings type, serialized inside the plugin's own settings
/// JSON. Put every tunable the user should be able to swap into <typeparamref name="T"/> and read it
/// through <see cref="Current"/> - the live config IS the active profile, so switching is a pointer
/// move, not a copy.
/// </summary>
/// <remarks>
/// <para>
/// That's the whole design. There is no capture step and no apply step, so live edits can't be lost
/// on a switch and there's no "overlay, don't clear" trap to get wrong. The price is that anything
/// the profile owns has to live in <typeparamref name="T"/>. Game-scraped definitions (every map,
/// every mod) stay OUT - keep those in a separate dictionary and let the profile hold only the user's
/// tuning for them, keyed the same way.
/// </para>
/// <para>
/// Wire-up is two lines in your settings class plus an optional recache hook:
/// <code>
/// public Profiles&lt;MapProfile&gt; Profiles = new();          // serializes itself
/// Settings.Profiles.OnSwitch = _ =&gt; RebuildCaches();      // once, at load
/// </code>
/// Then read <c>Settings.Profiles.Current.Whatever</c> everywhere instead of <c>Settings.Whatever</c>.
/// </para>
/// <para>
/// If <typeparamref name="T"/> holds color fields, set <see cref="Json"/> to serializer settings
/// carrying <c>EColorConverter</c> - the same one the fields themselves need, or
/// <see cref="Duplicate"/> and "Copy from..." round-trip the color to nothing.
/// </para>
/// </remarks>
public sealed class Profiles<T> where T : class, new()
{
    public const string DefaultName = "Default";

    // both serialize: they ride inside your ISettings JSON, no separate file. the wire names are
    // pinned and are NOT the C# names - "ActiveProfile" and "Profiles" are what the hand-rolled
    // stores this replaces already wrote, so an existing plugin can swap the type in without
    // orphaning anyone's saved profiles. don't rename them.
    [JsonProperty("ActiveProfile")] public string Active { get; set; } = DefaultName;
    [JsonProperty("Profiles")] public Dictionary<string, T> Items { get; set; } = new() { { DefaultName, new T() } };

    /// <summary>Fires after the active profile's values change - switch, delete-the-active, copy-into.
    /// Recache here. Not fired by a rename: the name moved, the values didn't.</summary>
    [JsonIgnore] public Action<string> OnSwitch;

    /// <summary>Optional name seed for the "New" button, e.g. <c>() =&gt; ServerData.League</c>.
    /// Falls back to "Profile". Wrapped in a try, so a null game state can't kill the frame.</summary>
    [JsonIgnore] public Func<string> NameStem;

    /// <summary>Serializer settings used for the deep clone behind Duplicate and "Copy from...".
    /// Give it your converters or nested values won't survive the round trip.</summary>
    [JsonIgnore] public JsonSerializerSettings Json;

    [JsonIgnore] private string memoName;
    [JsonIgnore] private T memo;

    // transient ui state
    [JsonIgnore] private string edit = "";
    [JsonIgnore] private bool renaming;
    [JsonIgnore] private string copySource;
    [JsonIgnore] private bool[] copyOn;

    /// <summary>The active profile. Read every setting off this. Never null - a missing or empty
    /// dictionary self-heals to a single Default.</summary>
    [JsonIgnore]
    public T Current
    {
        get
        {
            if (memo != null && memoName == Active) return memo;
            Ensure();
            memo = Items[Active];
            memoName = Active;
            return memo;
        }
    }

    /// <summary>Profile names, sorted for display.</summary>
    public IEnumerable<string> Names() =>
        Items == null ? Enumerable.Empty<string>()
                      : Items.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

    /// <summary>Makes the store valid: at least one profile, and Active pointing at one that exists.
    /// Call after deserializing a settings file you didn't write.</summary>
    public void Ensure()
    {
        Items ??= new Dictionary<string, T>();
        if (Items.Count == 0) Items[DefaultName] = new T();
        if (string.IsNullOrEmpty(Active) || !Items.ContainsKey(Active))
        {
            Active = Items.Keys.First();
            Invalidate();
        }
    }

    /// <returns>True the frame the active profile actually moves.</returns>
    public bool Switch(string name)
    {
        if (string.IsNullOrEmpty(name) || name == Active) return false;
        if (Items == null || !Items.ContainsKey(name)) return false;
        Active = name;
        Invalidate();
        OnSwitch?.Invoke(name);
        return true;
    }

    /// <summary>Adds a profile at defaults and switches to it. Nothing carries over from the current
    /// one - that's what Duplicate is for.</summary>
    /// <returns>The name it ended up with, which may be uniquified.</returns>
    public string New(string stem = null)
    {
        Ensure();
        var name = Unique(string.IsNullOrWhiteSpace(stem) ? Stem() : stem);
        Items[name] = new T();
        Switch(name);
        return name;
    }

    /// <summary>Deep-copies a profile (the active one by default) and switches to the copy.</summary>
    /// <returns>The new name, or null if the source doesn't exist.</returns>
    public string Duplicate(string from = null)
    {
        Ensure();
        from ??= Active;
        if (!Items.TryGetValue(from, out var src)) return null;
        var name = Unique(from + " Copy");
        Items[name] = Clone(src);
        Switch(name);
        return name;
    }

    /// <returns>False when the new name is blank, unchanged, or already taken.</returns>
    public bool Rename(string from, string to)
    {
        to = to?.Trim();
        if (string.IsNullOrEmpty(to) || to == from || Items == null) return false;
        if (!Items.ContainsKey(from) || Items.ContainsKey(to)) return false;
        Items[to] = Items[from];
        Items.Remove(from);
        if (Active == from) Active = to;
        Invalidate();
        return true;
    }

    /// <summary>Deletes a profile. The last one is never deletable.</summary>
    /// <remarks>Deliberately does not route through Switch - the seam-free design means nothing
    /// needs banking first, and going via Switch on a name we just removed would only misfire.</remarks>
    public bool Delete(string name)
    {
        if (Items == null || Items.Count <= 1 || !Items.ContainsKey(name)) return false;
        Items.Remove(name);
        Invalidate();
        if (Active == name)
        {
            Active = Items.Keys.First();
            OnSwitch?.Invoke(Active);
        }
        return true;
    }

    /// <summary>
    /// Pulls values out of another profile into the active one, keeping the active one's name.
    /// </summary>
    /// <param name="groups">Which slices to take. Null or empty replaces the whole profile.</param>
    /// <param name="enabled">Parallel to <paramref name="groups"/>; a missing or short array means
    /// every group is on.</param>
    public bool CopyFrom(string source, IReadOnlyList<CopyGroup<T>> groups = null,
        IReadOnlyList<bool> enabled = null)
    {
        Ensure();
        if (string.IsNullOrEmpty(source) || source == Active) return false;
        if (!Items.TryGetValue(source, out var src)) return false;

        var clone = Clone(src);   // clone first, so the two profiles never share nested references
        if (groups == null || groups.Count == 0)
        {
            Items[Active] = clone;
        }
        else
        {
            var into = Current;
            for (int i = 0; i < groups.Count; i++)
            {
                if (enabled != null && i < enabled.Count && !enabled[i]) continue;
                groups[i]?.Apply?.Invoke(clone, into);
            }
        }
        Invalidate();
        OnSwitch?.Invoke(Active);
        return true;
    }

    /// <summary>JSON round-trip deep copy, so nested reference types don't alias the original.</summary>
    public T Clone(T value) =>
        value == null ? new T()
                      : JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value, Json), Json) ?? new T();

    /// <summary><paramref name="stem"/> if it's free, else "stem 2", "stem 3"...</summary>
    public string Unique(string stem)
    {
        stem = string.IsNullOrWhiteSpace(stem) ? "Profile" : stem.Trim();
        if (Items == null || !Items.ContainsKey(stem)) return stem;
        for (int i = 2; ; i++)
            if (!Items.ContainsKey(stem + " " + i)) return stem + " " + i;
    }

    private void Invalidate()
    {
        memoName = null;
        memo = null;
    }

    private string Stem()
    {
        string s = null;
        try { s = NameStem?.Invoke(); } catch { /* hook reaches game state, don't let it kill the frame */ }
        return string.IsNullOrWhiteSpace(s) ? "Profile" : s;
    }

    /// <summary>
    /// The whole toolbar: picker, New, Duplicate, Copy from..., Rename, Delete, with the delete
    /// confirm and the inline rename box. Drop it at the top of your settings page.
    /// </summary>
    /// <param name="copyGroups">Slices offered in the copy popup. Null still gets you a
    /// whole-profile copy.</param>
    /// <returns>True the frame anything changes, so the page can roll up to one dirty bit.</returns>
    public bool Bar(string id = "profiles", float width = 220f, IReadOnlyList<CopyGroup<T>> copyGroups = null)
    {
        Ensure();
        bool changed = false;
        ImGui.PushID(id);

        ImGui.SetNextItemWidth(width);
        if (ImGui.BeginCombo("##pick", Active))
        {
            foreach (var name in Names().ToList())
            {
                bool sel = name == Active;
                if (ImGui.Selectable(name, sel)) changed |= Switch(name);
                if (sel) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button("New")) { New(); changed = true; }

        ImGui.SameLine();
        if (ImGui.Button("Duplicate")) { Duplicate(); changed = true; }

        if (Items.Count > 1)
        {
            ImGui.SameLine();
            if (ImGui.Button("Copy from...")) { copySource = null; ImGui.OpenPopup("copyfrom"); }
        }
        changed |= CopyPopup(copyGroups);

        ImGui.SameLine();
        if (ImGui.Button("Rename")) { renaming = true; edit = Active; }

        if (Items.Count > 1)
        {
            ImGui.SameLine();
            if (ImGui.Button("Delete")) ImGui.OpenPopup("del");
        }
        changed |= DeletePopup();

        if (renaming) changed |= RenameRow();

        ImGui.PopID();
        return changed;
    }

    private bool CopyPopup(IReadOnlyList<CopyGroup<T>> groups)
    {
        if (!ImGui.BeginPopup("copyfrom")) return false;
        bool copied = false;

        // TextUnformatted, not Text - profile names are user typed and must never hit a printf path
        ImGui.TextUnformatted("Copy into \"" + Active + "\" from:");
        ImGui.Separator();

        foreach (var name in Names().ToList())
        {
            if (name == Active) continue;
            if (ImGui.Selectable(name, name == copySource)) copySource = name;
        }

        if (groups != null && groups.Count > 0)
        {
            ImGui.Separator();
            if (copyOn == null || copyOn.Length != groups.Count)
            {
                copyOn = new bool[groups.Count];
                for (int i = 0; i < copyOn.Length; i++) copyOn[i] = groups[i]?.DefaultOn ?? true;
            }
            for (int i = 0; i < groups.Count; i++)
                ImGui.Checkbox(groups[i]?.Label ?? "?", ref copyOn[i]);
        }

        ImGui.Separator();
        ImGui.BeginDisabled(copySource == null);
        if (ImGui.Button("Copy"))
        {
            copied = CopyFrom(copySource, groups, copyOn);
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
        return copied;
    }

    private bool DeletePopup()
    {
        if (!ImGui.BeginPopup("del")) return false;
        bool done = false;
        ImGui.TextUnformatted("Delete profile \"" + Active + "\"?");
        ImGui.Separator();
        if (ImGui.Button("Delete")) { done = Delete(Active); ImGui.CloseCurrentPopup(); }
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
        return done;
    }

    private bool RenameRow()
    {
        bool done = false;
        ImGui.SetNextItemWidth(220f);
        if (ImGui.InputText("##rename", ref edit, 64, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            done = Rename(Active, edit);
            renaming = false;
        }
        ImGui.SameLine();
        if (ImGui.Button("Save##rn")) { done = Rename(Active, edit); renaming = false; }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##rn")) renaming = false;
        return done;
    }
}

/// <summary>
/// Per-key state on disk: one JSON file per key under a folder you pick, usually
/// <c>Path.Combine(ConfigDirectory, "profiles")</c>. The key is whatever the state has to follow -
/// character name, league, both - and the caller decides when it changes.
/// </summary>
/// <remarks>
/// <para>
/// Different job from <see cref="Profiles{T}"/>: that one holds settings presets the user picks, this
/// one holds state that follows the character whatever the settings say (route progress, per-character
/// notes). A plugin can run both; they never touch.
/// </para>
/// <code>
/// var store = new ProfileFiles&lt;RunState&gt;(Path.Combine(ConfigDirectory, "profiles"));
/// // each tick:
/// store.Switch(key);          // banks the outgoing state, loads the incoming one. no-op if unchanged
/// if (dirty) store.Save();
/// </code>
/// <para>
/// Every disk call swallows its exception and keeps the in-memory state - a read-only config folder
/// degrades to "changes don't persist", never to a crash mid-frame.
/// </para>
/// </remarks>
public sealed class ProfileFiles<T> where T : class, new()
{
    private readonly string dir;

    /// <summary>The key currently loaded, raw and unsanitized. Empty before the first Switch.</summary>
    public string ActiveKey { get; private set; } = "";

    /// <summary>The loaded state. Mutate it freely, call <see cref="Save"/> when it changes.</summary>
    public T State { get; private set; } = new();

    /// <summary>Fires after a switch has loaded the new state. Reset derived caches here.</summary>
    public Action<string> OnSwitch;

    /// <summary>Serializer settings for the state file. Give it your converters if the state holds
    /// types Newtonsoft can't round-trip on its own.</summary>
    public JsonSerializerSettings Json;

    public ProfileFiles(string profilesDir) => dir = profilesDir;

    public string Dir => dir;

    /// <summary>Where a key lands on disk. The name shown in your UI is the raw key; this is the
    /// sanitized filename, so show both when they differ.</summary>
    public string PathFor(string key) => Path.Combine(dir, Sanitize(key) + ".json");

    /// <summary>Saved profiles, by filename stem. Sanitized, so it won't always equal the raw key.</summary>
    public string[] List()
    {
        try
        {
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            return Directory.GetFiles(dir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Banks the outgoing state under the old key, then loads the new one. No-op if the key
    /// is blank or unchanged, so it's cheap to call every tick.</summary>
    public bool Switch(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key == ActiveKey) return false;
        Save();
        ActiveKey = key;
        Load();
        OnSwitch?.Invoke(key);
        return true;
    }

    public void Save()
    {
        if (string.IsNullOrEmpty(ActiveKey)) return;
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(PathFor(ActiveKey), JsonConvert.SerializeObject(State, Formatting.Indented, Json));
        }
        catch { /* config dir not writable, keep running on the in-memory copy */ }
    }

    /// <summary>Re-reads the active key's file. A missing or corrupt file gives fresh state, not a throw.</summary>
    public void Load()
    {
        try
        {
            var path = PathFor(ActiveKey);
            State = File.Exists(path)
                ? JsonConvert.DeserializeObject<T>(File.ReadAllText(path), Json) ?? new T()
                : new T();
        }
        catch { State = new T(); }
    }

    /// <summary>Switches to a key and writes it out, so it shows up in <see cref="List"/> right away.</summary>
    public void Create(string key)
    {
        Switch(key);
        Save();
    }

    public bool Delete(string key)
    {
        try
        {
            var p = PathFor(key);
            if (!File.Exists(p)) return false;
            File.Delete(p);
        }
        catch { return false; }
        if (key == ActiveKey) { ActiveKey = ""; State = new T(); }
        return true;
    }

    public bool Rename(string from, string to)
    {
        if (string.IsNullOrWhiteSpace(to)) return false;
        try
        {
            var src = PathFor(from);
            var dst = PathFor(to);
            if (!File.Exists(src) || File.Exists(dst)) return false;
            File.Move(src, dst);
        }
        catch { return false; }
        if (ActiveKey == from) ActiveKey = to;
        return true;
    }

    public bool Duplicate(string from, string to)
    {
        if (string.IsNullOrWhiteSpace(to)) return false;
        try
        {
            var src = PathFor(from);
            var dst = PathFor(to);
            if (!File.Exists(src) || File.Exists(dst)) return false;
            File.Copy(src, dst);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// One-time migration for a plugin that used to keep a single flat state file. Call right after
    /// the first real <see cref="Switch"/>: moves the old file in as this key's profile, but only if
    /// the key has no profile yet.
    /// </summary>
    public bool AdoptLegacy(string legacyPath)
    {
        if (string.IsNullOrEmpty(ActiveKey) || string.IsNullOrEmpty(legacyPath)) return false;
        try
        {
            var dst = PathFor(ActiveKey);
            if (File.Exists(dst) || !File.Exists(legacyPath)) return false;
            Directory.CreateDirectory(dir);
            File.Move(legacyPath, dst);
        }
        catch { return false; }
        Load();
        return true;
    }

    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Windows-safe filename, reserved device names included. Character names come from the
    /// game, so they can be anything.</summary>
    public static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "_default";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.Trim()) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        var s = sb.ToString().TrimEnd('.', ' ');
        if (s.Length > 80) s = s[..80].TrimEnd('.', ' ');
        if (s.Length == 0) return "_default";
        var stem = s;
        var dot = stem.IndexOf('.');
        if (dot >= 0) stem = stem[..dot];
        return Reserved.Contains(stem) ? "_" + s : s;
    }

    /// <summary>Stable short hash of a key, for logs. Character names are user data and ExileCore's
    /// log is shared, so mask before writing rather than after someone notices.</summary>
    public static string Mask(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "(none)";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim()));
        var sb = new StringBuilder("profile-", 14);
        for (int i = 0; i < 3; i++) sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }
}
