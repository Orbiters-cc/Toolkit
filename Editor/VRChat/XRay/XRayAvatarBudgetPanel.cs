using Orbiters.Toolkit.Editor.XRay;
using Orbiters.Toolkit.XRay;
using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.Refit;
using Orbiters.Toolkit.Editor.VRChat.Budget;
using Orbiters.Toolkit.Editor.VRChat.Parameters;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;
using UnityEngine.UIElements;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;

namespace Orbiters.Toolkit.Editor.VRChat.XRay
{
    /// <summary>
    /// The selected avatar (or the scene's only one) against VRChat's limits, over the Scene view: synced parameters, as
    /// built when VRCFury compresses them, and the bones, PhysBones and contacts that decide its PC performance rank. Each
    /// bar shows the avatar's share and its custom base's share.
    /// </summary>
    [Overlay(typeof(SceneView), "Orbiters/XRayGizmos/AvatarBudget", "Avatar budget", true,
        defaultDockZone = DockZone.RightColumn, defaultDockPosition = DockPosition.Bottom, defaultLayout = Layout.Panel)]
    [Icon("Packages/orbiters.toolkit/Editor/XRay/UI/Icons/xray-bones.png")]
    internal sealed class XRayAvatarBudgetOverlay : Overlay
    {
        public override VisualElement CreatePanelContent() => new XRayAvatarBudgetPanel();
    }

    internal sealed class XRayAvatarBudgetPanel : VisualElement
    {
        private const string StyleSheetPath = "Packages/orbiters.toolkit/Editor/VRChat/XRay/avatar-budget.uss";
        private static readonly Color AvatarColor = new Color32(0xd8, 0xd8, 0xd8, 0xff), CustomBaseColor = new Color32(0x00, 0xda, 0x6d, 0xff),
            FaceTrackingColor = new Color32(0x8a, 0x7d, 0xff, 0xff);

        private readonly Func<VRCAvatarDescriptor> avatar;
        private readonly Label avatarName, empty, compression;
        private readonly VisualElement legend, metrics;
        private IVisualElementScheduledItem pending;
        private bool warned;

        /// <param name="avatar">The avatar to show; by default the selected one, else the scene's only active one.</param>
        public XRayAvatarBudgetPanel(Func<VRCAvatarDescriptor> avatar = null)
        {
            this.avatar = avatar ?? SelectedAvatar;
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet) styleSheets.Add(sheet);
            AddToClassList("orb-avatar-budget");
            avatarName = new Label(); avatarName.AddToClassList("orb-avatar-budget__avatar"); Add(avatarName);
            empty = new Label("Select an avatar to see its budget."); empty.AddToClassList("orb-avatar-budget__note"); Add(empty);
            legend = new VisualElement(); legend.AddToClassList("orb-avatar-budget__legend"); Add(legend);
            metrics = new VisualElement(); metrics.AddToClassList("orb-avatar-budget__metrics"); Add(metrics);
            compression = new Label(); compression.AddToClassList("orb-avatar-budget__note");
            compression.tooltip = "Change compression behavior from VRCFury's global settings.";
            Add(compression);

