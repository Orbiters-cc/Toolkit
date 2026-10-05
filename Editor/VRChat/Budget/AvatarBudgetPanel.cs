using System;
using System.Collections.Generic;
using Orbiters.Toolkit.Editor.Refit;
using Orbiters.Toolkit.Editor.VRChat.Parameters;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;

namespace Orbiters.Toolkit.Editor.VRChat.Budget
{
    /// <summary>
    /// An avatar's budget against VRChat's limits: synced parameters and the bones, PhysBones and contacts that decide its
    /// PC performance rank. Each bar shows the avatar's share and its custom base's share. Recounts itself shortly after
    /// the hierarchy changes.
    /// </summary>
    public sealed class AvatarBudgetPanel : VisualElement
    {
        private const string StyleSheetPath = "Packages/orbiters.toolkit/Editor/VRChat/Budget/avatar-budget.uss";
        private static readonly Color AvatarColor = new Color32(0xd8, 0xd8, 0xd8, 0xff), CustomBaseColor = new Color32(0x00, 0xda, 0x6d, 0xff);

        private readonly Func<AvatarBudget> estimate;
        private readonly VisualElement legend, metrics;
        private readonly Label compression;
        private IVisualElementScheduledItem pending;

        /// <param name="estimate">Counts the avatar again, e.g. <c>() => AvatarBudget.Estimate(root)</c>.</param>
        public AvatarBudgetPanel(Func<AvatarBudget> estimate)
        {
            this.estimate = estimate;
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet) styleSheets.Add(sheet);
            AddToClassList("orb-avatar-budget");
            legend = new VisualElement(); legend.AddToClassList("orb-avatar-budget__legend"); Add(legend);
            metrics = new VisualElement(); metrics.AddToClassList("orb-avatar-budget__metrics"); Add(metrics);
            compression = new Label(); compression.AddToClassList("orb-avatar-budget__note");
            compression.tooltip = "Estimates are before compression. Change compression behavior from VRCFury's global settings.";
            Add(compression);

