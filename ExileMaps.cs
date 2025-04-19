using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

using ExileCore2;
using ExileCore2.PoEMemory.Elements.AtlasElements;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Helpers;
using ExileCore2.Shared.Nodes;
using ExileCore2.Shared.Enums;

using GameOffsets2.Native;

using ImGuiNET;

using RectangleF = ExileCore2.Shared.RectangleF;
using ExileMaps.Classes;

namespace ExileMaps;

public class ExileMapsCore : BaseSettingsPlugin<ExileMapsSettings>
{
    #region Declarations
    public static ExileMapsCore Main;

    private const string defaultMapsPath = "json\\maps.json";
    private const string defaultModsPath = "json\\mods.json";
    private const string defaultBiomesPath = "json\\biomes.json";
    private const string defaultContentPath = "json\\content.json";
    private const string ArrowPath = "textures\\arrow.png";
    private const string IconsFile = "Icons.png";
    
    public IngameUIElements UI;
    public AtlasPanel AtlasPanel;

    private Vector2 screenCenter;
    private Dictionary<Vector2i, Node> mapCache = [];
    public bool refreshCache = false;
    private int cacheTicks = 0;
    private bool refreshingCache = false;
    private float maxMapWeight = 20.0f;
    private float minMapWeight = -20.0f;
    private float averageMapWeight = 0.0f;
    private float stdDevMapWeight = 0.0f;
    private readonly object mapCacheLock = new();
    private DateTime lastRefresh = DateTime.Now;
    private int TickCount { get; set; }

    private Vector2 atlasOffset;
    private Vector2 atlasDelta;

    internal IntPtr iconsId;
    internal IntPtr arrowId;

    private bool AtlasHasBeenClosed = true;
    private bool WaypointPanelIsOpen = false;
    private bool ShowMinimap = false;


    #endregion

    #region ExileCore Methods
    public override bool Initialise()
    {
        Main = this;        
        RegisterHotkeys();
        SubscribeToEvents();

        LoadDefaultBiomes();
        LoadDefaultContentTypes();
        LoadDefaultMaps();
        LoadDefaultMods();
        
        Graphics.InitImage(IconsFile);
        iconsId = Graphics.GetTextureId(IconsFile);
        Graphics.InitImage("arrow.png", Path.Combine(DirectoryFullName, ArrowPath));
        arrowId = Graphics.GetTextureId("arrow.png");

        CanUseMultiThreading = true;

        return true;
    }
    public override void AreaChange(AreaInstance area)
    {
        refreshCache = true;
    }

    public override void Tick()
    {
        UI = GameController.Game.IngameState.IngameUi;
        AtlasPanel = UI.WorldMap.AtlasPanel;

        if (!AtlasPanel.IsVisible) {
            AtlasHasBeenClosed = true;
            WaypointPanelIsOpen = false;
            return;
        }

        if (AtlasHasBeenClosed) {
            RefreshMapCache(true);
        }

        AtlasHasBeenClosed = false;

        cacheTicks++;
        if (cacheTicks % 100 != 0) 
            if (AtlasPanel.Descriptions.Count > mapCache.Count)
                refreshCache = true;
        cacheTicks = 0;
        
        screenCenter = GameController.Window.GetWindowRectangle().Center - GameController.Window.GetWindowRectangle().Location;
        
        if (refreshCache && !refreshingCache && (DateTime.Now.Subtract(lastRefresh).TotalSeconds > Settings.Graphics.MapCacheRefreshRate || mapCache.Count == 0))
        {
            var job = new Job($"{nameof(ExileMaps)}RefreshCache", () =>
            {
                RefreshMapCache();
                refreshCache = false;
            });
            job.Start();
            
        }
        
        return;
    }

    public override void Render()
    {
        CheckKeybinds();

        if (WaypointPanelIsOpen) DrawWaypointPanel();

        TickCount++;
        if (Settings.Graphics.RenderNTicks.Value % TickCount != 0) return;  

        TickCount = 0;

        if (!AtlasPanel.IsVisible) return;
        if (mapCache.Count == 0) RefreshMapCache();
        
        // Filter out nodes based on settings.
        List<Node> selectedNodes;
        lock (mapCacheLock) {
            selectedNodes = mapCache.Values.Where(x => Settings.Features.ProcessVisitedNodes || !x.IsVisited || x.IsAttempted)
                .Where(x => (Settings.Features.ProcessHiddenNodes && !x.IsVisible) || x.IsVisible || x.IsTower)            
                .Where(x => (Settings.Features.ProcessLockedNodes && !x.IsUnlocked) || x.IsUnlocked)
                .Where(x => (Settings.Features.ProcessUnlockedNodes && x.IsUnlocked) || !x.IsUnlocked)
                .Where(x => IsOnScreen(x.MapNode.Element.GetClientRect().Center)).AsParallel().ToList();
        }
        
        selectedNodes.ForEach(RenderNode);

        try {
            List<string> waypointNames = Settings.MapTypes.Maps.Where(x => x.Value.DrawLine).Select(x => x.Value.Name).ToList();
            if (Settings.Features.DrawLines && waypointNames.Count > 0) {
                List<Node> waypointNodes;

                lock (mapCacheLock) {                        
                    waypointNodes = mapCache.Values.Where(x => !x.IsVisited || x.IsAttempted)
                    .Where(x => waypointNames.Contains(x.Name))
                    .Where(x => !Settings.Features.WaypointsUseAtlasRange || Vector2.Distance(screenCenter, x.MapNode.Element.GetClientRect().Center) <= (Settings.Features.AtlasRange ?? 2000)).AsParallel().ToList();
                }
                
                waypointNodes.ForEach(DrawWaypointLine);
            }
        } catch (Exception e) {
            LogError("Error drawing waypoint lines: " + e.Message + "\n" + e.StackTrace);
        }

        try {
            foreach (var (k,waypoint) in Settings.Waypoints.Waypoints) {
                DrawWaypoint(waypoint);
                DrawWaypointArrow(waypoint);
            }
        }
        catch (Exception e) {
            LogError("Error drawing waypoints: " + e.Message + "\n" + e.StackTrace);
        }

    }
    #endregion

    #region Keybinds & Events

    ///MARK: SubscribeToEvents
    /// <summary>
    /// Subscribes to events that trigger a refresh of the map cache.
    /// </summary>
    private void SubscribeToEvents() {
        try {
            Settings.MapTypes.Maps.CollectionChanged += (_, _) => { RecalculateWeights(); };
            Settings.MapTypes.Maps.PropertyChanged += (_, _) => { RecalculateWeights(); };
            Settings.Biomes.Biomes.PropertyChanged += (_, _) => { RecalculateWeights(); };
            Settings.Biomes.Biomes.CollectionChanged += (_, _) => { RecalculateWeights(); };
            Settings.MapContent.ContentTypes.CollectionChanged += (_, _) => { RecalculateWeights(); };
            Settings.MapContent.ContentTypes.PropertyChanged += (_, _) => { RecalculateWeights(); };
            Settings.MapMods.MapModTypes.CollectionChanged += (_, _) => { RecalculateWeights(); };
            Settings.MapMods.MapModTypes.PropertyChanged += (_, _) => { RecalculateWeights(); };
        } catch (Exception e) {
            LogError("Error subscribing to events: " + e.Message);
        }
    }

    ///MARK: RegisterHotkeys
    /// <summary>
    /// Registers the hotkeys defined in the settings.
    /// </summary>
    private void RegisterHotkeys() {
        RegisterHotkey(Settings.Keybinds.RefreshMapCacheHotkey);
        RegisterHotkey(Settings.Keybinds.DebugKey);
        RegisterHotkey(Settings.Keybinds.ToggleWaypointPanelHotkey);
        RegisterHotkey(Settings.Keybinds.AddWaypointHotkey);
        RegisterHotkey(Settings.Keybinds.DeleteWaypointHotkey);
        RegisterHotkey(Settings.Keybinds.ShowTowerRangeHotkey);
        RegisterHotkey(Settings.Keybinds.UpdateMapsKey);
        RegisterHotkey(Settings.Keybinds.ToggleLockedNodesHotkey);
        RegisterHotkey(Settings.Keybinds.ToggleUnlockedNodesHotkey);
        RegisterHotkey(Settings.Keybinds.ToggleVisitedNodesHotkey);
        RegisterHotkey(Settings.Keybinds.ToggleHiddenNodesHotkey);
    }
    
