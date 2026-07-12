using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ExileMaps.Classes
{
    public class ObservableDictionary<TKey, TValue> : IDictionary<TKey, TValue>, INotifyCollectionChanged, INotifyPropertyChanged
    {
        private readonly Dictionary<TKey, TValue> dictionary = new Dictionary<TKey, TValue>();

        public event NotifyCollectionChangedEventHandler CollectionChanged;
        public event PropertyChangedEventHandler PropertyChanged;

        public TValue this[TKey key]
        {
            get => dictionary[key];
            set
            {
                if (dictionary.ContainsKey(key))
                {
                    var oldValue = dictionary[key];
                    if (oldValue is INotifyPropertyChanged oldNotifyPropertyChanged)
                    {
                        oldNotifyPropertyChanged.PropertyChanged -= Value_PropertyChanged;
                    }

                    dictionary[key] = value;
                    if (value is INotifyPropertyChanged newNotifyPropertyChanged)
                    {
                        newNotifyPropertyChanged.PropertyChanged += Value_PropertyChanged;
                    }

                    OnPropertyChanged(nameof(Count));
                    OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace, new KeyValuePair<TKey, TValue>(key, value), new KeyValuePair<TKey, TValue>(key, oldValue)));
                }
                else
                {
                    dictionary[key] = value;
                    if (value is INotifyPropertyChanged newNotifyPropertyChanged)
                    {
                        newNotifyPropertyChanged.PropertyChanged += Value_PropertyChanged;
                    }

                    OnPropertyChanged(nameof(Count));
                    OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, new KeyValuePair<TKey, TValue>(key, value)));
                }
            }
        }

        public ICollection<TKey> Keys => dictionary.Keys;
        public ICollection<TValue> Values => dictionary.Values;
        public int Count => dictionary.Count;
        public bool IsReadOnly => false;

        public void Add(TKey key, TValue value)
        {
            if (dictionary.ContainsKey(key))
                return;
                
            dictionary.Add(key, value);
            if (value is INotifyPropertyChanged notifyPropertyChanged)
            {
                notifyPropertyChanged.PropertyChanged += Value_PropertyChanged;
            }

            OnPropertyChanged(nameof(Count));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, new KeyValuePair<TKey, TValue>(key, value)));
        }

        public bool Remove(TKey key)
        {
            if (dictionary.TryGetValue(key, out TValue value) && dictionary.Remove(key))
            {
                if (value is INotifyPropertyChanged notifyPropertyChanged)
                {
                    notifyPropertyChanged.PropertyChanged -= Value_PropertyChanged;
                }

                OnPropertyChanged(nameof(Count));
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, new KeyValuePair<TKey, TValue>(key, value)));
                return true;
            }
            return false;
        }

        public bool TryGetValue(TKey key, out TValue value) => dictionary.TryGetValue(key, out value);
        public void Clear()
        {
            foreach (var value in dictionary.Values)
            {
                if (value is INotifyPropertyChanged notifyPropertyChanged)
                {
                    notifyPropertyChanged.PropertyChanged -= Value_PropertyChanged;
                }
            }

            dictionary.Clear();
            OnPropertyChanged(nameof(Count));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        public bool ContainsKey(TKey key) => dictionary.ContainsKey(key);
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => dictionary.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => dictionary.GetEnumerator();
        public void Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);
        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) => ((IDictionary<TKey, TValue>)dictionary).CopyTo(array, arrayIndex);
        public bool Remove(KeyValuePair<TKey, TValue> item) => Remove(item.Key);

        public bool Contains(KeyValuePair<TKey, TValue> item) => dictionary.ContainsKey(item.Key) && EqualityComparer<TValue>.Default.Equals(dictionary[item.Key], item.Value);

        protected virtual void OnCollectionChanged(NotifyCollectionChangedEventArgs e) => CollectionChanged?.Invoke(this, e);
        protected virtual void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private void Value_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            try {
                var item = (TValue)sender;
                // Propagate item property changes (e.g. Weight edits) so subscribers can react.
                // Drives the debounced weight recalc. Without this, only Add/Remove notified them.
                // No subscriber reads the key (all handlers use (_, _)), so skip the O(N) reverse
                // lookup that used to scan the whole dictionary on every property change.
                OnPropertyChanged(e.PropertyName);
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace, new KeyValuePair<TKey, TValue>(default, item), new KeyValuePair<TKey, TValue>(default, item)));
            }
            catch (Exception) {
            }
        }
    }
}