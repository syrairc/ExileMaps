using System;
using System.Collections.Generic;
using System.Drawing;
using ExileCore2;
using ExileCore2.Shared.Enums;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Nodes;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.ComponentModel;

namespace ExileMaps.Classes
{
    public class Content : INotifyPropertyChanged
    {
        private string name;
        private float weight = 25.0f;
        private Color color = Color.FromArgb(255, 255, 255, 255);
        private bool highlight = true;
        private bool favorite = false;

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public override string ToString()
        {
            return Name;
        }
        
        public string Name
        {
            get => name;
            set
            {
                if (name != value)
                {
                    name = value;
                    OnPropertyChanged(nameof(Name));
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

        [JsonConverter(typeof(JsonColorConverter))]
        public Color Color
        {
            get => color;
            set
            {
                if (color != value)
                {
                    color = value;
                    OnPropertyChanged(nameof(Color));
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

        // Atlas icon path scraped from EndgameMapContentVisualIdentity. Stored for later rendering;
        // not drawn yet.
        public string AtlasIcon { get; set; }
    }
}