    private static void RegisterHotkey(HotkeyNode hotkey)
    {
        Input.RegisterKey(hotkey);
        hotkey.OnValueChanged += () => { Input.RegisterKey(hotkey); };
    }
    private void CheckKeybinds() {
        if (!AtlasPanel.IsVisible)
            return;

        if (Settings.Keybinds.RefreshMapCacheHotkey.PressedOnce()) {  
            RefreshMapCache();
        }

        if (Settings.Keybinds.DebugKey.PressedOnce())        
            DoDebugging();

        if (Settings.Keybinds.UpdateMapsKey.PressedOnce())        
            UpdateMapData();

        if (Settings.Keybinds.ToggleWaypointPanelHotkey.PressedOnce()) {  
            WaypointPanelIsOpen = !WaypointPanelIsOpen;
        }

        if (Settings.Keybinds.AddWaypointHotkey.PressedOnce())        
            AddWaypoint(GetClosestNodeToCursor());

        if (Settings.Keybinds.DeleteWaypointHotkey.PressedOnce())        
            RemoveWaypoint(GetClosestNodeToCursor());

        if (Settings.Keybinds.ToggleLockedNodesHotkey.PressedOnce())        
            Settings.Features.ProcessLockedNodes.Value = !Settings.Features.ProcessLockedNodes.Value;
        
        if (Settings.Keybinds.ToggleUnlockedNodesHotkey.PressedOnce())        
            Settings.Features.ProcessUnlockedNodes.Value = !Settings.Features.ProcessUnlockedNodes.Value;

        if (Settings.Keybinds.ToggleVisitedNodesHotkey.PressedOnce())
            Settings.Features.ProcessVisitedNodes.Value = !Settings.Features.ProcessVisitedNodes.Value;

        if (Settings.Keybinds.ToggleHiddenNodesHotkey.PressedOnce())
            Settings.Features.ProcessHiddenNodes.Value = !Settings.Features.ProcessHiddenNodes.Value;

        if (Settings.Keybinds.ShowTowerRangeHotkey.PressedOnce()) {
            mapCache.TryGetValue(GetClosestNodeToCursor().Coordinates, out Node node);
            if (node != null) {
                mapCache.Where(x => x.Value.DrawTowers && x.Value.Address != node.Address).AsParallel().ToList().ForEach(x => x.Value.DrawTowers = false);
                node.DrawTowers = !node.DrawTowers;
            }

        }

    }
    #endregion

    #region Load Defaults
    private void LoadDefaultMods() {
        try {
            if (Settings.MapMods.MapModTypes == null)
                Settings.MapMods.MapModTypes = new ObservableDictionary<string, Mod>();

            var jsonFile = File.ReadAllText(Path.Combine(DirectoryFullName, defaultModsPath));
            var mods = JsonSerializer.Deserialize<Dictionary<string, Mod>>(jsonFile);

            foreach (var mod in mods.OrderBy(x => x.Value.Name))
                Settings.MapMods.MapModTypes.TryAdd(mod.Key, mod.Value);

            LogMessage("Loaded Mods");
        } catch (Exception e) {
            LogError("Error loading default mod: " + e.Message + "\n" + e.StackTrace);
        }
    }

    private void LoadDefaultBiomes() {
        try {
            var jsonFile = File.ReadAllText(Path.Combine(DirectoryFullName, defaultBiomesPath));
            var biomes = JsonSerializer.Deserialize<Dictionary<string, Biome>>(jsonFile);

            foreach (var biome in biomes.Where(x => x.Value.Name != "").OrderBy(x => x.Value.Name))
                Settings.Biomes.Biomes.TryAdd(biome.Key, biome.Value);  

            LogMessage("Loaded Biomes");
        } catch (Exception e) {
            LogError("Error loading default biomes: " + e.Message + "\n" + e.StackTrace);
        }
            
    }

    private void LoadDefaultContentTypes() {
        try {
            var jsonFile = File.ReadAllText(Path.Combine(DirectoryFullName, defaultContentPath));
            var contentTypes = JsonSerializer.Deserialize<Dictionary<string, Content>>(jsonFile);

            foreach (var content in contentTypes.OrderBy(x => x.Value.Name))
                Settings.MapContent.ContentTypes.TryAdd(content.Key, content.Value);   

            LogMessage("Loaded Content Types");
        } catch (Exception e) {
            LogError("Error loading default content types: " + e.Message + "\n" + e.StackTrace);
        }

    }
    
    public void LoadDefaultMaps()
    {
        try {
            var jsonFile = File.ReadAllText(Path.Combine(DirectoryFullName, defaultMapsPath));
            var maps = JsonSerializer.Deserialize<Dictionary<string, Map>>(jsonFile);

            foreach (var (key,map) in maps.OrderBy(x => x.Value.Name)) {

                // Update legacy map settings
                if(Settings.MapTypes.Maps.TryGetValue(map.Name.Replace(" ", ""), out Map existingMap) && existingMap.IDs.Length == 0) {
                    Settings.MapTypes.Maps.Remove(existingMap.Name.Replace(" ",""));
                    existingMap.ID = key;
                    existingMap.IDs = map.IDs;
                    existingMap.ShortestId = map.ShortestId;
                    Settings.MapTypes.Maps.TryAdd(key, existingMap);                
                } else {
                    // add new map
                    Settings.MapTypes.Maps.TryAdd(key, map);
                }
            }
        } catch (Exception e) {
            LogError("Error loading default maps: " + e.Message + "\n" + e.StackTrace);
        }
    }

    #endregion
    

    
    #region Map Processing
    ///MARK: RenderNode
    /// <summary>
    /// Renders a map node on the atlas panel.
    /// </summary>
    /// <param name="cachedNode"></param>
    private void RenderNode(Node cachedNode)
    {
        if (ShowMinimap) 
            return;

        if (Settings.Features.DebugMode) {
            DrawDebugging(cachedNode);
            return;
        }

        var ringCount = 0;           
        RectangleF nodeCurrentPosition = cachedNode.MapNode.Element.GetClientRect();

        try {
            ringCount += DrawContentRings(cachedNode, nodeCurrentPosition, ringCount, "Breach");
            ringCount += DrawContentRings(cachedNode, nodeCurrentPosition, ringCount, "Delirium");
            ringCount += DrawContentRings(cachedNode, nodeCurrentPosition, ringCount, "Expedition");
            ringCount += DrawContentRings(cachedNode, nodeCurrentPosition, ringCount, "Ritual");
            ringCount += DrawContentRings(cachedNode, nodeCurrentPosition, ringCount, "Map Boss");
            ringCount += DrawContentRings(cachedNode, nodeCurrentPosition, ringCount, "Cleansed");
            ringCount += DrawContentRings(cachedNode, nodeCurrentPosition, ringCount, "Corrupted");
            ringCount += DrawContentRings(cachedNode, nodeCurrentPosition, ringCount, "Corrupted Nexus");
            ringCount += DrawContentRings(cachedNode, nodeCurrentPosition, ringCount, "Irradiated");
            ringCount += DrawContentRings(cachedNode, nodeCurrentPosition, ringCount, "Unique Map");
            DrawConnections(cachedNode, nodeCurrentPosition);
            DrawMapNode(cachedNode, nodeCurrentPosition);            
            DrawTowerMods(cachedNode, nodeCurrentPosition);
            DrawMapName(cachedNode, nodeCurrentPosition);
            DrawWeight(cachedNode, nodeCurrentPosition);
            DrawTowerRange(cachedNode);

        } catch (Exception e) {
            LogError("Error drawing map node: " + e.Message + " - " + e.StackTrace);
            return;
        }
    }
    #endregion

    #region Debugging
    private void DoDebugging() {
        mapCache.TryGetValue(GetClosestNodeToCursor().Coordinates, out Node cachedNode);
        LogMessage(cachedNode.ToString());

    }

    private void DrawDebugging(Node cachedNode) {
        using (Graphics.SetTextScale(Settings.MapMods.MapModScale))
            DrawCenteredTextWithBackground(cachedNode.DebugText(), cachedNode.MapNode.Element.GetClientRect().Center, Settings.Graphics.FontColor, Settings.Graphics.BackgroundColor, true, 10, 4);

    }

    private void UpdateMapData() {
        var uniqueMapNames = AtlasPanel.Descriptions.Select(x => x.Element.Area.Name).Distinct().ToList();

        // iterate through each name and find the ID for all mapes iwth that name
        foreach (var name in uniqueMapNames) {
            var maps = AtlasPanel.Descriptions.Where(x => x.Element.Area.Name == name).ToList();
            var mapIds = maps.Select(x => x.Element.Area.Id).Distinct().ToList();
            // get shortest item from list
            var shortID = mapIds.OrderBy(x => x.Length).FirstOrDefault();
            if (Settings.MapTypes.Maps.TryGetValue(name.Replace(" ", ""), out Map mapType) || Settings.MapTypes.Maps.TryGetValue(shortID, out mapType)) {        
                Settings.MapTypes.Maps.Remove(name.Replace(" ", ""));                
                Settings.MapTypes.Maps.TryAdd(shortID, mapType);
                mapType.IDs = [.. mapIds];
                mapType.ShortestId = shortID;
                LogMessage($"Updated Map Data for {shortID}");
            } else {
                var newMap = new Map { 
                    Name = name, 
                    IDs = [.. mapIds],
                    ShortestId = shortID};
        
                Settings.MapTypes.Maps.TryAdd(shortID, newMap);        
                LogMessage($"Added Map Data for {shortID}");    
            }
        }

        var json = JsonSerializer.Serialize(Settings.MapTypes.Maps, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(DirectoryFullName, defaultMapsPath), json);

        LogMessage("Updated Map Data");
    }