            // Toggles, controllers and bones change as the hierarchy is edited, and settings as components and assets are (e.g.
            // My Avatar leaving its default menu out of the upload): recount shortly after, not on every change. The first
            // count waits too: Unity restores overlays early in a reload, before the VRChat SDK is ready.
            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                Frame();
                EditorApplication.hierarchyChanged += ScheduleRefresh;
                ObjectChangeEvents.changesPublished += OnObjectsChanged;
                Undo.undoRedoPerformed += ScheduleRefresh;
                Selection.selectionChanged += ScheduleRefresh;
                CustomBases.Changed += OnCustomBaseChanged;
                ScheduleRefresh();
            });
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                EditorApplication.hierarchyChanged -= ScheduleRefresh;
                ObjectChangeEvents.changesPublished -= OnObjectsChanged;
                Undo.undoRedoPerformed -= ScheduleRefresh;
                Selection.selectionChanged -= ScheduleRefresh;
                CustomBases.Changed -= OnCustomBaseChanged;
            });
        }

        // A light, see-through frame: the scene stays visible behind the graphs.
        private void Frame()
        {
            for (var element = parent; element != null; element = element.parent)
                if (element.ClassListContains("unity-overlay"))
                {
                    element.style.backgroundColor = new Color(0.09f, 0.09f, 0.1f, 0.3f);
                    element.style.borderTopWidth = element.style.borderBottomWidth = element.style.borderLeftWidth = element.style.borderRightWidth = 0;
                    break;
                }
        }

        private void OnCustomBaseChanged(Transform _) => ScheduleRefresh();

        private void OnObjectsChanged(ref ObjectChangeEventStream _) => ScheduleRefresh();

        private void ScheduleRefresh() => ScheduleRefresh(400);

        private void ScheduleRefresh(long delayMs)
        {
            pending?.Pause();
            pending = schedule.Execute(Refresh).StartingIn(delayMs);
        }

        // The selected avatar, or the avatar of the selected object, else the scene's only active avatar.
        private static VRCAvatarDescriptor SelectedAvatar()
        {
            var selected = Selection.activeGameObject;
            var descriptor = selected != null && !EditorUtility.IsPersistent(selected) ? selected.GetComponentInParent<VRCAvatarDescriptor>(true) : null;
            if (descriptor != null) return descriptor;
            var all = UnityEngine.Object.FindObjectsOfType<VRCAvatarDescriptor>(false);
            return all.Length == 1 ? all[0] : null;
        }

        // A count that fails says so and tries again: the panel lives in the Scene view and must never break it.
        private void Refresh()
        {
            if (!AvatarBudget.Ready) { ScheduleRefresh(500); return; }
            try { Recount(); }
            catch (Exception ex)
            {
                foreach (var part in new VisualElement[] { avatarName, legend, metrics, compression }) part.style.display = DisplayStyle.None;
                empty.text = "Budget unavailable for now.";
                empty.style.display = DisplayStyle.Flex;
                if (!warned) Debug.LogWarning("[XRay Gizmos] Could not count the avatar budget: " + ex.Message);
                warned = true;
                ScheduleRefresh(2000);
            }
        }

        private void Recount()
        {
            empty.text = "Select an avatar to see its budget.";
            var shownAvatar = avatar();
            bool shown = shownAvatar != null;
            empty.style.display = shown ? DisplayStyle.None : DisplayStyle.Flex;
            foreach (var part in new VisualElement[] { avatarName, metrics }) part.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
            if (!shown) { legend.style.display = compression.style.display = DisplayStyle.None; return; }
            avatarName.text = shownAvatar.name;
            Show(AvatarBudget.Estimate(shownAvatar.gameObject));
        }

        private void Show(AvatarBudget budget)
        {
            legend.Clear(); metrics.Clear();
            bool custom = !string.IsNullOrEmpty(budget.CustomBase);
            // The colors only need a key when the bars split the avatar from a custom base or its face tracking.
            bool face = budget.Parameters.FaceTrackingBits > 0;
            legend.style.display = custom || face ? DisplayStyle.Flex : DisplayStyle.None;
            legend.Add(Key("Avatar", AvatarColor, "The avatar's own parameters, bones, PhysBones and contacts."));
            if (custom) legend.Add(Key("Custom base", CustomBaseColor, budget.CustomBase + ": what it adds to the avatar, as built."));
            if (face) legend.Add(Key("Face tracking", FaceTrackingColor, "The synced parameters of the face tracking template, with the features My Avatar leaves out removed."));

            var parameters = budget.Parameters;
            int max = ParameterBudget.MaxSyncedBits;
            metrics.Add(Metric("Parameters", parameters.BuiltBits, "/ " + max + " bits",
                parameters.OverBudget ? (parameters.BuiltBits - max) + " over" : parameters.Free + " free",
                parameters.OverBudget ? "very-poor" : "good",
                parameters.OverBudget ? "VRCFury cannot fit these parameters in 256 bits: the build will fail." : "Synced parameter memory left once built.",
                new BudgetCount(parameters.AvatarBits, parameters.CustomBaseBits, parameters.FaceTrackingBits), Mathf.Max(max, parameters.TotalBeforeCompression),
                parameters.TotalBeforeCompression > max ? new[] { max } : Array.Empty<int>(), true,
                parameters.Compresses
                    ? $"Compressed by VRCFury at build: {parameters.TotalBeforeCompression} → {parameters.CompressedBits} bits, {parameters.CompressedParameters} parameters in a {parameters.SyncSeconds:0.#} s sync."
                    : null));
            metrics.Add(Rated("Bones", budget.Bones, l => l.boneCount,
                budget.BuildRemovedBones > 0 ? "The build removes " + budget.BuildRemovedBones + " bone(s) of the custom base." : null));
            metrics.Add(Rated("PhysBones", budget.PhysBones, l => l.physBone.componentCount,
                budget.BuildPhysBones > 0 ? "The build adds " + budget.BuildPhysBones + " PhysBone(s) for the custom base." : null));
            metrics.Add(Rated("Contacts", budget.Contacts, l => l.contactCount, null));

            // Only over the limit, when VRCFury will not compress: the Parameters hint covers compression.
            bool uncompressed = parameters.TotalBeforeCompression > max && !parameters.Compresses;
            compression.style.display = uncompressed ? DisplayStyle.Flex : DisplayStyle.None;
            compression.text = !parameters.VrcFuryPresent ? "VRCFury is not installed." : parameters.CompressionStatus;
        }

        private VisualElement Rated(string name, BudgetCount count, Func<AvatarPerformanceStatsLevel, int> stat, string note)
        {
            var rating = AvatarBudget.Rate(stat, count.Total);
            int poor = AvatarBudget.Limit(stat, PerformanceRating.Poor);
            var limits = AvatarBudget.Limits(stat).ToList();
            string tooltip = "VRChat PC ranks: " + string.Join(" · ", limits.Select(l => RatingName(l.rating) + " ≤ " + l.limit)) + ".";
            if (rating == PerformanceRating.VeryPoor && (name == "PhysBones" || name == "Contacts"))
                tooltip += " Players who hide Very Poor avatars see none of its PhysBones, colliders and contacts.";
            return Metric(name, count.Total, null, RatingName(rating), RatingClass(rating), tooltip, count, Mathf.Max(poor, count.Total),
                limits.Select(l => l.limit), false, note);
        }

        private VisualElement Metric(string name, int total, string unit, string chipText, string chipClass, string chipTooltip,
            BudgetCount split, int scale, IEnumerable<int> ticks, bool large, string note)
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
            track.tooltip = "Avatar " + split.Avatar + " · Custom base " + split.CustomBase + (split.FaceTracking > 0 ? " · Face tracking " + split.FaceTracking : string.Empty);
            AddSegment(track, split.Avatar, scale, AvatarColor);
            AddSegment(track, split.CustomBase, scale, CustomBaseColor);
            AddSegment(track, split.FaceTracking, scale, FaceTrackingColor);
            foreach (int limit in ticks)
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
