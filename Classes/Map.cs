using System;
using System.Drawing;
using System.Text.Json.Serialization;
using System.ComponentModel;
using System.Linq;

namespace ExileMaps.Classes
{
    public class Map : INotifyPropertyChanged
    {
        private Color nameColor = Color.FromArgb(255, 255, 255, 255);
        private Color backgroundColor = Color.FromArgb(220, 0, 0, 0);
        private Color nodeColor = Color.FromArgb(200, 155, 155, 155);
        private bool highlight = true;
        private bool colorNodesByWeight = true;
        private bool useWeightColorForName = false;
        private bool favorite = false;
        private SpriteIcon icon = SpriteIcon.Circle;

        [JsonIgnore]
        private int count = 0;

        [JsonIgnore]
        private int lockedCount = 0;

        [JsonIgnore]
        private int fogCount = 0;
        private float weight = 10.0f;

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public string Name { get; set; } = "";

        [JsonIgnore]
        public string ID { get; set; } = ""; // deprecated, here so peoples imported settings dont break immediately

        public string[] IDs { get; set; } = [];
        public string ShortestId { get; set; }

        // Sprite drawn for this map's nodes (and special-map indicator). Serializes as int; default Circle.
        // Raises PropertyChanged so icon edits flag the profile dirty and get snapshotted/persisted.
        public SpriteIcon Icon
        {
            get => icon;
            set
            {
                if (icon != value)
                {
                    icon = value;
                    OnPropertyChanged(nameof(Icon));
                }
            }
        }

        public override string ToString()
        {
            return Name;
        }

        public bool MatchID(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            return Array.Exists(IDs, x => x.Equals(id, StringComparison.OrdinalIgnoreCase));
        }

        [JsonConverter(typeof(JsonColorConverter))]
        public Color NameColor
        {
            get => nameColor;
            set
            {
                if (nameColor != value)
                {
                    nameColor = value;
                    OnPropertyChanged(nameof(NameColor));
                }
            }
        }

        [JsonConverter(typeof(JsonColorConverter))]
        public Color BackgroundColor
        {
            get => backgroundColor;
            set
            {
                if (backgroundColor != value)
                {
                    backgroundColor = value;
                    OnPropertyChanged(nameof(BackgroundColor));
                }
            }
        }

        [JsonConverter(typeof(JsonColorConverter))]
        public Color NodeColor
        {
            get => nodeColor;
            set
            {
                if (nodeColor != value)
                {
                    nodeColor = value;
                    OnPropertyChanged(nameof(NodeColor));
                }
            }
        }

        public bool Highlight
        {
            get => highlight;
            set
            {
                if (highlight != value)
                {
                    highlight = value;
                    OnPropertyChanged(nameof(Highlight));
                }
            }
        }

        public bool ColorNodesByWeight
        {
            get => colorNodesByWeight;
            set
            {
                if (colorNodesByWeight != value)
                {
                    colorNodesByWeight = value;
                    OnPropertyChanged(nameof(ColorNodesByWeight));
                }
            }
        }

        public bool UseWeightColorForName
        {
            get => useWeightColorForName;
            set
            {
                if (useWeightColorForName != value)
                {
                    useWeightColorForName = value;
                    OnPropertyChanged(nameof(UseWeightColorForName));
                }
            }
        }

        public bool Favorite
        {
            get => favorite;
            set
            {
                if (favorite != value)
                {
                    favorite = value;
                    OnPropertyChanged(nameof(Favorite));
                }
            }
        }

        public int Count
        {
            get => count;
            set
            {
                if (count != value)
                {
                    count = value;
                    OnPropertyChanged(nameof(Count));
                }
            }
        }

        public int LockedCount
        {
            get => lockedCount;
            set
            {
                if (lockedCount != value)
                {
                    lockedCount = value;
                    OnPropertyChanged(nameof(LockedCount));
                }
            }
        } 

        public int FogCount
        {
            get => fogCount;
            set
            {
                if (fogCount != value)
                {
                    fogCount = value;
                    OnPropertyChanged(nameof(FogCount));
                }
            }
        }

        public float Weight
        {
            get => weight;
            set
            {
                if (weight != value)
                {
                    weight = value;
                    OnPropertyChanged(nameof(Weight));
                }
            }
        }
    }
}