    #endregion

    private Node GetClosestNodeToCursor() {
        var closestNode = AtlasPanel.Descriptions.OrderBy(x => Vector2.Distance(GameController.Game.IngameState.UIHoverElement.GetClientRect().Center, x.Element.GetClientRect().Center)).AsParallel().FirstOrDefault();

        if (mapCache.TryGetValue(closestNode.Coordinate, out Node cachedNode))
            return cachedNode;
        else
            return null;
    }

    private Node GetClosestNodeToCenterScreen() {
        var closestNode = AtlasPanel.Descriptions.OrderBy(x => Vector2.Distance(screenCenter, x.Element.GetClientRect().Center)).AsParallel().FirstOrDefault();
        if (mapCache.TryGetValue(closestNode.Coordinate, out Node cachedNode))
            return cachedNode;
        else 
            return null;
    }

    #region Map Cache
    public void RefreshMapCache(bool clearCache = false)
    {
        refreshingCache = true;

        if (clearCache)        
            lock (mapCacheLock)            
                mapCache.Clear();

        List<AtlasNodeDescription> atlasNodes = [.. AtlasPanel.Descriptions];

        // Start timer
        var timer = new Stopwatch();
        timer.Start();
        long count = 0;
        foreach (var node in atlasNodes) {
            if (mapCache.TryGetValue(node.Coordinate, out Node cachedNode))
                count += RefreshCachedMapNode(node, cachedNode);
            else
                count += CacheNewMapNode(node);

            CacheMapConnections(mapCache[node.Coordinate]);

        }
        // stop timer
        timer.Stop();
        long time = timer.ElapsedMilliseconds;
        float average = (float)time / count;
        //LogMessage($"Map cache refreshed in {time}ms, {count} nodes processed, average time per node: {average:0.00}ms");

        RecalculateWeights();
        //LogMessage($"Max Map Weight: {maxMapWeight}, Min Map Weight: {minMapWeight}");

        refreshingCache = false;
        refreshCache = false;
        lastRefresh = DateTime.Now;
    }
    
    private void RecalculateWeights() {

        if (mapCache.Count == 0)
            return;

        var mapNodes = mapCache.Values.Where(x => !x.IsVisited).Select(x => x.Weight).Distinct().ToList();
        // Get the weighted average value of the map nodes
        averageMapWeight = mapNodes.Count > 0 ? mapNodes.Average() : 0;
        // Get the standard deviation of the map nodes
        // Get the max and min map weights
        maxMapWeight = mapNodes.Count > 0 ? mapNodes.OrderByDescending(x => x).Skip(10).Max() : 0;
        minMapWeight = mapNodes.Count > 0 ? mapNodes.OrderBy(x => x).Skip(5).Min() : 0;
    }

    private int CacheNewMapNode(AtlasNodeDescription node)
    {
        string mapId = node.Element.Area.Id.Trim();
        string shortID = mapId.Replace("_NoBoss", "");
        Node newNode = new()
        {
            IsUnlocked = node.Element.IsUnlocked,
            IsVisible = node.Element.IsVisible,
            IsVisited = node.Element.IsVisited,
            IsActive = node.Element.IsActive,
            ParentAddress = node.Address,
            Address = node.Element.Address,
            Coordinates = node.Coordinate,
            Name = node.Element.Area.Name,
            Id = mapId,
            MapNode = node,
            MapType = Settings.MapTypes.Maps.TryGetValue(shortID, out Map mapType) ? mapType : Settings.MapTypes.Maps.Where(x => x.Value.MatchID(mapId)).Select(x => x.Value).FirstOrDefault() ?? new Map()
        };

        // Set Content
        if (!newNode.IsVisited) {
            // Check if the map has content
            try {
                AddNodeContentTypesFromTextures(node, newNode);

                if (node.Element.Content != null)  
                    foreach(var content in node.Element.Content.Where(x => x.Name != "").AsParallel().ToList())        
                        if (Settings.MapContent.ContentTypes.TryGetValue(content.Name, out Content newContent))
                            newNode.Content.TryAdd(newContent.Name, newContent);

            } catch (Exception e) {
                LogError($"Error getting Content for map type {node.Address.ToString("X")}: " + e.Message);
            }
            
            // Set Biomes
            try {
                var biomes = Settings.MapTypes.Maps.TryGetValue(mapId, out Map map) ? map.Biomes.Where(x => x != "").AsParallel().ToList() : [];

                foreach (var biome in biomes)                     
                    if (Settings.Biomes.Biomes.TryGetValue(biome, out Biome newBiome)) 
                        newNode.Biomes.TryAdd(newBiome.Name, newBiome);

            }   catch (Exception e) {
                LogError($"Error getting Biomes for map type {mapId}: " + e.Message);
            }
        }
    
        if (!newNode.IsVisited || newNode.IsTower) {
            try {
                foreach(var source in AtlasPanel.EffectSources.Where(x => Vector2.Distance(x.Coordinate, node.Coordinate) <= 11).AsParallel().ToList()) {
                    foreach(var effect in source.Effects.Where(x => Settings.MapMods.MapModTypes.ContainsKey(x.ModId.ToString()) && x.Value != 0).AsParallel().ToList()) {
                        var effectKey = effect.ModId.ToString();
                        var requiredContent = Settings.MapMods.MapModTypes[effectKey].RequiredContent;
                        
                        if (newNode.Effects.TryGetValue(effectKey, out Effect existingEffect)) {
                            if (!newNode.IsTower || !newNode.IsVisited)
                                newNode.Effects[effectKey].Value1 += effect.Value;

                            newNode.Effects[effectKey].Sources.Add(source.Coordinate);
                        } else {
                            Effect newEffect = new() {
                                Name = Settings.MapMods.MapModTypes[effectKey].Name,
                                Description = Settings.MapMods.MapModTypes[effectKey].Description,
                                Value1 = effect.Value,
                                ID = effect.ModId,
                                Enabled = Settings.MapMods.MapModTypes[effectKey].ShowOnMap && 
                                            !(Settings.MapMods.OnlyDrawApplicableMods && 
                                            !string.IsNullOrEmpty(requiredContent) && 
                                            (newNode.Content == null || !newNode.Content.Any(x => x.Value.Name.Contains(requiredContent)))),
                                Sources = [source.Coordinate]
                            };
                            
                            newNode.Effects.TryAdd(effectKey, newEffect);
                        }                                       
                    }
                }
            } catch (Exception e) {
                LogError($"Error getting Tower Effects for map {newNode.Coordinates}: " + e.Message);
            }
        }
        newNode.RecalculateWeight();

        lock (mapCacheLock)        
            return mapCache.TryAdd(node.Coordinate, newNode) ? 1 : 0;

    }

    private int RefreshCachedMapNode(AtlasNodeDescription node, Node cachedNode)
    {
        string shortID = node.Element.Area.Id.Trim().Replace("_NoBoss", "");
        cachedNode.IsUnlocked = node.Element.IsUnlocked;
        cachedNode.IsVisible = node.Element.IsVisible;
        cachedNode.IsVisited = node.Element.IsVisited || (!node.Element.IsUnlocked && node.Element.IsVisited);
        cachedNode.IsActive = node.Element.IsActive;
        cachedNode.Address = node.Element.Address;
        cachedNode.ParentAddress = node.Address;     
        cachedNode.MapNode = node;
        cachedNode.MapType = Settings.MapTypes.Maps.TryGetValue(shortID, out Map mapType) ? mapType : Settings.MapTypes.Maps.Where(x => x.Value.MatchID(node.Element.Area.Id)).Select(x => x.Value).FirstOrDefault() ?? new Map();

        if (cachedNode.IsVisited)
            return 1;

        cachedNode.Content.Clear();
        AddNodeContentTypesFromTextures(node, cachedNode);

        if (node.Element.Content != null)
            foreach(var content in node.Element.Content.Where(x => x.Name != ""))           
                cachedNode.Content.TryAdd(content.Name, Settings.MapContent.ContentTypes[content.Name]);

        try {
            cachedNode.Effects.Clear();
            foreach(var source in AtlasPanel.EffectSources.Where(x => Vector2.Distance(x.Coordinate, node.Coordinate) <= 11).ToList()) {
                foreach(var effect in source.Effects.Where(x => Settings.MapMods.MapModTypes.ContainsKey(x.ModId.ToString()) && x.Value != 0).ToList()) {
                    var effectKey = effect.ModId.ToString();
                    var requiredContent = Settings.MapMods.MapModTypes[effectKey].RequiredContent;
                    
                    if (cachedNode.Effects.TryGetValue(effectKey, out Effect existingEffect)) {
                        if (cachedNode.IsTower || !cachedNode.IsVisited)
                            cachedNode.Effects[effectKey].Value1 += effect.Value;

                        cachedNode.Effects[effectKey].Sources.Add(source.Coordinate);
                    } else {
                        Effect newEffect = new() {
                            Name = Settings.MapMods.MapModTypes[effectKey].Name,
                            Description = Settings.MapMods.MapModTypes[effectKey].Description,
                            Value1 = effect.Value,
                            ID = effect.ModId,
                            Enabled = Settings.MapMods.MapModTypes[effectKey].ShowOnMap && 
                                        !(Settings.MapMods.OnlyDrawApplicableMods && 
                                        !string.IsNullOrEmpty(requiredContent) && 
                                        (cachedNode.Content == null || !cachedNode.Content.Any(x => x.Value.Name.Contains(requiredContent)))),
                            Sources = [source.Coordinate]
                        };
                        
                        cachedNode.Effects.TryAdd(effectKey, newEffect);
                    }                                       
                }
            }
        } catch (Exception e) {
            LogError($"Error getting Tower Effects for map {cachedNode.Coordinates}: " + e.Message);
        }

        cachedNode.RecalculateWeight();
        return 1;
    } 
    
