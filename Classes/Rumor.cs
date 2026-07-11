namespace ExileMaps.Classes;

// One row in the Expedition Rumor Weights editor. Text is the exact AtlasButtonNode.Rumors key.
// Content/Description come from json/rumors.json; Weight is user-editable and persisted per profile.
public class Rumor
{
    public string Text { get; set; } = "";
    public string Content { get; set; } = "";
    public string Description { get; set; } = "";
    public float Weight { get; set; } = 25f;
}
