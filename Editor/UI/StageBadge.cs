using UnityEditor;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>A small pill naming how mature a feature is ("beta", "alpha", "experimental"); empty and hidden for stable.</summary>
    public sealed class StageBadge : Label
    {
        private const string StyleSheetPath = "Packages/orbiters.toolkit/Editor/UI/stage-badge.uss";

        public FeatureStage Stage { get; private set; }

        public StageBadge(FeatureStage stage)
        {
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet) styleSheets.Add(sheet);
            AddToClassList("orb-stage");
            pickingMode = PickingMode.Ignore;
            SetStage(stage);
        }

        public void SetStage(FeatureStage stage)
        {
            RemoveFromClassList("orb-stage--" + Name(Stage));
            Stage = stage;
            text = stage == FeatureStage.Stable ? string.Empty : Name(stage);
            AddToClassList("orb-stage--" + Name(stage));
            style.display = stage == FeatureStage.Stable ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private static string Name(FeatureStage stage) => stage.ToString().ToLowerInvariant();
    }
}