    private void CacheMapConnections(Node cachedNode) {
        
        if (cachedNode.Neighbors.Where(x => x.Value.Coordinates != default).Count() == 4)
            return;
            
        var connectionPoints = AtlasPanel.Points.FirstOrDefault(x => x.Item1 == cachedNode.Coordinates);
        cachedNode.NeighborCoordinates = (connectionPoints.Item2, connectionPoints.Item3, connectionPoints.Item4, connectionPoints.Item5);
        var neighborCoordinates = new[] { connectionPoints.Item2, connectionPoints.Item3, connectionPoints.Item4, connectionPoints.Item5 };

        foreach (Vector2i vector in neighborCoordinates)
            if (mapCache.TryGetValue(vector, out Node neighborNode))
                cachedNode.Neighbors.TryAdd(vector, neighborNode);
        
        // Get connections from other nodes to this node
        var neighborConnections = AtlasPanel.Points.Where(x => x.Item2 == cachedNode.Coordinates || x.Item3 == cachedNode.Coordinates || x.Item4 == cachedNode.Coordinates || x.Item5 == cachedNode.Coordinates).AsParallel().ToList();
        foreach (var point in neighborConnections)
            if (mapCache.TryGetValue(point.Item1, out Node neighborNode))
                cachedNode.Neighbors.TryAdd(point.Item1, neighborNode);
        
    }

    private void AddNodeContentTypesFromTextures(AtlasNodeDescription node, Node toNode) {
        
        if (node.Element.GetChildAtIndex(0).GetChildAtIndex(0).Children.Any(x => x.TextureName.Contains("Corrupt")))
            if (Settings.MapContent.ContentTypes.TryGetValue("Corrupted", out Content corruption))                        
                toNode.Content.TryAdd(corruption.Name, corruption);
            
        if (node.Element.GetChildAtIndex(0).GetChildAtIndex(0).Children.Any(x => x.TextureName.Contains("CorruptionNexus")))
            if (Settings.MapContent.ContentTypes.TryGetValue("Corrupted Nexus", out Content nexus))                        
                toNode.Content.TryAdd(nexus.Name, nexus);
        
        if (node.Element.GetChildAtIndex(0).GetChildAtIndex(0).Children.Any(x => x.TextureName.Contains("Sanctification")))
            if (Settings.MapContent.ContentTypes.TryGetValue("Cleansed", out Content cleansed))                        
                toNode.Content.TryAdd(cleansed.Name, cleansed);

        if (node.Element.GetChildAtIndex(0).GetChildAtIndex(0).Children.Any(x => x.TextureName.Contains("UniqueMap")))
            if (Settings.MapContent.ContentTypes.TryGetValue("UniqueMap", out Content uniqueMap))                        
                toNode.Content.TryAdd(uniqueMap.Name, uniqueMap);
        
    }
    
    #endregion

    #region Drawing Functions
    //MARK: DrawConnections
    /// <summary>
    /// Draws lines between a map node and its connected nodes on the atlas.
    /// </summary>
    /// <param name="WorldMap">The atlas panel containing the map nodes and their connections.</param>
    /// <param name="cachedNode">The map node for which connections are to be drawn.</param>
    /// 
    private void DrawConnections(Node cachedNode, RectangleF nodeCurrentPosition)
    {       
         foreach (Vector2i coordinates in cachedNode.GetNeighborCoordinates())
        {
            if (coordinates == default)
                continue;
            
            if (!mapCache.TryGetValue(coordinates, out Node destinationNode))
                continue;
                
            if (!Settings.Features.DrawVisitedNodeConnections && (destinationNode.IsVisited || cachedNode.IsVisited))
                continue;

            if ((!Settings.Features.DrawHiddenNodeConnections || !Settings.Features.ProcessHiddenNodes) && (!destinationNode.IsVisible || !cachedNode.IsVisible))
                continue;
            
            var destinationPos = destinationNode.MapNode.Element.GetClientRect();

            if (!IsOnScreen(destinationPos.Center) || !IsOnScreen(nodeCurrentPosition.Center))
                continue;

            if (Settings.Graphics.DrawGradientLines) {
                Color sourceColor = cachedNode.IsVisited ? Settings.Graphics.VisitedLineColor : cachedNode.IsUnlocked ? Settings.Graphics.UnlockedLineColor : Settings.Graphics.LockedLineColor;
                Color destinationColor = destinationNode.IsVisited ? Settings.Graphics.VisitedLineColor : destinationNode.IsUnlocked ? Settings.Graphics.UnlockedLineColor : Settings.Graphics.LockedLineColor;
                
                if (sourceColor == destinationColor)
                    Graphics.DrawLine(nodeCurrentPosition.Center, destinationPos.Center, Settings.Graphics.MapLineWidth, sourceColor);
                else
                    Graphics.DrawLine(nodeCurrentPosition.Center, destinationPos.Center, Settings.Graphics.MapLineWidth, sourceColor, destinationColor);

            } else {
                var color = Settings.Graphics.LockedLineColor;

                if (destinationNode.IsUnlocked || cachedNode.IsUnlocked)
                    color = Settings.Graphics.UnlockedLineColor;
                
                if (destinationNode.IsVisited && cachedNode.IsVisited)
                    color = Settings.Graphics.VisitedLineColor;

                Graphics.DrawLine(nodeCurrentPosition.Center, destinationPos.Center, Settings.Graphics.MapLineWidth, color);
            }
            
        }
    }

    /// MARK: HighlightMapContent
    /// <summary>
    /// Highlights a map node by drawing a circle around it if certain conditions are met.
    /// </summary>
    /// <param name="cachedNode">The map node to be highlighted.</param>
    /// <param name="Count">The count used to calculate the radius of the circle.</param>
    /// <param name="Content">The content string to check within the map node's elements.</param>
    /// <param name="Draw">A boolean indicating whether to draw the circle or not.</param>
    /// <param name="color">The color of the circle to be drawn.</param>
    /// <returns>Returns 1 if the circle is drawn, otherwise returns 0.</returns>
    private int DrawContentRings(Node cachedNode, RectangleF nodeCurrentPosition, int Count, string Content)
    {
        if ((cachedNode.IsVisited && !cachedNode.IsAttempted) || 
            (!Settings.MapContent.ShowRingsOnLockedNodes && !cachedNode.IsUnlocked) || 
            (!Settings.MapContent.ShowRingsOnUnlockedNodes && cachedNode.IsUnlocked) || 
            (!Settings.MapContent.ShowRingsOnHiddenNodes && !cachedNode.IsVisible) ||         
            !cachedNode.Content.TryGetValue(Content, out Content cachedContent) || 
            !cachedNode.MapType.Highlight || !cachedContent.Highlight)            
            return 0;

        float radius = (Count * Settings.Graphics.RingWidth) + 1 + ((nodeCurrentPosition.Right - nodeCurrentPosition.Left) / 2 * Settings.Graphics.RingRadius);
        Graphics.DrawCircle(nodeCurrentPosition.Center, radius, cachedContent.Color, Settings.Graphics.RingWidth, 32);

        return 1;
    }
    
    /// MARK: DrawWaypointLine
    /// Draws a line from the center of the screen to the specified map node on the atlas.
    /// </summary>
    /// <param name="mapNode">The atlas node to which the line will be drawn.</param>
    /// <remarks>
    /// This method checks if the feature to draw lines is enabled in the settings. If enabled, it finds the corresponding map settings
    /// for the given map node. If the map settings are found and the line drawing is enabled for that map, it proceeds to draw the line.
    /// Additionally, if the feature to draw line labels is enabled, it draws the node name and the distance to the node.
    /// </remarks>
    private void DrawWaypointLine(Node cachedNode)
    {
        
        if (cachedNode.IsVisited || cachedNode.IsAttempted || !cachedNode.MapType.DrawLine || !Settings.Features.DrawLines)
            return;

        RectangleF nodeCurrentPosition = cachedNode.MapNode.Element.GetClientRect();
        var distance = Vector2.Distance(screenCenter, nodeCurrentPosition.Center);

        if (distance < 400)
            return;

        Vector2 position = Vector2.Lerp(screenCenter, nodeCurrentPosition.Center, Settings.Graphics.LabelInterpolationScale);
        // Clamp position to screen
        var minX = Graphics.MeasureText($"{cachedNode.Name} ({distance:0})").X;
        var minY = Graphics.MeasureText($"{cachedNode.Name} ({distance:0})").Y;
        var maxX = GameController.Window.GetWindowRectangle().Width - Graphics.MeasureText($"{cachedNode.Name} ({distance:0})").X;
        var maxY = GameController.Window.GetWindowRectangle().Height - Graphics.MeasureText($"{cachedNode.Name} ({distance:0})").Y;
        // Clamp
        position.X = Math.Clamp(position.X, minX, maxX);
        position.Y = Math.Clamp(position.Y, minY, maxY);

        Graphics.DrawLine(position, nodeCurrentPosition.Center, Settings.Graphics.MapLineWidth, cachedNode.MapType.NodeColor);

        if (Settings.Features.DrawLineLabels) {
            DrawCenteredTextWithBackground( $"{cachedNode.Name} ({Vector2.Distance(screenCenter, nodeCurrentPosition.Center):0})", position, cachedNode.MapType.NameColor, Settings.Graphics.BackgroundColor, true, 10, 4);
        }  
            
        
    }
    
