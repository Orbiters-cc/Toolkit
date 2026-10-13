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

        public static List<AvatarIssue> Find(GameObject root, AvatarUsage usage = null)
        {
            var issues = new List<AvatarIssue>();
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null || !descriptor.customExpressions) return issues;
            usage ??= new AvatarUsage(root);
            bool defaults = (descriptor.baseAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
                .Concat(descriptor.specialAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>()).Any(l => l.isDefault);
            // VRChat sets parameters on the playable layers' animator only: an Animator component elsewhere never reads them.
            bool Read(string name) => usage.ReadsInPlayableLayers(name) || VrcParameters.Protected(name) || defaults && VrcParameters.DefaultExpressionParameters.Contains(name);
            var declared = new Dictionary<string, VRCExpressionParameters.Parameter>(StringComparer.Ordinal);
            var list = descriptor.expressionParameters != null ? descriptor.expressionParameters.parameters : null;
            foreach (var parameter in list ?? Array.Empty<VRCExpressionParameters.Parameter>())
                if (parameter != null && !string.IsNullOrEmpty(parameter.name) && !declared.ContainsKey(parameter.name)) declared.Add(parameter.name, parameter);
            // VRCFury removes the extra copies at upload, on every avatar of a project it is in.
            if (!VrcFury.Installed)
                foreach (var twice in (list ?? Array.Empty<VRCExpressionParameters.Parameter>()).Where(p => p != null && !string.IsNullOrEmpty(p.name)).GroupBy(p => p.name).Where(g => g.Count() > 1))
                    issues.Add(new AvatarIssue(IssueLevel.Warning, $"“{twice.Key}” is listed {twice.Count()} times in the parameters",
                        "VRChat uses one of them, and every synced copy still takes its bits. Remove the extra ones.", "Parameters"));

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
                    // Puppets use as many sub-parameters as they have axes; other controls keep the ones of an earlier type,
                    // which VRChat ignores.
                    var axes = (control.subParameters ?? Array.Empty<VRCExpressionsMenu.Control.Parameter>()).Take(Axes(control.type))
                        .Select(s => s?.name).Where(n => !string.IsNullOrEmpty(n)).ToList();
                    var names = new List<string>();
                    if (!string.IsNullOrEmpty(control.parameter?.name)) names.Add(control.parameter.name);
                    names.AddRange(axes);
                    if (!string.IsNullOrEmpty(control.parameter?.name) && declared.TryGetValue(control.parameter.name, out var set) && OutOfRange(set.valueType, control.value) is string range)
                        issues.Add(new AvatarIssue(IssueLevel.Warning, $"“{name}” sets “{set.name}” to {control.value:0.###}, which {range}",
                            "VRChat sets another value than the control asks for. Pick a value the parameter holds.", at));
                    foreach (var axis in axes.Distinct())
                        if (declared.TryGetValue(axis, out var moved) && moved.valueType != VRCExpressionParameters.ValueType.Float)
                            issues.Add(new AvatarIssue(IssueLevel.Warning, $"“{name}” moves “{axis}”, which is {moved.valueType}",
                                "A puppet sets a Float from its position: an Int or a Bool only takes whole values, so the control jumps or does nothing. Make the parameter a Float.", at));
                    if (axes.Count > axes.Distinct().Count())
                        issues.Add(new AvatarIssue(IssueLevel.Warning, $"“{name}” moves one parameter with two of its axes",
                            "The axes fight over it: it follows whichever moved last. Give each axis its own parameter.", at));
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

        // How many sub-parameters a control uses: one per puppet axis.
        private static int Axes(VRCExpressionsMenu.Control.ControlType type) =>
            type == VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet ? 2 : type == VRCExpressionsMenu.Control.ControlType.FourAxisPuppet ? 4
            : type == VRCExpressionsMenu.Control.ControlType.RadialPuppet ? 1 : 0;

        // Why a control's value doesn't fit its parameter, or null: Ints hold whole numbers from 0 to 255, Floats -1 to 1.
        private static string OutOfRange(VRCExpressionParameters.ValueType type, float value) =>
            type == VRCExpressionParameters.ValueType.Int && (value < 0f || value > 255f || Math.Abs(value - Math.Round(value)) > 1e-4) ? "an Int can't hold (whole numbers from 0 to 255)"
            : type == VRCExpressionParameters.ValueType.Float && (value < -1f || value > 1f) ? "a Float can't hold (-1 to 1)"
            : null;
    }
}
