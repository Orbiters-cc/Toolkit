using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Refit
{
    /// <summary>The original base a custom base was made from, ready to refit from. Dispose it when done: it may be a temporary import.</summary>
    public sealed class CustomBaseOriginal : IDisposable
    {
        public GameObject Avatar;
        public SkinnedMeshRenderer Body;
        public Action Cleanup;

        public void Dispose()
        {
            var cleanup = Cleanup;
            Cleanup = null;
            cleanup?.Invoke();
        }
    }

    /// <summary>The custom base an avatar uses, as a tool knows it.</summary>
    public sealed class CustomBaseInfo
    {
        /// <summary>The custom base and version, stable across sessions; refit records keep it.</summary>
        public string Key;
        /// <summary>The custom base as users know it, e.g. "Muscle Orbit 0.5.0".</summary>
        public string Name;
        /// <summary>The original base it modifies, e.g. "Masculine Canine".</summary>
        public string BaseName;
        public int AssetId;
        public string Version;
        /// <summary>The avatar's body, which carries the custom blendshapes.</summary>
        public SkinnedMeshRenderer Body;
        /// <summary>The custom base's blendshapes on the body: the ones clothing should follow.</summary>
        public List<string> Shapes = new List<string>();
        /// <summary>Who answered: "MCB" (the avatar's MCB component) or "Orbiters" (the model file, recognised by Orbiters).</summary>
        public string Source;
        /// <summary>Resolves the original base for a full refit (main thread, may import a file). Null when this source can't.</summary>
        public Func<CustomBaseOriginal> ResolveOriginal;
        /// <summary>A small picture of the custom base, cheap to call again: null while it loads or when there is none.</summary>
        public Func<Texture2D> Thumbnail;
        /// <summary>What the custom base adds to the avatar, for budgets (main thread, cheap). Null when the tool can't tell.</summary>
        public Func<CustomBaseFootprint> Footprint;

        public bool CanFit => ResolveOriginal != null;
    }

    /// <summary>
    /// The part of an avatar a custom base accounts for: its objects (logic, sliders), the bones it added to the original
    /// skeleton, and what its build will add or remove that the scene does not show yet.
    /// </summary>
    public sealed class CustomBaseFootprint
    {
        /// <summary>Hierarchy roots the custom base owns: parameters, PhysBones and contacts under them are the custom base's.</summary>
        public List<GameObject> Objects = new List<GameObject>();
        /// <summary>Bones the custom base added to the original skeleton.</summary>
        public HashSet<Transform> Bones = new HashSet<Transform>();
        /// <summary>Build-time changes: PhysBones (and the transforms they simulate) it adds, and bones it removes.</summary>
        public int BuildPhysBones, BuildPhysBoneTransforms, BuildRemovedBones;

        public bool Owns(Transform transform)
        {
            if (transform == null) return false;
            foreach (var root in Objects)
                if (root != null && transform.IsChildOf(root.transform)) return true;
            return false;
        }
    }

    /// <summary>A tool that knows which custom base an avatar uses (MCB registers one).</summary>
    public interface ICustomBaseProvider
    {
        /// <summary>Main thread and cheap. Null when this provider knows nothing about the avatar.</summary>
        CustomBaseInfo Describe(Transform avatarRoot);
    }

    /// <summary>A provider that keeps fits to put back later (MCB saves them per version), so a refit taken back stays taken back.</summary>
    public interface ICustomBaseFits
    {
        /// <summary>The renderer's refit was taken back for good: never put a saved fit back on it for the current custom base.</summary>
        void Forget(Transform avatarRoot, SkinnedMeshRenderer renderer);
    }

    public static class CustomBases
    {
        private static readonly List<ICustomBaseProvider> Providers = new List<ICustomBaseProvider>();

        /// <summary>Raised with the avatar root when a provider's answer changed (a version was applied or reset).</summary>
        public static event Action<Transform> Changed;

        public static void Register(ICustomBaseProvider provider)
        {
            if (provider != null && !Providers.Contains(provider)) Providers.Add(provider);
        }

        public static void Unregister(ICustomBaseProvider provider) => Providers.Remove(provider);

        /// <summary>The first registered provider's answer for this avatar, or null.</summary>
        public static CustomBaseInfo Describe(Transform avatarRoot)
        {
            if (avatarRoot == null) return null;
            foreach (var provider in Providers)
            {
                CustomBaseInfo info;
                try { info = provider.Describe(avatarRoot); }
                catch (Exception ex)
                {
                    Debug.LogWarning("[Orbiters] Could not read the custom base of " + avatarRoot.name + ": " + ex.Message);
                    continue;
                }
                if (info != null) return info;
            }
            return null;
        }

        public static void NotifyChanged(Transform avatarRoot) => Changed?.Invoke(avatarRoot);

        /// <summary>Tells every provider keeping fits that the renderer's refit was taken back (see RefitRecords.Discard).</summary>
        public static void ForgetFit(Transform avatarRoot, SkinnedMeshRenderer renderer)
        {
            if (avatarRoot == null || renderer == null) return;
            foreach (var fits in Providers.OfType<ICustomBaseFits>())
            {
                try { fits.Forget(avatarRoot, renderer); }
                catch (Exception ex) { Debug.LogWarning("[Orbiters] Could not forget the saved fit of " + renderer.name + ": " + ex.Message); }
            }
        }

        /// <summary>Flexing shapes are named by convention: any name containing "flex", in any case.</summary>
        public static bool IsFlex(string name) => !string.IsNullOrEmpty(name) && name.IndexOf("flex", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// The blendshapes clothing follows on a custom base: its declared shapes (those on the body, when the body is known),
        /// then the body's flexing shapes, which a version may add without declaring them. Exact names, no duplicates.
        /// </summary>
        public static List<string> Shapes(IEnumerable<string> declared, Mesh body)
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (declared != null)
                foreach (string name in declared)
                    if (!string.IsNullOrEmpty(name) && (body == null || body.GetBlendShapeIndex(name) >= 0) && seen.Add(name)) names.Add(name);
            if (body != null)
                for (int i = 0; i < body.blendShapeCount; i++)
                {
                    string name = body.GetBlendShapeName(i);
                    if (IsFlex(name) && seen.Add(name)) names.Add(name);
                }
            return names;
        }
    }
}