    /// MARK: DrawMapNode
    /// Draws a highlighted circle around a map node on the atlas if the node is configured to be highlighted.
    /// </summary>
    /// <param name="mapNode">The atlas node description containing information about the map node to be drawn.</param>   
    private void DrawMapNode(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        if (!Settings.MapTypes.HighlightMapNodes || cachedNode.IsVisited || !cachedNode.MapType.Highlight)
            return;
            
        var radius = (nodeCurrentPosition.Right - nodeCurrentPosition.Left) / 4 * Settings.Graphics.NodeRadius;
        var weight = cachedNode.Weight;
        Color color = Settings.MapTypes.ColorNodesByWeight ? ColorUtils.InterpolateColor(Settings.MapTypes.BadNodeColor, Settings.MapTypes.GoodNodeColor, (weight - minMapWeight) / (maxMapWeight - minMapWeight)) : cachedNode.MapType.NodeColor;
        Graphics.DrawCircleFilled(nodeCurrentPosition.Center, radius, color, 16);
    }

    //DrawMapNode
    private void DrawWeight(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        if (!Settings.MapTypes.DrawWeightOnMap ||
            (!cachedNode.IsVisible && !Settings.MapTypes.ShowMapNamesOnHiddenNodes) ||
            (cachedNode.IsUnlocked && !Settings.MapTypes.ShowMapNamesOnUnlockedNodes) ||
            (!cachedNode.IsUnlocked && !Settings.MapTypes.ShowMapNamesOnLockedNodes) ||
            cachedNode.IsVisited || !cachedNode.MapType.Highlight)
            return;  

        // get the map weight % relative to the average map weight
        float weight = (cachedNode.Weight - minMapWeight) / (maxMapWeight - minMapWeight);  
         
        float offsetX = Settings.MapTypes.ShowMapNames ? (Graphics.MeasureText(cachedNode.Name.ToUpper()).X / 2) + 30 : 50;
        Vector2 position = new(nodeCurrentPosition.Center.X + offsetX, nodeCurrentPosition.Center.Y);

        DrawCenteredTextWithBackground($"{(int)(weight*100)}%", position, ColorUtils.InterpolateColor(Settings.MapTypes.BadNodeColor, Settings.MapTypes.GoodNodeColor, weight), Settings.Graphics.BackgroundColor, true, 10, 3);
    }
    /// <summary>
    /// Draws the name of the map on the atlas.
    /// </summary>
    /// <param name="cachedNode">The atlas node description containing information about the map.</param>
    private void DrawMapName(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        if (!Settings.MapTypes.ShowMapNames ||
            (!cachedNode.IsVisible && !Settings.MapTypes.ShowMapNamesOnHiddenNodes) ||
            (cachedNode.IsUnlocked && !Settings.MapTypes.ShowMapNamesOnUnlockedNodes) ||
            (!cachedNode.IsUnlocked && !Settings.MapTypes.ShowMapNamesOnLockedNodes) ||
            cachedNode.IsVisited || !cachedNode.MapType.Highlight)
            return;

        Color fontColor = Settings.MapTypes.UseColorsForMapNames ? cachedNode.MapType.NameColor : Settings.Graphics.FontColor;
        Color backgroundColor = Settings.MapTypes.UseColorsForMapNames ? cachedNode.MapType.BackgroundColor : Settings.Graphics.BackgroundColor;
        
        if (Settings.MapTypes.UseWeightColorsForMapNames) {
            float weight = (cachedNode.Weight - minMapWeight) / (maxMapWeight - minMapWeight);
            fontColor = ColorUtils.InterpolateColor(Settings.MapTypes.BadNodeColor, Settings.MapTypes.GoodNodeColor, weight);
        }

        DrawCenteredTextWithBackground(cachedNode.Name.ToUpper(), nodeCurrentPosition.Center, fontColor, backgroundColor, true, 10, 3);
    }

    private void DrawTowerMods(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        if ((cachedNode.IsTower && !Settings.MapMods.ShowOnTowers) || (!cachedNode.IsTower && !Settings.MapMods.ShowOnMaps) || !cachedNode.MapType.Highlight)    
            return; 

        Dictionary<string, Color> mods = [];

        var effects = new List<Effect>();
        if (cachedNode.IsTower) {            
            if (Settings.MapMods.ShowOnTowers) {                
                effects = cachedNode.Effects.Where(x => x.Value.Sources.Contains(cachedNode.Coordinates)).Select(x => x.Value).ToList();

                if (effects.Count == 0 && cachedNode.IsVisited)
                    DrawCenteredTextWithBackground("MISSING TABLET", nodeCurrentPosition.Center + new Vector2(0, Settings.MapMods.MapModOffset), Color.Red, Settings.Graphics.BackgroundColor, true, 10, 4);
                }
        } else {
            if (Settings.MapMods.ShowOnMaps && !cachedNode.IsVisited) {
                effects = cachedNode.Effects.Where(x => x.Value.Enabled).Select(x => x.Value).ToList();
            }
        }

        if (effects.Count == 0)
            return;
        
        foreach (var effect in effects) {
            if (Settings.MapMods.MapModTypes.TryGetValue(effect.ID.ToString(), out Mod mod)) {
                if (effect.Value1 >= mod.MinValueToShow) {
                    mods.TryAdd(effect.ToString(), mod.Color);
                }
            }
            
        }
        mods = mods.OrderBy(x => x.Value.ToString()).ToDictionary(x => x.Key, x => x.Value);
        DrawMapModText(mods, cachedNode.MapNode.Element.GetClientRect().Center);
    }
    private void DrawMapModText(Dictionary<string, Color> mods, Vector2 position)
    {      
        using (Graphics.SetTextScale(Settings.MapMods.MapModScale)) {
            string fullText = string.Join("\n", mods.Select(x => $"{x.Key}"));
            var boxSize = Graphics.MeasureText(fullText) + new Vector2(10, 10);
            var lineHeight = Graphics.MeasureText("A").Y;
            position -= new Vector2(boxSize.X / 2, boxSize.Y / 2);

            // offset the box below the node
            position += new Vector2(0, (boxSize.Y / 2) + Settings.MapMods.MapModOffset);
            
            Graphics.DrawBox(position, boxSize + position, Settings.Graphics.BackgroundColor, 5.0f);

            position += new Vector2(5, 5);

            foreach (var mod in mods)
            {
                Graphics.DrawText(mod.Key, position, mod.Value);
                position += new Vector2(0, lineHeight);
            }
        }
    }

    // private void DrawBiomes(AtlasPanelNode mapNode)
    // {
    //     var currentPosition = mapNode.GetClientRect();
    //     if (!IsOnScreen(currentPosition.Center) || !mapCache.ContainsKey(mapNode.Address))
    //         return;

    //     Node cachedNode = mapCache[mapNode.Address];
    //     string mapName = mapNode.Area.Name.Trim();
    //     if (cachedNode == null || !Settings.Biomes.ShowBiomes || cachedNode.Biomes.Count == 0)    
    //         return; 

    //     Dictionary<string, Color> biomes = new Dictionary<string, Color>();

    //     var biomeList = new List<Biome>();
    //     if (Settings.Biomes.ShowBiomes && !mapNode.IsVisited) {
    //         biomeList = cachedNode.Biomes;
    //     }

    //     foreach (var biome in biomeList) {
    //         biomes.Add(biome.ToString(), Settings.Biomes.Biomes[biome.ToString()].Color);
    //     }

    //     DrawBiomeText(biomes, currentPosition.Center);
    // }

    // private void DrawBiomeText(Dictionary<string, Color> biomes, Vector2 position)
    // {      
    //     using (Graphics.SetTextScale(Settings.Biomes.BiomeScale)) {
    //         string fullText = string.Join("\n", biomes.Select(x => $"{x.Key}"));
    //         var boxSize = Graphics.MeasureText(fullText) + new Vector2(10, 10);
    //         var lineHeight = Graphics.MeasureText("A").Y;
    //         position = position - new Vector2(boxSize.X / 2, boxSize.Y / 2);