            // Toggles, controllers and bones change as the hierarchy is edited: recount shortly after, not on every change.
            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                EditorApplication.hierarchyChanged += ScheduleRefresh;
                Undo.undoRedoPerformed += ScheduleRefresh;
                CustomBases.Changed += OnCustomBaseChanged;
            });
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                EditorApplication.hierarchyChanged -= ScheduleRefresh;
                Undo.undoRedoPerformed -= ScheduleRefresh;
                CustomBases.Changed -= OnCustomBaseChanged;
            });
            Refresh();
        }

        private void OnCustomBaseChanged(Transform _) => ScheduleRefresh();

        public void ScheduleRefresh()
        {
            pending?.Pause();
            pending = schedule.Execute(Refresh).StartingIn(400);
        }

        public void Refresh() => Show(estimate?.Invoke() ?? new AvatarBudget());

        public void Show(AvatarBudget budget)
        {
            legend.Clear(); metrics.Clear();
            bool custom = !string.IsNullOrEmpty(budget.CustomBase);
            legend.Add(Key("Avatar", AvatarColor, "The avatar's own parameters, bones, PhysBones and contacts."));
            if (custom) legend.Add(Key("Custom base", CustomBaseColor, budget.CustomBase + ": what it adds to the avatar, as built."));

            var parameters = budget.Parameters;
            int free = Mathf.Max(0, ParameterBudget.MaxSyncedBits - parameters.TotalBeforeCompression);
            metrics.Add(Metric("Parameters", parameters.TotalBeforeCompression, "/ " + ParameterBudget.MaxSyncedBits + " bits",
                parameters.OverBudget ? (parameters.TotalBeforeCompression - ParameterBudget.MaxSyncedBits) + " over" : free + " free",
                parameters.OverBudget ? "very-poor" : "good",
                parameters.OverBudget ? "This estimate exceeds 256 bits before compression. Check the final build result in VRCFury." : "Synced parameter memory left.",
                new BudgetCount(parameters.AvatarBits, parameters.CustomBaseBits), Mathf.Max(ParameterBudget.MaxSyncedBits, parameters.TotalBeforeCompression),
                null, true));
            metrics.Add(Rated("Bones", budget.Bones, l => l.boneCount,
                budget.BuildRemovedBones > 0 ? "The build removes " + budget.BuildRemovedBones + " bone(s) of the custom base." : null));
            metrics.Add(Rated("PhysBones", budget.PhysBones, l => l.physBone.componentCount,
                budget.BuildPhysBones > 0 ? "The build adds " + budget.BuildPhysBones + " PhysBone(s) for the custom base." : null));
            metrics.Add(Rated("Contacts", budget.Contacts, l => l.contactCount, null));

            compression.text = !parameters.VrcFuryPresent ? "VRCFury is not installed." : "Parameters are estimated before compression. " + parameters.CompressionStatus;
        }

        private VisualElement Rated(string name, BudgetCount count, Func<AvatarPerformanceStatsLevel, int> stat, string note)
        {
            var rating = AvatarBudget.Rate(stat, count.Total);
            int poor = AvatarBudget.Limit(stat, PerformanceRating.Poor);
            var limits = new List<string>();
            foreach (var (r, limit) in AvatarBudget.Limits(stat)) limits.Add(RatingName(r) + " ≤ " + limit);
            string tooltip = "VRChat PC ranks: " + string.Join(" · ", limits) + ".";
            if (rating == PerformanceRating.VeryPoor && (name == "PhysBones" || name == "Contacts"))
                tooltip += " Players who hide Very Poor avatars see none of its PhysBones, colliders and contacts.";
            return Metric(name, count.Total, null, RatingName(rating), RatingClass(rating), tooltip, count, Mathf.Max(poor, count.Total), stat, false, note);
        }

        private VisualElement Metric(string name, int total, string unit, string chipText, string chipClass, string chipTooltip,
            BudgetCount split, int scale, Func<AvatarPerformanceStatsLevel, int> ticks, bool large, string note = null)
        {
            var metric = new VisualElement(); metric.AddToClassList("orb-avatar-budget__metric");
            metric.EnableInClassList("orb-avatar-budget__metric--large", large);
            var head = new VisualElement(); head.AddToClassList("orb-avatar-budget__head"); metric.Add(head);
            var title = new Label(name); title.AddToClassList("orb-avatar-budget__name"); head.Add(title);
            var value = new Label(total.ToString()); value.AddToClassList("orb-avatar-budget__value"); head.Add(value);
            if (!string.IsNullOrEmpty(unit)) { var u = new Label(unit); u.AddToClassList("orb-avatar-budget__unit"); head.Add(u); }
            var chip = new Label(chipText) { tooltip = chipTooltip }; chip.AddToClassList("orb-avatar-budget__chip");
            chip.AddToClassList("orb-avatar-budget__chip--" + chipClass); head.Add(chip);

            var track = new VisualElement(); track.AddToClassList("orb-avatar-budget__track"); metric.Add(track);
            track.tooltip = "Avatar " + split.Avatar + " · Custom base " + split.CustomBase;
            AddSegment(track, split.Avatar, scale, AvatarColor);
            AddSegment(track, split.CustomBase, scale, CustomBaseColor);
            if (ticks != null)
                foreach (var (_, limit) in AvatarBudget.Limits(ticks))
                {
                    if (limit <= 0 || limit >= scale) continue;
                    var tick = new VisualElement(); tick.AddToClassList("orb-avatar-budget__tick");
                    tick.style.left = new Length(100f * limit / scale, LengthUnit.Percent);
                    track.Add(tick);
                }
            if (!string.IsNullOrEmpty(note)) { var hint = new Label(note); hint.AddToClassList("orb-avatar-budget__hint"); metric.Add(hint); }
            return metric;
        }

        private static void AddSegment(VisualElement track, int value, int scale, Color color)
        {
            if (value <= 0 || scale <= 0) return;
            var segment = new VisualElement(); segment.AddToClassList("orb-avatar-budget__segment");
            segment.style.width = new Length(Mathf.Min(100f, 100f * value / scale), LengthUnit.Percent);
            segment.style.backgroundColor = color;
            track.Add(segment);
        }

        private static VisualElement Key(string text, Color color, string tooltip)
        {
            var row = new VisualElement { tooltip = tooltip }; row.AddToClassList("orb-avatar-budget__key");
            var swatch = new VisualElement(); swatch.AddToClassList("orb-avatar-budget__swatch"); swatch.style.backgroundColor = color; row.Add(swatch);
            var label = new Label(text); label.AddToClassList("orb-avatar-budget__key-label"); row.Add(label);
            return row;
        }

        private static string RatingName(PerformanceRating rating) => rating == PerformanceRating.VeryPoor ? "Very Poor" : rating.ToString();

        private static string RatingClass(PerformanceRating rating)
        {
            switch (rating)
            {
                case PerformanceRating.Excellent:
                case PerformanceRating.Good: return "good";
                case PerformanceRating.Medium: return "medium";
                case PerformanceRating.Poor: return "poor";
                default: return "very-poor";
            }
        }
    }
}
