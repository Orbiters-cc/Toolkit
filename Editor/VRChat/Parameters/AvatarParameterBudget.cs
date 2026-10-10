using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Orbiters.Toolkit.Editor.VRChat.Parameters
{
    /// <summary>Estimated synced parameter memory, before and after VRCFury's build-time compression.</summary>
    public struct ParameterBudget
    {
        public const int MaxSyncedBits = 256;
        public int DescriptorBits, ToggleBits, FullControllerBits, TotalBeforeCompression;
        /// <summary>Bits of the custom base's objects (Options.IsCustomBase).</summary>
        public int CustomBaseBits;
        /// <summary>Bits of the face tracking template (<see cref="AvatarParameterBudget.FaceTrackingOwners"/>), as built.</summary>
        public int FaceTrackingBits;
        public int AvatarBits => TotalBeforeCompression - CustomBaseBits - FaceTrackingBits;
        public bool VrcFuryPresent;
        public string CompressionStatus;
        /// <summary>True when VRCFury will compress at build: over the limit, with compression allowed (or asked and accepted).</summary>
        public bool Compresses;
        /// <summary>Bits once VRCFury compressed them, when <see cref="Compresses"/>.</summary>
        public int CompressedBits;
        /// <summary>How many parameters VRCFury compresses, and how long a full sync of them takes.</summary>
        public int CompressedParameters;
        public float SyncSeconds;
        /// <summary>The bits that count against the limit once built.</summary>
        public int BuiltBits => Compresses ? CompressedBits : TotalBeforeCompression;
        public int Free => Mathf.Max(0, MaxSyncedBits - BuiltBits);
        public bool OverBudget => BuiltBits > MaxSyncedBits;
    }

    public static class AvatarParameterBudget
    {
        public sealed class Options
        {
            /// <summary>Objects a custom base owns: their toggles and controllers count as the custom base's.</summary>
            public Func<GameObject, bool> IsCustomBase;
        }

        // The menu control a synced parameter drives, in VRCFury's compressor priority order: a parameter several controls
        // use counts as the last of them. Radials, toggles and puppets can be compressed; buttons and sub-menus cannot.
        private enum MenuUse { None = -1, Radial = 0, Toggle = 1, TwoAxis = 2, FourAxis = 3, Button = 4, SubMenu = 5 }

        private struct Synced
        {
            public int Cost;
            public bool Bool;
            public MenuUse Menu;
        }

        // VRCFury's ParameterCompressorService: seconds per batch, plus half a frame at 30 fps.
        private const float BatchSeconds = .1f + .5f / 30f;

        /// <summary>
        /// Parameters a build step leaves out of a VRCFury component's parameter assets (e.g. face tracking features My
        /// Avatar does not sync), or out of the avatar's own expression parameters when the component is its
        /// <see cref="VRCAvatarDescriptor"/> (e.g. those only menu items left out of the upload set): they are not counted.
        /// Each function returns the names for one component, or null.
        /// </summary>
        public static readonly List<Func<Component, ICollection<string>>> BuildRemovedParameters = new List<Func<Component, ICollection<string>>>();

        /// <summary>
        /// VRCFury Toggles a build step leaves out with their menu item (e.g. items left out of the upload in My Avatar's
        /// menu editor): their parameter is not counted. Each function tells for one Toggle component.
        /// </summary>
        public static readonly List<Func<Component, bool>> BuildRemovedToggles = new List<Func<Component, bool>>();

        /// <summary>
        /// Tells which objects are a face tracking template (My Avatar's), so their parameters are counted apart from the
        /// avatar's own. Takes priority over the custom base when an object is both.
        /// </summary>
        public static readonly List<Func<GameObject, bool>> FaceTrackingOwners = new List<Func<GameObject, bool>>();

        private static Type vrcFuryType, toggleType, fullControllerType;
        private static System.Reflection.FieldInfo networkSyncedField;

        public static ParameterBudget Estimate(GameObject avatarRoot, Options options = null)
        {
            var budget = new ParameterBudget();
            if (avatarRoot == null) return budget;
            var descriptor = avatarRoot.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null) return budget;

            var parameters = new List<Synced>();
            var synced = new Dictionary<string, VRCExpressionParameters.ValueType>();
            var descriptorRemoved = RemovedByBuild(descriptor);
            if (descriptor.customExpressions)
            {
                var uses = MenuUses(descriptor.expressionsMenu);
                foreach (var parameter in SyncedParameters(descriptor.expressionParameters))
                {
                    if (synced.ContainsKey(parameter.name) || descriptorRemoved != null && descriptorRemoved.Contains(parameter.name)) continue;
                    synced[parameter.name] = parameter.valueType;
                    budget.DescriptorBits += Add(parameters, parameter.valueType, uses, parameter.name);
                }
            }
            budget.TotalBeforeCompression = budget.DescriptorBits;
            ResolveVrcFury();
            if (vrcFuryType == null) return budget;
            budget.VrcFuryPresent = true;

            bool IsFaceTracking(GameObject go) => go != null && IsFaceTrackingOwner(go);
            bool IsCustomBase(GameObject go) => go != null && !IsFaceTracking(go) && options?.IsCustomBase != null && options.IsCustomBase(go);
            var components = avatarRoot.GetComponentsInChildren(vrcFuryType, true);
            budget.FullControllerBits = FullControllerBits(components, synced, descriptorRemoved, parameters, IsCustomBase, IsFaceTracking, out int customControllers, out int faceControllers);
            budget.ToggleBits = ToggleBits(components, parameters, IsCustomBase, IsFaceTracking, out int customToggles, out int faceToggles);
            budget.TotalBeforeCompression = budget.DescriptorBits + budget.ToggleBits + budget.FullControllerBits;
            budget.CustomBaseBits = customControllers + customToggles;
            budget.FaceTrackingBits = faceControllers + faceToggles;

            var mode = VrcFury.Find("VF.Menu.CompressorMenuItem")?.GetMethod("Get")?.Invoke(null, null)?.ToString();
            budget.CompressionStatus = mode == "Ask" ? "VRCFury asks about compression during build."
                : mode == "Fail" ? "VRCFury compression is disabled in its global settings."
                : mode == "Compress" ? "VRCFury compresses automatically when needed during build."
                : "Compression is controlled by VRCFury during build.";
            if (budget.TotalBeforeCompression > ParameterBudget.MaxSyncedBits && mode != "Fail") Compress(ref budget, parameters);
            return budget;
        }

        // VRCFury's ParameterCompressorSolverService: from the least to the most aggressive set of menu controls, the first
        // set that fits; a more aggressive one only when it at least halves the sync time.
        private static void Compress(ref ParameterBudget budget, List<Synced> parameters)
        {
            var attempts = new[]
            {
                new[] { MenuUse.Radial }, new[] { MenuUse.Toggle }, new[] { MenuUse.Radial, MenuUse.Toggle },
                new[] { MenuUse.TwoAxis, MenuUse.FourAxis }, new[] { MenuUse.Radial, MenuUse.TwoAxis, MenuUse.FourAxis },
                new[] { MenuUse.Toggle, MenuUse.TwoAxis, MenuUse.FourAxis }, new[] { MenuUse.Radial, MenuUse.Toggle, MenuUse.TwoAxis, MenuUse.FourAxis },
            };
            int original = budget.TotalBeforeCompression, bestCost = original, bestCount = 0, bestBatches = 0;
            bool bestFits = false;
            foreach (var attempt in attempts)
            {
                var eligible = parameters.Where(p => attempt.Contains(p.Menu)).ToList();
                if (eligible.Count == 0) continue;
                int bools = eligible.Count(p => p.Bool);
                var (cost, batches) = Optimize(original, eligible.Sum(p => p.Cost), bools, eligible.Count - bools);
                if (bestFits)
                {
                    if (batches > bestBatches / 2f || cost > ParameterBudget.MaxSyncedBits) continue;
                }
                else if (cost >= bestCost) continue;
                bestCost = cost; bestCount = eligible.Count; bestBatches = batches;
                bestFits = cost <= ParameterBudget.MaxSyncedBits;
                if (bestFits && batches * .1f <= 1f) break;
            }
            if (bestCount == 0) return;
            budget.Compresses = true;
            budget.CompressedBits = bestCost;
            budget.CompressedParameters = bestCount;
            budget.SyncSeconds = bestBatches * BatchSeconds;
        }

        // VRCFury's OptimizationDecision: one bool and one number slot, then more while they fit, keeping batch counts even.
        private static (int cost, int batches) Optimize(int original, int removed, int bools, int numbers)
        {
            int Batches(int n, int b) => Math.Max(n > 0 ? (numbers + n - 1) / n : 0, b > 0 ? (bools + b - 1) / b : 0);
            int Cost(int n, int b) => original + IndexBits(Batches(n, b)) + n * 8 + b - removed;
            int numberSlots = numbers > 0 ? 1 : 0, boolSlots = bools > 0 ? 1 : 0;
            while (true)
            {
                if (numberSlots < numbers && Cost(numberSlots + 1, boolSlots) <= ParameterBudget.MaxSyncedBits
                    && (bools == 0 || (float)numberSlots / numbers < (float)boolSlots / bools)) numberSlots++;
                else if (boolSlots < bools && Cost(numberSlots, boolSlots + 1) <= ParameterBudget.MaxSyncedBits) boolSlots++;
                else break;
            }
            return (Cost(numberSlots, boolSlots), Batches(numberSlots, boolSlots));
        }

        private static int IndexBits(int batchCount)
        {
            int bits = 1;
            while ((1 << bits) < batchCount + 1) bits++;
            return bits;
        }

        private static int Add(List<Synced> parameters, VRCExpressionParameters.ValueType type, Dictionary<string, MenuUse> uses, string name)
        {
            int cost = VRCExpressionParameters.TypeCost(type);
            parameters.Add(new Synced { Cost = cost, Bool = type == VRCExpressionParameters.ValueType.Bool,
                Menu = name != null && uses.TryGetValue(name, out var use) ? use : MenuUse.None });
            return cost;
        }

        // Which control of the menu (and its sub-menus) each parameter drives.
        private static Dictionary<string, MenuUse> MenuUses(VRCExpressionsMenu menu)
        {
            var uses = new Dictionary<string, MenuUse>();
            var visited = new HashSet<VRCExpressionsMenu>();
            void Use(VRCExpressionsMenu.Control.Parameter parameter, MenuUse use)
            {
                if (string.IsNullOrEmpty(parameter?.name)) return;
                if (!uses.TryGetValue(parameter.name, out var previous) || use > previous) uses[parameter.name] = use;
            }
            void Walk(VRCExpressionsMenu current)
            {
                if (current?.controls == null || !visited.Add(current)) return;
                foreach (var control in current.controls)
                {
                    if (control == null) continue;
                    var sub = control.subParameters ?? Array.Empty<VRCExpressionsMenu.Control.Parameter>();
                    switch (control.type)
                    {
                        case VRCExpressionsMenu.Control.ControlType.Toggle: Use(control.parameter, MenuUse.Toggle); break;
                        case VRCExpressionsMenu.Control.ControlType.Button: Use(control.parameter, MenuUse.Button); break;
                        case VRCExpressionsMenu.Control.ControlType.RadialPuppet:
                            Use(control.parameter, MenuUse.SubMenu);
                            if (sub.Length > 0) Use(sub[0], MenuUse.Radial);
                            break;
                        case VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet:
                            Use(control.parameter, MenuUse.SubMenu);
                            foreach (var p in sub.Take(2)) Use(p, MenuUse.TwoAxis);
                            break;
                        case VRCExpressionsMenu.Control.ControlType.FourAxisPuppet:
                            Use(control.parameter, MenuUse.SubMenu);
                            foreach (var p in sub.Take(4)) Use(p, MenuUse.FourAxis);
                            break;
                        case VRCExpressionsMenu.Control.ControlType.SubMenu:
                            Use(control.parameter, MenuUse.SubMenu);
                            Walk(control.subMenu);
                            break;
                    }
                }
            }
            Walk(menu);
            return uses;
        }

        private static void ResolveVrcFury()
        {
            vrcFuryType ??= VrcFury.Find("VF.Model.VRCFury");
            toggleType ??= VrcFury.Find("VF.Model.Feature.Toggle");
            fullControllerType ??= VrcFury.Find("VF.Model.Feature.FullController");
        }

        private static bool IsSynced(VRCExpressionParameters.Parameter parameter)
        {
            networkSyncedField ??= typeof(VRCExpressionParameters.Parameter).GetField("networkSynced",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return networkSyncedField == null || (bool)networkSyncedField.GetValue(parameter);
        }

        private static IEnumerable<VRCExpressionParameters.Parameter> SyncedParameters(VRCExpressionParameters parameters) =>
            parameters?.parameters == null ? Enumerable.Empty<VRCExpressionParameters.Parameter>()
                : parameters.parameters.Where(p => p != null && !string.IsNullOrEmpty(p.name) && IsSynced(p));

        private static object Content(Component component) => VrcFury.Content(component);

        private static bool IsFaceTrackingOwner(GameObject go)
        {
            foreach (var owns in FaceTrackingOwners)
            {
                try { if (owns(go)) return true; }
                catch (Exception ex) { Debug.LogWarning("[Orbiters] A face tracking budget hook failed on " + go.name + ": " + ex.Message); }
            }

            return false;
        }

        private static int FullControllerBits(Component[] components, Dictionary<string, VRCExpressionParameters.ValueType> synced, HashSet<string> descriptorRemoved,
            List<Synced> parameters, Func<GameObject, bool> isCustomBase, Func<GameObject, bool> isFaceTracking, out int custom, out int face)
        {
            custom = 0;
            face = 0;
            if (fullControllerType == null) return 0;
            int added = 0;
            foreach (var component in components)
            {
                if (component == null) continue;
                var content = Content(component);
                if (content == null || !fullControllerType.IsInstanceOfType(content)) continue;
                // VRCFury gives each Full Controller its own namespace. Only explicit globals share names.
                var localNames = new HashSet<string>();
                var uses = new Dictionary<string, MenuUse>();
                if (fullControllerType.GetField("menus")?.GetValue(content) is IEnumerable menus)
                    foreach (var entry in menus)
                        if (VrcFury.ObjectReference(entry?.GetType().GetField("menu")?.GetValue(entry)) is VRCExpressionsMenu menu)
                            foreach (var pair in MenuUses(menu))
                                if (!uses.TryGetValue(pair.Key, out var previous) || pair.Value > previous) uses[pair.Key] = pair.Value;
                if (!(fullControllerType.GetField("prms")?.GetValue(content) is IEnumerable entries)) continue;
                var removed = RemovedByBuild(component);
                foreach (var entry in entries)
                {
                    if (!(VrcFury.ObjectReference(entry?.GetType().GetField("parameters")?.GetValue(entry)) is VRCExpressionParameters asset)) continue;
                    foreach (var parameter in SyncedParameters(asset))
                    {
                        if (removed != null && removed.Contains(parameter.name)) continue;
                        if (VrcFury.IsGlobalParameter(content, parameter.name))
                        {
                            // The avatar's own parameter: left out with it.
                            if (synced.ContainsKey(parameter.name) || descriptorRemoved != null && descriptorRemoved.Contains(parameter.name)) continue;
                            synced[parameter.name] = parameter.valueType;
                        }
                        else if (!localNames.Add(parameter.name)) continue;
                        int cost = Add(parameters, parameter.valueType, uses, parameter.name);
                        added += cost;
                        if (isFaceTracking(component.gameObject)) face += cost;
                        else if (isCustomBase(component.gameObject)) custom += cost;
                    }
                }
            }
            return added;
        }

        private static HashSet<string> RemovedByBuild(Component component)
        {
            HashSet<string> removed = null;
            foreach (var removes in BuildRemovedParameters)
            {
                ICollection<string> names;
                try { names = removes(component); }
                catch (Exception ex) { Debug.LogWarning("[Orbiters] A parameter budget hook failed on " + component.name + ": " + ex.Message); continue; }
                if (names == null || names.Count == 0) continue;
                removed ??= new HashSet<string>(StringComparer.Ordinal);
                removed.UnionWith(names);
            }
            return removed;
        }

        private static bool ToggleRemovedByBuild(Component component)
        {
            foreach (var removes in BuildRemovedToggles)
            {
                try { if (removes(component)) return true; }
                catch (Exception ex) { Debug.LogWarning("[Orbiters] A parameter budget hook failed on " + component.name + ": " + ex.Message); }
            }
            return false;
        }

        // A toggle is a menu toggle, a radial when it is a slider, or a button when held.
        private static int ToggleBits(Component[] components, List<Synced> parameters, Func<GameObject, bool> isCustomBase,
            Func<GameObject, bool> isFaceTracking, out int custom, out int face)
        {
            custom = 0;
            face = 0;
            if (toggleType == null) return 0;
            int raw = 0;
            foreach (var component in components)
            {
                if (component == null) continue;
                var content = Content(component);
                if (content == null || !toggleType.IsInstanceOfType(content) || ToggleRemovedByBuild(component)) continue;
                bool slider = (bool)(toggleType.GetField("slider")?.GetValue(content) ?? false);
                bool integer = (bool)(toggleType.GetField("useInt")?.GetValue(content) ?? false);
                bool held = (bool)(toggleType.GetField("holdButton")?.GetValue(content) ?? false);
                int cost = slider || integer ? 8 : 1;
                parameters.Add(new Synced { Cost = cost, Bool = cost == 1, Menu = held ? MenuUse.Button : slider ? MenuUse.Radial : MenuUse.Toggle });
                raw += cost;
                if (isFaceTracking(component.gameObject)) face += cost;
                else if (isCustomBase(component.gameObject)) custom += cost;
            }
            return raw;
        }
    }
}
