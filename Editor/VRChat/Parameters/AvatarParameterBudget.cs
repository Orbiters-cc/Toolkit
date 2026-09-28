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
    /// <summary>What an avatar will use of VRChat's synced parameter memory once VRCFury has built it.</summary>
    public struct ParameterBudget
    {
        public const int MaxSyncedBits = 256;

        /// <summary>Synced bits in the avatar descriptor's expression parameters, before the build.</summary>
        public int DescriptorBits;
        /// <summary>Bits VRCFury toggles, sliders and full controllers add, before compression.</summary>
        public int ToggleBits;
        public int FullControllerBits;
        /// <summary>Bits saved by VRCFury's Parameter Compressor, when present.</summary>
        public int CompressionSavings;
        /// <summary>Everything except the sliders a host tool is about to add (see <see cref="AvatarParameterBudget.Options"/>).</summary>
        public int WithoutAdded;
        /// <summary>Bits taken by the host tool's own sliders (reserved and about to be added).</summary>
        public int Added;
        public int TotalAfterBuild;
        public bool VrcFuryPresent;
        public bool CompressionEnabled;
        /// <summary>The compressor lives outside the host tool's own slider object.</summary>
        public bool CompressionIsExternal;
        public string CompressionPath;

        public int Free => Mathf.Max(0, MaxSyncedBits - TotalAfterBuild);
        public bool OverBudget => TotalAfterBuild > MaxSyncedBits;
    }

    /// <summary>
    /// Estimates synced parameter use after a VRCFury build, without building: the descriptor's expression parameters,
    /// VRCFury toggles (8 bits for sliders and ints, 1 for bools), parameters full controllers add, and the savings of
    /// VRCFury's Parameter Compressor. VRCFury is read by reflection, so the estimate works without it installed.
    /// </summary>
    public static class AvatarParameterBudget
    {
        public sealed class Options
        {
            /// <summary>Recognises a host tool's own slider object: its toggles count as <see cref="ParameterBudget.Added"/>.</summary>
            public Func<GameObject, bool> IsReservedSliderHost;
            /// <summary>Sliders the host tool is about to add (8 bits each, compressible).</summary>
            public int PlannedSliders;
            /// <summary>Estimate as if VRCFury's Parameter Compressor were added with its defaults, to show what it would save.</summary>
            public bool AssumeCompression;
        }

        private static Type vrcFuryType, toggleType, unlimitedType, fullControllerType;
        private static System.Reflection.FieldInfo networkSyncedField;

        public static ParameterBudget Estimate(GameObject avatarRoot, Options options = null)
        {
            var budget = new ParameterBudget();
            if (avatarRoot == null) return budget;
            var descriptor = avatarRoot.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null) return budget;

            budget.DescriptorBits = DescriptorBits(descriptor);
            ResolveVrcFury();
            if (vrcFuryType == null)
            {
                budget.WithoutAdded = budget.TotalAfterBuild = budget.DescriptorBits;
                return budget;
            }
            budget.VrcFuryPresent = true;

            bool IsReserved(GameObject go) => go != null && options?.IsReservedSliderHost != null && options.IsReservedSliderHost(go);
            var components = avatarRoot.GetComponentsInChildren(vrcFuryType, true);
            var compressor = FindCompressor(components);
            if (!compressor.enabled && options != null && options.AssumeCompression && unlimitedType != null)
                compressor = (true, false, false, string.Empty, null);
            budget.CompressionEnabled = compressor.enabled;
            budget.CompressionPath = compressor.path;
            budget.CompressionIsExternal = compressor.enabled && !IsReserved(compressor.host);

            var synced = SyncedTypes(descriptor.expressionParameters);
            var controllers = FullControllerStats(components, synced, compressor.enabled, compressor.includeBools, compressor.includePuppets);
            var menu = compressor.enabled
                ? MenuCompressionStats(new[] { descriptor.expressionsMenu }, synced, compressor.includeBools, compressor.includePuppets)
                : (numbers: 0, bools: 0);
            var toggles = ToggleStats(components, compressor.enabled, compressor.includeBools, IsReserved);

            int planned = Mathf.Max(0, options?.PlannedSliders ?? 0);
            int rawWithout = toggles.rawBits - toggles.reservedBits + controllers.addedBits;
            int numbersWithout = toggles.numbers - toggles.reservedCount + controllers.numbers + menu.numbers;
            int boolsWithout = toggles.bools + controllers.bools + menu.bools;
            int savingsWithout = compressor.enabled ? CompressionSavings(numbersWithout, boolsWithout) : 0;
            int savingsWith = compressor.enabled ? CompressionSavings(numbersWithout + planned, boolsWithout) : 0;

            budget.ToggleBits = toggles.rawBits - toggles.reservedBits;
            budget.FullControllerBits = controllers.addedBits;
            budget.CompressionSavings = savingsWith;
            budget.WithoutAdded = Mathf.Max(0, budget.DescriptorBits + rawWithout - savingsWithout);
            budget.TotalAfterBuild = Mathf.Max(0, budget.DescriptorBits + rawWithout + planned * 8 - savingsWith);
            budget.Added = Mathf.Max(0, budget.TotalAfterBuild - budget.WithoutAdded);
            return budget;
        }

        /// <summary>VRCFury is installed, so its Parameter Compressor can be added.</summary>
        public static bool CanCompress
        {
            get { ResolveVrcFury(); return vrcFuryType != null && unlimitedType != null; }
        }

        /// <summary>
        /// Adds VRCFury's Parameter Compressor to <paramref name="host"/> when none is found there (or below it, with
        /// <paramref name="includeChildren"/>), or removes those found. Undoable.
        /// </summary>
        public static void SetCompression(GameObject host, bool enabled, bool includeChildren = false)
        {
            if (host == null || !CanCompress) return;
            var found = (includeChildren ? host.GetComponentsInChildren(vrcFuryType, true) : host.GetComponents(vrcFuryType))
                .Where(c => c != null && unlimitedType.IsInstanceOfType(Content(c))).ToList();
            if (enabled)
            {
                if (found.Count > 0) return;
                var component = Undo.AddComponent(host, vrcFuryType);
                vrcFuryType.GetField("content").SetValue(component, Activator.CreateInstance(unlimitedType));
                EditorUtility.SetDirty(component);
            }
            else
                foreach (var component in found) Undo.DestroyObjectImmediate(component);
        }

        private static void ResolveVrcFury()
        {
            vrcFuryType ??= FindType("VF.Model.VRCFury");
            toggleType ??= FindType("VF.Model.Feature.Toggle");
            unlimitedType ??= FindType("VF.Model.Feature.UnlimitedParameters");
            fullControllerType ??= FindType("VF.Model.Feature.FullController");
        }

        private static bool IsSynced(VRCExpressionParameters.Parameter parameter)
        {
            networkSyncedField ??= typeof(VRCExpressionParameters.Parameter).GetField("networkSynced",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return networkSyncedField == null || (bool)networkSyncedField.GetValue(parameter);
        }

        private static int DescriptorBits(VRCAvatarDescriptor descriptor)
        {
            if (!descriptor.customExpressions || descriptor.expressionParameters?.parameters == null) return 0;
            int total = 0;
            foreach (var parameter in descriptor.expressionParameters.parameters)
                if (parameter != null && IsSynced(parameter)) total += VRCExpressionParameters.TypeCost(parameter.valueType);
            return total;
        }

        private static Dictionary<string, VRCExpressionParameters.ValueType> SyncedTypes(VRCExpressionParameters parameters)
        {
            var map = new Dictionary<string, VRCExpressionParameters.ValueType>();
            if (parameters?.parameters == null) return map;
            foreach (var parameter in parameters.parameters)
                if (parameter != null && !string.IsNullOrEmpty(parameter.name) && IsSynced(parameter) && !map.ContainsKey(parameter.name))
                    map[parameter.name] = parameter.valueType;
            return map;
        }

        private static object Content(Component component) => vrcFuryType.GetField("content")?.GetValue(component);

        private static (int addedBits, int numbers, int bools) FullControllerStats(Component[] components,
            Dictionary<string, VRCExpressionParameters.ValueType> synced, bool compression, bool includeBools, bool includePuppets)
        {
            if (fullControllerType == null) return (0, 0, 0);
            int added = 0;
            var menus = new List<VRCExpressionsMenu>();
            foreach (var component in components)
            {
                if (component == null) continue;
                var content = Content(component);
                if (content == null || !fullControllerType.IsInstanceOfType(content)) continue;
                if (fullControllerType.GetField("prms")?.GetValue(content) is IEnumerable entries)
                    foreach (var entry in entries)
                    {
                        if (!(ObjectReference(entry?.GetType().GetField("parameters")?.GetValue(entry)) is VRCExpressionParameters asset) || asset.parameters == null) continue;
                        foreach (var parameter in asset.parameters)
                        {
                            if (parameter == null || string.IsNullOrEmpty(parameter.name) || !IsSynced(parameter) || synced.ContainsKey(parameter.name)) continue;
                            synced[parameter.name] = parameter.valueType;
                            added += VRCExpressionParameters.TypeCost(parameter.valueType);
                        }
                    }
                if (fullControllerType.GetField("menus")?.GetValue(content) is IEnumerable menuEntries)
                    foreach (var entry in menuEntries)
                        if (ObjectReference(entry?.GetType().GetField("menu")?.GetValue(entry)) is VRCExpressionsMenu menu) menus.Add(menu);
            }
            if (!compression || menus.Count == 0) return (added, 0, 0);
            var stats = MenuCompressionStats(menus, synced, includeBools, includePuppets);
            return (added, stats.numbers, stats.bools);
        }

        private static (int numbers, int bools) MenuCompressionStats(IEnumerable<VRCExpressionsMenu> menus,
            Dictionary<string, VRCExpressionParameters.ValueType> synced, bool includeBools, bool includePuppets)
        {
            var names = new HashSet<string>();
            foreach (var menu in menus) CollectMenuParameters(menu, includePuppets, names);
            int numbers = 0, bools = 0;
            foreach (var name in names)
            {
                if (!synced.TryGetValue(name, out var type)) continue;
                if (type == VRCExpressionParameters.ValueType.Int || type == VRCExpressionParameters.ValueType.Float) numbers++;
                else if (includeBools && type == VRCExpressionParameters.ValueType.Bool) bools++;
            }
            return (numbers, bools);
        }

        private static void CollectMenuParameters(VRCExpressionsMenu root, bool includePuppets, HashSet<string> names)
        {
            if (root == null) return;
            var stack = new Stack<VRCExpressionsMenu>();
            var seen = new HashSet<VRCExpressionsMenu>();
            stack.Push(root);
            void Add(VRCExpressionsMenu.Control.Parameter parameter) { if (!string.IsNullOrEmpty(parameter?.name)) names.Add(parameter.name); }
            void AddSub(VRCExpressionsMenu.Control control, int count)
            {
                for (int i = 0; i < count && control.subParameters != null && i < control.subParameters.Length; i++) Add(control.subParameters[i]);
            }
            while (stack.Count > 0)
            {
                var menu = stack.Pop();
                if (menu == null || !seen.Add(menu) || menu.controls == null) continue;
                foreach (var control in menu.controls)
                {
                    if (control == null) continue;
                    switch (control.type)
                    {
                        case VRCExpressionsMenu.Control.ControlType.RadialPuppet: AddSub(control, 1); break;
                        case VRCExpressionsMenu.Control.ControlType.Toggle:
                        case VRCExpressionsMenu.Control.ControlType.Button: Add(control.parameter); break;
                        case VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet: if (includePuppets) AddSub(control, 2); break;
                        case VRCExpressionsMenu.Control.ControlType.FourAxisPuppet: if (includePuppets) AddSub(control, 4); break;
                        case VRCExpressionsMenu.Control.ControlType.SubMenu: stack.Push(control.subMenu); break;
                    }
                }
            }
        }

        private static UnityEngine.Object ObjectReference(object wrapper)
        {
            for (var type = wrapper?.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField("objRef", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field != null) return field.GetValue(wrapper) as UnityEngine.Object;
            }
            return null;
        }

        private static (bool enabled, bool includeBools, bool includePuppets, string path, GameObject host) FindCompressor(Component[] components)
        {
            if (unlimitedType == null) return (false, false, false, string.Empty, null);
            foreach (var component in components)
            {
                if (component == null) continue;
                var content = Content(component);
                if (content == null || !unlimitedType.IsInstanceOfType(content)) continue;
                bool includeBools = (bool)(unlimitedType.GetField("includeBools")?.GetValue(content) ?? false);
                bool includePuppets = (bool)(unlimitedType.GetField("includePuppets")?.GetValue(content) ?? false);
                return (true, includeBools, includePuppets, Path(component.gameObject), component.gameObject);
            }
            return (false, false, false, string.Empty, null);
        }

        private static (int rawBits, int numbers, int bools, int reservedBits, int reservedCount) ToggleStats(Component[] components,
            bool compression, bool includeBools, Func<GameObject, bool> isReserved)
        {
            if (toggleType == null) return (0, 0, 0, 0, 0);
            int raw = 0, numbers = 0, bools = 0, reservedBits = 0, reservedCount = 0;
            foreach (var component in components)
            {
                if (component == null) continue;
                var content = Content(component);
                if (content == null || !toggleType.IsInstanceOfType(content)) continue;
                bool slider = (bool)(toggleType.GetField("slider")?.GetValue(content) ?? false);
                bool integer = (bool)(toggleType.GetField("useInt")?.GetValue(content) ?? false);
                raw += slider || integer ? 8 : 1;
                if (compression)
                {
                    if (slider || integer) numbers++;
                    else if (includeBools) bools++;
                }
                if (slider && isReserved(component.gameObject)) { reservedCount++; reservedBits += 8; }
            }
            return (raw, numbers, bools, reservedBits, reservedCount);
        }

        /// <summary>The compressor packs numbers into one 8-bit value and index, and bools beyond eight into bytes.</summary>
        private static int CompressionSavings(int numbers, int bools)
        {
            numbers = Mathf.Max(0, numbers);
            int optimisedBools = bools <= 8 ? 0 : bools;
            int added = 8 + (numbers > 0 ? 8 : 0) + (optimisedBools > 0 ? 8 : 0);
            int removed = numbers * 8 + optimisedBools;
            return added >= removed ? 0 : removed - added;
        }

        private static Type FindType(string fullName)
        {
            foreach (var assembly in new[] { "VRCFury-Runtime", "VRCFury-Editor", "VRCFury" })
            {
                var type = Type.GetType(fullName + ", " + assembly);
                if (type != null) return type;
            }
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName);
                if (type != null) return type;
            }
            return null;
        }

        private static string Path(GameObject gameObject)
        {
            if (gameObject == null) return string.Empty;
            string path = gameObject.name;
            for (var parent = gameObject.transform.parent; parent != null; parent = parent.parent) path = parent.name + "/" + path;
            return path;
        }
    }
}
