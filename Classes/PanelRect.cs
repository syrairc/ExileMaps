namespace ExileMaps.Classes
{
    // Persisted window position/size for a movable panel. Saved with plugin settings so a panel
    // reopens where the user last left it (across game restarts). Saved=false until first stored.
    public class PanelRect
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float W { get; set; }
        public float H { get; set; }
        public bool Saved { get; set; } = false;
    }
}
