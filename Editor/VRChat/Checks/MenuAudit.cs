using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat.Optimization;
using Orbiters.Toolkit.Editor.VRChat.Parameters;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Orbiters.Toolkit.Editor.VRChat.Checks
{
    /// <summary>
    /// The expression menu and parameters of a built avatar: pages VRChat cuts short, controls that do nothing, and synced
    /// parameters no animator reads (bits of the 256 sent for nothing).
    /// </summary>
    public static class MenuAudit
    {
        /// <summary>Controls the Action Menu shows on one page.</summary>
        public const int PageSize = 8;

        // Read by VRChat's own layers when the avatar leaves them at their defaults (emotes, the face blend).
        private static readonly string[] DefaultLayerParameters = { "VRCEmote", "VRCFaceBlendH", "VRCFaceBlendV" };

        public static List<AvatarIssue> Find(GameObject root)
        {
            var issues = new List<AvatarIssue>();
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null || !descriptor.customExpressions) return issues;
            var usage = new AvatarUsage(root);
            bool defaults = (descriptor.baseAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
                .Concat(descriptor.specialAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>()).Any(l => l.isDefault);
            bool Read(string name) => usage.Reads(name) || VrcParameters.Protected(name) || defaults && DefaultLayerParameters.Contains(name);
            var declared = new Dictionary<string, VRCExpressionParameters.Parameter>(StringComparer.Ordinal);
            var list = descriptor.expressionParameters != null ? descriptor.expressionParameters.parameters : null;
            foreach (var parameter in list ?? Array.Empty<VRCExpressionParameters.Parameter>())
                if (parameter != null && !string.IsNullOrEmpty(parameter.name) && !declared.ContainsKey(parameter.name)) declared.Add(parameter.name, parameter);

            var seen = new HashSet<VRCExpressionsMenu>();
            void Visit(VRCExpressionsMenu menu, string path)
            {
                if (menu == null || !seen.Add(menu)) return;
                var controls = (menu.controls ?? new List<VRCExpressionsMenu.Control>()).Where(c => c != null).ToList();
                if (controls.Count > PageSize)
                    issues.Add(new AvatarIssue(IssueLevel.Problem, $"“{path}” holds {controls.Count} controls",
                        $"The Action Menu shows {PageSize}: the last {controls.Count - PageSize} can't be reached in game. Move some into a submenu.", path));
                foreach (var control in controls)
                {
                    string name = string.IsNullOrEmpty(control.name) ? "(unnamed)" : control.name;
                    string at = path + " / " + name;
                    var names = new List<string>();
                    if (!string.IsNullOrEmpty(control.parameter?.name)) names.Add(control.parameter.name);
                    foreach (var sub in control.subParameters ?? Array.Empty<VRCExpressionsMenu.Control.Parameter>())
                        if (!string.IsNullOrEmpty(sub?.name)) names.Add(sub.name);
                    foreach (var parameter in names.Distinct())
                    {
                        if (!declared.ContainsKey(parameter) && !VrcParameters.Protected(parameter))
                            issues.Add(new AvatarIssue(IssueLevel.Problem, $"“{name}” uses “{parameter}”, which isn't an expression parameter",
                                "VRChat ignores it: the control does nothing in game. Add the parameter, or point the control at an existing one.", at));
                        else if (declared.ContainsKey(parameter) && !Read(parameter))
                            issues.Add(new AvatarIssue(IssueLevel.Warning, $"“{name}” sets “{parameter}”, which no animator has",
                                "The control moves nothing in game: its toggle or animation was probably removed. Remove the control, or add what it should drive.", at));
                    }
                    if (control.type != VRCExpressionsMenu.Control.ControlType.SubMenu) continue;
                    if (control.subMenu == null)
                        issues.Add(new AvatarIssue(IssueLevel.Warning, $"“{name}” opens no menu", "The submenu shows empty in game. Pick its menu, or remove the control.", at));
                    else Visit(control.subMenu, at);
                }
            }
            Visit(descriptor.expressionsMenu, "Menu");

            foreach (var parameter in declared.Values.Where(AvatarParameterBudget.IsSynced))
            {
                if (Read(parameter.name)) continue;
                int bits = parameter.valueType == VRCExpressionParameters.ValueType.Bool ? 1 : 8;
                issues.Add(new AvatarIssue(IssueLevel.Warning, $"“{parameter.name}” is synced, but no animator reads it",
                    $"{bits} of the {ParameterBudget.MaxSyncedBits} synced bits go to other players for nothing. Untick Synced, or remove it if nothing uses it.", "Parameters"));
            }
            return issues;
        }
    }
}
