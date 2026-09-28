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
    /// <summary>Estimated synced parameter memory before VRCFury's build-time compression.</summary>
    public struct ParameterBudget
    {
        public const int MaxSyncedBits = 256;
        public int DescriptorBits, ToggleBits, FullControllerBits, WithoutAdded, Added, TotalBeforeCompression;
        public bool VrcFuryPresent;
        public string CompressionStatus;
        public int Free => Mathf.Max(0, MaxSyncedBits - TotalBeforeCompression);
        public bool OverBudget => TotalBeforeCompression > MaxSyncedBits;
    }

    public static class AvatarParameterBudget
    {
        public sealed class Options
        {
            public Func<GameObject, bool> IsReservedSliderHost;
            public int PlannedSliders;
        }

        private static Type vrcFuryType, toggleType, fullControllerType;
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
                budget.WithoutAdded = budget.TotalBeforeCompression = budget.DescriptorBits;
                return budget;
            }
            budget.VrcFuryPresent = true;

            bool IsReserved(GameObject go) => go != null && options?.IsReservedSliderHost != null && options.IsReservedSliderHost(go);
            var components = avatarRoot.GetComponentsInChildren(vrcFuryType, true);
            // Compression eligibility depends on the completed animator build. Do not invent savings here.
            var mode = VrcFury.Find("VF.Menu.CompressorMenuItem")?.GetMethod("Get")?.Invoke(null, null)?.ToString();
            budget.CompressionStatus = mode == "Ask" ? "VRCFury asks about compression during build."
                : mode == "Fail" ? "VRCFury compression is disabled in its global settings."
                : mode == "Compress" ? "VRCFury compresses automatically when needed during build."
                : "Compression is controlled by VRCFury during build.";
            var synced = descriptor.customExpressions ? SyncedTypes(descriptor.expressionParameters)
                : new Dictionary<string, VRCExpressionParameters.ValueType>();
            budget.FullControllerBits = FullControllerBits(components, synced);
            budget.ToggleBits = ToggleBits(components, IsReserved);
            budget.WithoutAdded = budget.DescriptorBits + budget.ToggleBits + budget.FullControllerBits;
            budget.Added = Mathf.Max(0, options?.PlannedSliders ?? 0) * 8;
            budget.TotalBeforeCompression = budget.WithoutAdded + budget.Added;
            return budget;
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

        private static object Content(Component component) => VrcFury.Content(component);

        private static int FullControllerBits(Component[] components, Dictionary<string, VRCExpressionParameters.ValueType> synced)
        {
            if (fullControllerType == null) return 0;
            int added = 0;
            foreach (var component in components)
            {
                if (component == null) continue;
                var content = Content(component);
                if (content == null || !fullControllerType.IsInstanceOfType(content)) continue;
                // VRCFury gives each Full Controller its own namespace. Only explicit globals share names.
                var localNames = new HashSet<string>();
                var globals = fullControllerType.GetField("globalParams")?.GetValue(content) as IEnumerable<string>;
                if (!(fullControllerType.GetField("prms")?.GetValue(content) is IEnumerable entries)) continue;
                foreach (var entry in entries)
                {
                    if (!(VrcFury.ObjectReference(entry?.GetType().GetField("parameters")?.GetValue(entry)) is VRCExpressionParameters asset) || asset.parameters == null) continue;
                    foreach (var parameter in asset.parameters)
                    {
                        if (parameter == null || string.IsNullOrEmpty(parameter.name) || !IsSynced(parameter)) continue;
                        if (IsGlobal(parameter.name, globals))
                        {
                            if (synced.ContainsKey(parameter.name)) continue;
                            synced[parameter.name] = parameter.valueType;
                        }
                        else if (!localNames.Add(parameter.name)) continue;
                        added += VRCExpressionParameters.TypeCost(parameter.valueType);
                    }
                }
            }
            return added;
        }

        private static bool IsGlobal(string name, IEnumerable<string> rules)
        {
            bool global = false;
            foreach (string rule in rules ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(rule)) continue;
                bool negative = rule.StartsWith("!", StringComparison.Ordinal);
                string match = negative ? rule.Substring(1) : rule;
                bool wildcard = match.EndsWith("*", StringComparison.Ordinal);
                if (wildcard) match = match.Substring(0, match.Length - 1);
                if (name == match || (wildcard && name.StartsWith(match, StringComparison.Ordinal)))
                {
                    if (negative) return false;
                    global = true;
                }
            }
            return global;
        }


        private static int ToggleBits(Component[] components, Func<GameObject, bool> isReserved)
        {
            if (toggleType == null) return 0;
            int raw = 0;
            foreach (var component in components)
            {
                if (component == null) continue;
                var content = Content(component);
                if (content == null || !toggleType.IsInstanceOfType(content)) continue;
                bool slider = (bool)(toggleType.GetField("slider")?.GetValue(content) ?? false);
                bool integer = (bool)(toggleType.GetField("useInt")?.GetValue(content) ?? false);
                if (slider && isReserved(component.gameObject)) continue;
                raw += slider || integer ? 8 : 1;
            }
            return raw;
        }


    }
}
