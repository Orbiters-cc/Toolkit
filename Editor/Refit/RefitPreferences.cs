using System;
using System.IO;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Refit
{
    /// <summary>
    /// Refit settings shared by the Orbiters tools of this project (MCB's ReFit panel, My Avatar), so a change made in one is
    /// used by the other. Stored in ProjectSettings, with the project.
    /// </summary>
    public static class RefitPreferences
    {
        public const float DefaultTightness = 0.5f;
        private const string FilePath = "ProjectSettings/OrbitersRefit.json";

        [Serializable]
        private sealed class Data
        {
            public float tightness = DefaultTightness;
        }

        private static Data data;

        /// <summary>Raised after a setting changed.</summary>
        public static event Action Changed;

        /// <summary>How close clothing is pulled to the body: 0 loose (accessories), 0.5 balanced, 1 tight (clothing).</summary>
        public static float Tightness
        {
            get => Load().tightness;
            set
            {
                value = Mathf.Clamp01(value);
                if (Mathf.Approximately(Load().tightness, value)) return;
                data.tightness = value;
                Save();
                Changed?.Invoke();
            }
        }

        private static Data Load()
        {
            if (data != null) return data;
            try { data = File.Exists(FilePath) ? JsonUtility.FromJson<Data>(File.ReadAllText(FilePath)) : null; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                Debug.LogWarning("[Orbiters] Could not read " + FilePath + ": " + ex.Message);
            }
            return data ??= new Data();
        }

        private static void Save()
        {
            try { File.WriteAllText(FilePath, JsonUtility.ToJson(data, true)); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Debug.LogWarning("[Orbiters] Could not save " + FilePath + ": " + ex.Message);
            }
        }
    }
}