    //         // offset the box below the node
    //         position += new Vector2(0, (boxSize.Y / 2) + Settings.Biomes.BiomeOffset);
            
    //         if (!IsOnScreen(boxSize + position))
    //             return;

    //         Graphics.DrawBox(position, boxSize + position, Settings.Graphics.BackgroundColor, 5.0f);

    //         position += new Vector2(5, 5);

    //         foreach (var biome in biomes)
    //         {
    //             Graphics.DrawText(biome.Key, position, biome.Value);
    //             position += new Vector2(0, lineHeight);
    //         }
    //     }
    // }

    /// MARK: DrawTowersWithinRange
    /// <summary>
    /// Draws lines between towers and maps within range of eachother.
    /// </summary>
    /// <param name="mapNode"></param>
    private void DrawTowerRange(Node cachedNode) {
        if (!cachedNode.DrawTowers || (cachedNode.IsVisited && !cachedNode.IsTower))
            return;

        if (cachedNode.IsTower) {
            DrawNodesWithinRange(cachedNode);
        } else {
            DrawTowersWithinRange(cachedNode);
        }
    }
    /// MARK: DrawTowersWithinRange
    /// <summary>
    ///  Draws lines between the current map node and any Lost Towers within range.
    /// </summary>
    /// <param name="cachedNode"></param>
    private void DrawTowersWithinRange(Node cachedNode) {
        if (!cachedNode.DrawTowers || cachedNode.IsVisited)
            return;

        var nearbyTowers = mapCache.Where(x => x.Value.IsTower && Vector2.Distance(x.Value.Coordinates, cachedNode.Coordinates) <= 11).Select(x => x.Value).AsParallel().ToList();
        if (nearbyTowers.Count == 0)
            return;

        Vector2 nodePos = cachedNode.MapNode.Element.GetClientRect().Center;
        Graphics.DrawCircle(nodePos, 50, Settings.Graphics.LineColor, 5, 16);

        foreach (var tower in nearbyTowers) {
            if (!mapCache.TryGetValue(tower.Coordinates, out Node towerNode))
                continue;

            var towerPosition = towerNode.MapNode.Element.GetClientRect();                        
            var endPos = towerPosition.Center;
            var distance = Vector2.Distance(nodePos, endPos);
            var direction = (endPos - nodePos) / distance;
            var offset = direction * 50;

            Graphics.DrawCircle(towerPosition.Center, 50, Settings.Graphics.LineColor, 5, 16);      
            Graphics.DrawLine(nodePos + offset, endPos - offset, Settings.Graphics.MapLineWidth, Settings.Graphics.LineColor);     
            DrawCenteredTextWithBackground($"{nearbyTowers.Count:0} towers in range", nodePos + new Vector2(0, -50), Settings.Graphics.FontColor, Settings.Graphics.BackgroundColor, true, 10, 4);
        }
    }

    /// MARK: DrawNodesWithinRange
    /// <summary>
    /// Draws lines between maps and tower within range of eachother.
    /// </summary>
    /// <param name="cachedNode"></param>
    private void DrawNodesWithinRange(Node cachedNode) {
        if (!cachedNode.DrawTowers)
            return;

        var nearbyMaps = mapCache.Where(x => x.Value.Name != "Lost Towers" && !x.Value.IsVisited && Vector2.Distance(x.Value.Coordinates, cachedNode.Coordinates) <= 11).Select(x => x.Value).AsParallel().ToList();
        if (nearbyMaps.Count == 0)
            return;
        Vector2 nodePos = cachedNode.MapNode.Element.GetClientRect().Center;
        Graphics.DrawCircle(nodePos, 50, Settings.Graphics.LineColor, 5, 16);

        foreach (var map in nearbyMaps) {
            if(!mapCache.TryGetValue(map.Coordinates, out Node nearbyMap))
                continue;

            var mapPosition = nearbyMap.MapNode.Element.GetClientRect();            
            var endPos = mapPosition.Center;
            var distance = Vector2.Distance(nodePos, endPos);
            var direction = (endPos - nodePos) / distance;
            var offset = direction * 50;

            Graphics.DrawCircle(mapPosition.Center, 50, Settings.Graphics.LineColor, 5, 16);  
            Graphics.DrawLine(nodePos + offset, endPos - offset, Settings.Graphics.MapLineWidth, Settings.Graphics.LineColor);     
            DrawCenteredTextWithBackground($"{nearbyMaps.Count:0} maps in range", nodePos + new Vector2(0, -50), Settings.Graphics.FontColor, Settings.Graphics.BackgroundColor, true, 10, 4);
        }
    }

    #endregion

    #region Misc Drawing
    /// <summary>
    /// Draws text with a background color at the specified position.
    /// </summary>
    /// <param name="text">The text to draw.</param>
    /// <param name="position">The position to draw the text at.</param>
    /// <param name="textColor">The color of the text.</param>
    /// <param name="backgroundColor">The color of the background.</param>
    /// Yes, I know exilecore has this built in, but I wanted padding and rounded corners.

    private void DrawCenteredTextWithBackground(string text, Vector2 position, Color color, Color backgroundColor, bool center = false, int xPadding = 0, int yPadding = 0)
    {
        if (!IsOnScreen(position))
            return;

        var boxSize = Graphics.MeasureText(text);

        boxSize += new Vector2(xPadding, yPadding);    

        if (center)
            position = position - new Vector2(boxSize.X / 2, boxSize.Y / 2);

        Graphics.DrawBox(position, boxSize + position, backgroundColor, 5.0f);       

        position += new Vector2(xPadding / 2, yPadding / 2);

        Graphics.DrawText(text, position, color);
    }

    private void DrawRotatedImage(IntPtr textureId, Vector2 position, Vector2 size, float angle, Color color)
    {
        Vector2 center = position + size / 2;

        float cosTheta = (float)Math.Cos(angle);
        float sinTheta = (float)Math.Sin(angle);

        Vector2 RotatePoint(Vector2 point)
        {
            Vector2 translatedPoint = point - center;
            Vector2 rotatedPoint = new Vector2(
                translatedPoint.X * cosTheta - translatedPoint.Y * sinTheta,
                translatedPoint.X * sinTheta + translatedPoint.Y * cosTheta
            );
            return rotatedPoint + center;
        }

        Vector2 topLeft = RotatePoint(position);
        Vector2 topRight = RotatePoint(position + new Vector2(size.X, 0));
        Vector2 bottomRight = RotatePoint(position + size);
        Vector2 bottomLeft = RotatePoint(position + new Vector2(0, size.Y));


        Graphics.DrawQuad(textureId, topLeft, topRight, bottomRight, bottomLeft, color);
        }
        private void DrawGradientLine(Vector2 start, Vector2 end, Color startColor, Color endColor, float lineWidth)
    {
        // No need to draw a gradient if the colors are the same
        if (startColor == endColor)
        {
            Graphics.DrawLine(start, end, Settings.Graphics.MapLineWidth, startColor);
            return;
        }

        int segments = 10; // Number of segments to create the gradient effect
        Vector2 direction = (end - start) / segments;

        for (int i = 0; i < segments; i++)
        {
            Vector2 segmentStart = start + direction * i;
            Vector2 segmentEnd = start + direction * (i + 1);

            float t = (float)i / segments;
            Color segmentColor = ColorUtils.InterpolateColor(startColor, endColor, t);

            Graphics.DrawLine(segmentStart, segmentEnd, lineWidth, segmentColor);

        }
    }

    #endregion
    
    #region Helper Functions

    private bool IsOnScreen(Vector2 position)
    {
        var screen = new RectangleF
        {
            X = 0,
            Y = 0,
            Width = GameController.Window.GetWindowRectangleTimeCache.Size.X,
            Height = GameController.Window.GetWindowRectangleTimeCache.Size.Y
        };

        var left = screen.Left;
        var right = screen.Right;

        if (UI.OpenRightPanel.IsVisible)
            right -= UI.OpenRightPanel.GetClientRect().Width;

        if (UI.OpenLeftPanel.IsVisible || WaypointPanelIsOpen)
            left += Math.Max(UI.OpenLeftPanel.GetClientRect().Width, UI.SettingsPanel.GetClientRect().Width);

        RectangleF screenRect = new RectangleF(left, screen.Top, right - left, screen.Height);
        if (UI.WorldMap.GetChildAtIndex(9).IsVisible) {
            RectangleF mapTooltip = UI.WorldMap.GetChildAtIndex(9).GetClientRect();                
            mapTooltip.Inflate(mapTooltip.Width * 0.1f, mapTooltip.Height * 0.1f);

            if (mapTooltip.Contains(position))
                return false;
        }
        
        return screenRect.Contains(position);
    }

    public float GetDistanceToNode(Node cachedNode)
    {
        return Vector2.Distance(screenCenter, cachedNode.MapNode.Element.GetClientRect().Center);
    }
    
