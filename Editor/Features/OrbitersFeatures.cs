using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEditor;

[assembly: InternalsVisibleTo("Orbiters.Toolkit.Editor.Tests")]

namespace Orbiters.Toolkit.Editor
{
    public enum FeatureStage { Stable, Beta, Alpha, Experimental }

    /// <summary>An opt-in feature of an Orbiters tool, switched per user in the Orbiters settings window.</summary>
    public sealed class OrbitersFeature
    {
        public string Key, Product, Label, Description, PrefKey;
        public FeatureStage Stage;
        public bool Default;
        /// <summary>Key of a feature this one needs: it reads as off while that one is off.</summary>
        public string Requires;
    }

    /// <summary>Registry of the feature flags of every Orbiters tool, stored in the user's EditorPrefs.</summary>
    public static class OrbitersFeatures
    {
        private static readonly List<OrbitersFeature> features = new List<OrbitersFeature>();
        private static readonly Dictionary<string, OrbitersFeature> byKey = new Dictionary<string, OrbitersFeature>(StringComparer.Ordinal);

        public static IReadOnlyList<OrbitersFeature> All => features;
        /// <summary>Raised with the key of a feature switched through <see cref="SetEnabled"/>.</summary>
        public static event Action<string> Changed;

        /// <summary>Adds a feature once; registering the same key again returns the first registration.</summary>
        public static OrbitersFeature Register(OrbitersFeature feature)
        {
            if (feature == null || string.IsNullOrEmpty(feature.Key)) throw new ArgumentException("A feature needs a key.", nameof(feature));
            if (byKey.TryGetValue(feature.Key, out var existing)) return existing;
            if (string.IsNullOrEmpty(feature.PrefKey)) feature.PrefKey = "Orbiters.Feature." + feature.Key;
            if (string.IsNullOrEmpty(feature.Label)) feature.Label = feature.Key;
            features.Add(feature);
            byKey.Add(feature.Key, feature);
            return feature;
        }

        public static OrbitersFeature Find(string key) => key != null && byKey.TryGetValue(key, out var feature) ? feature : null;

        /// <summary>False for unregistered keys and while a required feature is off.</summary>
        public static bool IsEnabled(string key)
        {
            var feature = Find(key);
            if (feature == null) return false;
            if (!string.IsNullOrEmpty(feature.Requires) && feature.Requires != key && !IsEnabled(feature.Requires)) return false;
            return EditorPrefs.GetBool(feature.PrefKey, feature.Default);
        }

        /// <summary>Stores the user's choice; switching a feature off also switches off the features that require it.</summary>
        public static void SetEnabled(string key, bool enabled)
        {
            var feature = Find(key);
            if (feature == null) throw new ArgumentException("Unknown Orbiters feature: " + key, nameof(key));
            bool changed = EditorPrefs.GetBool(feature.PrefKey, feature.Default) != enabled;
            EditorPrefs.SetBool(feature.PrefKey, enabled);
            if (!enabled)
                foreach (var dependent in features.ToArray())
                    if (dependent.Requires == key && dependent.Key != key && EditorPrefs.GetBool(dependent.PrefKey, dependent.Default))
                        SetEnabled(dependent.Key, false);
            if (changed) Changed?.Invoke(key);
        }

        internal static void Unregister(string key)
        {
            if (!byKey.TryGetValue(key, out var feature)) return;
            byKey.Remove(key);
            features.Remove(feature);
        }
    }
}