    #endregion
    #region Waypoint Panel
    private void DrawWaypointPanel() {
        Vector2 panelSize = new Vector2(UI.SettingsPanel.GetClientRect().Width, UI.SettingsPanel.GetClientRect().Height);
        Vector2 panelPosition = UI.SettingsPanel.GetClientRect().TopLeft;
        ImGui.SetNextWindowPos(panelPosition, ImGuiCond.Always);
        ImGui.SetNextWindowSize(panelSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.8f);

        ImGui.Begin("WaypointPanel", ref WaypointPanelIsOpen, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoMove);

        // Settings table
        if (ImGui.BeginTable("waypoint_top_table", 2, ImGuiTableFlags.NoBordersInBody|ImGuiTableFlags.PadOuterX))
        {
            ImGui.TableSetupColumn("Check", ImGuiTableColumnFlags.WidthFixed, 60);                                                               
            ImGui.TableSetupColumn("Option", ImGuiTableColumnFlags.WidthStretch, 300);                     

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            bool _show = Settings.Waypoints.ShowWaypoints;
            if(ImGui.Checkbox($"##show_waypoints", ref _show))                        
                Settings.Waypoints.ShowWaypoints = _show;

            ImGui.TableNextColumn();
            ImGui.Text("Show Waypoints on Atlas");

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            bool _showArrows = Settings.Waypoints.ShowWaypointArrows;
            if(ImGui.Checkbox($"##show_arrows", ref _showArrows))                        
                Settings.Waypoints.ShowWaypointArrows = _showArrows;

            ImGui.TableNextColumn();
            ImGui.Text("Show Waypoint Arrows on Atlas");

            ImGui.TableNextRow();
        }
        ImGui.EndTable();

        ImGui.Spacing();

        // larger font size
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 10));
        ImGui.Text("Waypoints");
        ImGui.PopStyleVar();        
        ImGui.Separator();


        #region Waypoints Table
        // Collapse
        if (ImGui.CollapsingHeader("Waypoints", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var flags = ImGuiTableFlags.BordersInnerH;
            ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0, 0, 0, 0));
            if (ImGui.BeginTable("waypoint_list_table", 8, flags))//, new Vector2(-1, panelSize.Y/3)))
            {
                ImGui.TableSetupColumn("Enable", ImGuiTableColumnFlags.WidthFixed, 30);                                                               
                ImGui.TableSetupColumn("Waypoint Name", ImGuiTableColumnFlags.WidthFixed, 300);     
                ImGui.TableSetupColumn("X", ImGuiTableColumnFlags.WidthFixed, 40);                    
                ImGui.TableSetupColumn("Y", ImGuiTableColumnFlags.WidthFixed, 40);     
                ImGui.TableSetupColumn("Color", ImGuiTableColumnFlags.WidthFixed, 30);     
                ImGui.TableSetupColumn("Scale", ImGuiTableColumnFlags.WidthFixed, 100);     
                ImGui.TableSetupColumn("Option", ImGuiTableColumnFlags.WidthFixed, 60); 
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthStretch, 50);
                ImGui.TableHeadersRow();                    

                foreach (var waypoint in Settings.Waypoints.Waypoints.Values) {
                    string id = waypoint.Address.ToString();
                    ImGui.PushID(id);
                    
                    ImGui.TableNextRow();

                    // Enabled
                    ImGui.TableNextColumn();
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - 30.0f) / 2.0f);
                    bool _show = waypoint.Show;
                    if (ImGui.Checkbox($"##{id}_enabled", ref _show)) {
                        waypoint.Show = _show;
                    }

                    // Name
                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(300);
                    string _name = waypoint.Name;                    
                    if (ImGui.InputText($"##{id}_name", ref _name, 32)) {
                        waypoint.Name = _name;
                    }
                    // Coordinates
                    ImGui.TableNextColumn();                    
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - 40.0f) / 2.0f);
                    ImGui.Text(waypoint.Coordinates.X.ToString());

                    ImGui.TableNextColumn();                    
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - 40.0f) / 2.0f);
                    ImGui.Text(waypoint.Coordinates.Y.ToString());


                    // Color
                    ImGui.TableNextColumn();
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - 30.0f) / 2.0f);
                    Color _color = waypoint.Color;
                    Vector4 _vector = new Vector4(_color.R / 255.0f, _color.G / 255.0f, _color.B / 255.0f, _color.A / 255.0f);
                    if(ImGui.ColorEdit4($"##{id}_nodecolor", ref _vector, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreview | ImGuiColorEditFlags.NoInputs))                        
                        waypoint.Color = Color.FromArgb((int)(_vector.W * 255), (int)(_vector.X * 255), (int)(_vector.Y * 255), (int)(_vector.Z * 255));
                    
                    // Scale
                    ImGui.TableNextColumn();
                    float _scale = waypoint.Scale;
                    ImGui.SetNextItemWidth(100);
                    if(ImGui.SliderFloat($"##{id}_weight", ref _scale, 0.1f, 2.0f, "%.2f"))                        
                        waypoint.Scale = _scale;


                    // Buttons
                    ImGui.TableNextColumn();
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - 50.0f) / 2.0f);
                    ImGui.SetNextItemWidth(60);
                    if (ImGui.Button("Delete")) {
                        RemoveWaypoint(waypoint);
                    }
                    ImGui.PopID();
                }
                ImGui.EndTable();
                ImGui.PopStyleColor();
            }
            #endregion
            
        }
       
        ImGui.Spacing();

        #region Atlas Table
        if (ImGui.CollapsingHeader("Atlas"))
        {
            

            // Sort by Combobox
            ImGui.Text("Sort: ");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            string sortBy = Settings.Waypoints.WaypointPanelSortBy;
            if (ImGui.BeginCombo("##sortByCombo", sortBy))
            {
                if (ImGui.Selectable("Name", sortBy == "Name")) 
                    sortBy = "Name";         
                if (ImGui.Selectable("Weight", sortBy == "Weight")) 
                    sortBy = "Weight";                    
       
                Settings.Waypoints.WaypointPanelSortBy = sortBy;
                ImGui.EndCombo();
            }

            ImGui.SameLine();
            ImGui.Spacing();
            ImGui.SameLine();

            ImGui.Text("Max Items: ");
            ImGui.SameLine();
            int maxItems = Settings.Waypoints.WaypointPanelMaxItems;
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputInt("##maxItems", ref maxItems))
                Settings.Waypoints.WaypointPanelMaxItems = maxItems; 

            ImGui.SameLine();
            ImGui.Spacing();
            ImGui.SameLine();

            bool unlockedOnly = Settings.Waypoints.ShowUnlockedOnly;
            if (ImGui.Checkbox("Show Unlocked Maps Only", ref unlockedOnly))
                Settings.Waypoints.ShowUnlockedOnly = unlockedOnly;

            ImGui.Separator();

            ImGui.Text("Search: ");
            ImGui.SameLine();
            string regex = Settings.Waypoints.WaypointPanelFilter;
            ImGui.SetNextItemWidth(250);
            if (ImGui.InputText("##search", ref regex, 32, ImGuiInputTextFlags.EnterReturnsTrue)) {
                Settings.Waypoints.WaypointPanelFilter = regex;
            } else if (ImGui.IsItemDeactivatedAfterEdit()) {
                Settings.Waypoints.WaypointPanelFilter = regex;
            } else if (ImGui.IsItemHovered()) {
                ImGui.SetTooltip("Searches for map names and/or mod text. Press enter to search.");
            }

            ImGui.SameLine();
            ImGui.Spacing();
            ImGui.SameLine();
            bool useRegex = Settings.Waypoints.WaypointsUseRegex;
            if (ImGui.Checkbox("Regex", ref useRegex))
                Settings.Waypoints.WaypointsUseRegex = useRegex;
            
            ImGui.Separator();
        
            var tempCache = mapCache.Where(x => !x.Value.IsVisited && (!Settings.Waypoints.ShowUnlockedOnly || x.Value.IsUnlocked)).AsParallel().ToDictionary(x => x.Key, x => x.Value);    
            // if search isnt blank
            if (!string.IsNullOrEmpty(Settings.Waypoints.WaypointPanelFilter)) {
                if (useRegex) {
                    tempCache = tempCache.Where(x => Regex.IsMatch(x.Value.Name, Settings.Waypoints.WaypointPanelFilter, RegexOptions.IgnoreCase) || x.Value.MatchEffect(Settings.Waypoints.WaypointPanelFilter) || x.Value.Content.Any(x => x.Value.Name == Settings.Waypoints.WaypointPanelFilter)).AsParallel().ToDictionary(x => x.Key, x => x.Value);
                } else {
                    tempCache = tempCache.Where(x => x.Value.Name.Contains(Settings.Waypoints.WaypointPanelFilter, StringComparison.CurrentCultureIgnoreCase) || x.Value.MatchEffect(Settings.Waypoints.WaypointPanelFilter) || x.Value.Content.Any(x => x.Value.Name == Settings.Waypoints.WaypointPanelFilter)).AsParallel().ToDictionary(x => x.Key, x => x.Value);
                }
            }

            tempCache = sortBy switch
            {
                "Name" => tempCache.OrderBy(x => x.Value.Name).ToDictionary(x => x.Key, x => x.Value),
                "Weight" => tempCache.OrderByDescending(x => x.Value.Weight).ToDictionary(x => x.Key, x => x.Value),
                _ => tempCache.OrderByDescending(x => x.Value.Weight).ToDictionary(x => x.Key, x => x.Value),
            };
            tempCache = tempCache.Take(maxItems).ToDictionary(x => x.Key, x => x.Value);

            var flags = ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable | ImGuiTableFlags.NoSavedSettings;
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(2, 2)); // Adjust the padding values as needed
            ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(2, 2)); // A
            if (ImGui.BeginTable("atlas_list_table", 8, flags))//, new Vector2(-1, panelSize.Y/3)))
            {                                                            
                ImGui.TableSetupColumn("Map Name", ImGuiTableColumnFlags.WidthFixed, 200);   
                ImGui.TableSetupColumn("Content", ImGuiTableColumnFlags.WidthFixed, 60);     
                ImGui.TableSetupColumn("Modifiers", ImGuiTableColumnFlags.WidthFixed, 100); 
                ImGui.TableSetupColumn("Weight", ImGuiTableColumnFlags.WidthFixed, 80);
                ImGui.TableSetupColumn("Unlocked", ImGuiTableColumnFlags.WidthFixed, 28);
                ImGui.TableSetupColumn("Way", ImGuiTableColumnFlags.WidthFixed, 32);
                ImGui.TableHeadersRow();                    

                Vector4 _colorVector;
                Color _color;

                if (tempCache != null) {
                    foreach (var (key, node) in tempCache) {
                        string id = node.Address.ToString();
                        ImGui.PushID(id);                        
                        ImGui.TableNextRow();

                        // Name
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(node.Name);

                        ImGui.SetWindowFontScale(0.7f);            

                        // Content
                        ImGui.TableNextColumn();
                        foreach(var (k,content) in node.Content) {
                            _color = Settings.MapContent.ContentTypes[content.Name].Color;
                            _colorVector = new Vector4(_color.R / 255.0f, _color.G / 255.0f, _color.B / 255.0f, _color.A / 255.0f);
                            ImGui.PushStyleColor(ImGuiCol.Text, _colorVector);
                            ImGui.TextUnformatted(content.Name);
                            ImGui.PopStyleColor();
                        }
                        

                        // Modifiers
                        ImGui.TableNextColumn();
                        foreach(var effect in node.Effects) {       
                            _color = Settings.MapMods.MapModTypes[effect.Key].Color;
                            _colorVector = new Vector4(_color.R / 255.0f, _color.G / 255.0f, _color.B / 255.0f, _color.A / 255.0f);
                            ImGui.PushStyleColor(ImGuiCol.Text, _colorVector);
                            ImGui.TextUnformatted(effect.Value.ToString());
                            ImGui.PopStyleColor();
                        }
                        // reset font size
                        ImGui.SetWindowFontScale(1.0f);

                        // Weight
                        ImGui.TableNextColumn();
                        // set color
                        float weight = (node.Weight - minMapWeight) / (maxMapWeight - minMapWeight);        
                        _color = ColorUtils.InterpolateColor(Settings.MapTypes.BadNodeColor,Settings.MapTypes.GoodNodeColor, weight);
                        _colorVector = new Vector4(_color.R / 255.0f, _color.G / 255.0f, _color.B / 255.0f, _color.A / 255.0f);
                        ImGui.PushStyleColor(ImGuiCol.Text, _colorVector);
                        ImGui.TextUnformatted(node.Weight.ToString("0.0"));
                        ImGui.PopStyleColor();

                        // Unlocked
                        ImGui.TableNextColumn();
                        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - 30.0f) / 2.0f);
                        bool _unlocked = node.IsUnlocked;
                        ImGui.BeginDisabled();                        
                        ImGui.Checkbox($"##{id}_enabled", ref _unlocked);
                        ImGui.EndDisabled();
    //
                        // Buttons
                        ImGui.TableNextColumn();
                        RectangleF icon = SpriteHelper.GetUV(MapIconsIndex.Waypoint);
                        
                        if (!node.IsWaypoint){
                            ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.TableRowBg));
                            if (ImGui.ImageButton($"$${id}_wp", iconsId, new Vector2(32,32), icon.TopLeft, icon.BottomRight)) {
                                AddWaypoint(node);
                            } else if (ImGui.IsItemHovered()) {
                                ImGui.SetTooltip("Add Waypoint");
                            }
                            ImGui.PopStyleColor();
                        }

                        ImGui.PopID();
                    }
                }
            }
            ImGui.EndTable();
            ImGui.PopStyleVar(2);
            #endregion
            
        }

        ImGui.End();
    }
    #endregion

    #region Waypoint Functions
    private void DrawWaypoint(Waypoint waypoint) {
        if (!Settings.Waypoints.ShowWaypoints || waypoint.MapNode() == null || !waypoint.Show || !IsOnScreen(waypoint.MapNode().Element.GetClientRect().Center))
            return;

        Vector2 waypointSize = new Vector2(48, 48);        
        waypointSize *= waypoint.Scale;

        Vector2 iconPosition = waypoint.MapNode().Element.GetClientRect().Center - new Vector2(0, waypoint.MapNode().Element.GetClientRect().Height / 2);

        if (waypoint.MapNode().Element.GetChildAtIndex(0) != null)
            iconPosition -= new Vector2(0, waypoint.MapNode().Element.GetChildAtIndex(0).GetClientRect().Height);

        iconPosition -= new Vector2(0, 20);
        Vector2 waypointTextPosition = iconPosition - new Vector2(0, 10);
        
        DrawCenteredTextWithBackground(waypoint.Name, waypointTextPosition, Settings.Graphics.FontColor, Settings.Graphics.BackgroundColor, true, 10, 4);
        
        iconPosition -= new Vector2(waypointSize.X / 2, 0);
        RectangleF iconSize = new RectangleF(iconPosition.X, iconPosition.Y, waypointSize.X, waypointSize.Y);
        Graphics.DrawImage(IconsFile, iconSize, SpriteHelper.GetUV(waypoint.Icon), waypoint.Color);


    }

    private void AddWaypoint(Node cachedNode) {
        if (Settings.Waypoints.Waypoints.ContainsKey(cachedNode.Coordinates.ToString()))
            return;

        float weight = (cachedNode.Weight - minMapWeight) / (maxMapWeight - minMapWeight);
        Waypoint newWaypoint = cachedNode.ToWaypoint();
        newWaypoint.Icon = MapIconsIndex.LootFilterLargeWhiteUpsideDownHouse;
        newWaypoint.Color = ColorUtils.InterpolateColor(Settings.MapTypes.BadNodeColor, Settings.MapTypes.GoodNodeColor, weight);

        Settings.Waypoints.Waypoints.Add(cachedNode.Coordinates.ToString(), newWaypoint);
    }

    private void RemoveWaypoint(Node cachedNode) {
        if (!Settings.Waypoints.Waypoints.ContainsKey(cachedNode.Coordinates.ToString()))
            return;

        Settings.Waypoints.Waypoints.Remove(cachedNode.Coordinates.ToString());
    }
    private void RemoveWaypoint(Waypoint waypoint) {
        Settings.Waypoints.Waypoints.Remove(waypoint.Coordinates.ToString());
    }

    private void DrawWaypointArrow(Waypoint waypoint) {
        if (!Settings.Waypoints.ShowWaypointArrows || waypoint.MapNode() == null)
            return;

        Vector2 waypointPosition = waypoint.MapNode().Element.GetClientRect().Center;

        float distance = Vector2.Distance(screenCenter, waypointPosition);

        if (distance < 400)
            return;

        Vector2 arrowSize = new(64, 64);
        Vector2 arrowPosition = waypointPosition;
        arrowPosition.X = Math.Clamp(arrowPosition.X, 0, GameController.Window.GetWindowRectangleTimeCache.Size.X);
        arrowPosition.Y = Math.Clamp(arrowPosition.Y, 0, GameController.Window.GetWindowRectangleTimeCache.Size.Y);
        arrowPosition = Vector2.Lerp(screenCenter, arrowPosition, 0.80f);
        arrowPosition -= new Vector2(arrowSize.X / 2, arrowSize.Y / 2);

        Vector2 direction = waypointPosition - screenCenter;
        float phi = (float)Math.Atan2(direction.Y, direction.X) + (float)(Math.PI / 2);

        Color color = Color.FromArgb(255, waypoint.Color);
        DrawRotatedImage(arrowId, arrowPosition, arrowSize, phi, color);

        Vector2 textPosition = arrowPosition + new Vector2(arrowSize.X / 2, arrowSize.Y / 2);
        textPosition = Vector2.Lerp(textPosition, screenCenter, 0.10f);
        DrawCenteredTextWithBackground($"{waypoint.Name} ({distance:0})", textPosition, color, Settings.Graphics.BackgroundColor, true, 10, 4);
    }

    #endregion



}
