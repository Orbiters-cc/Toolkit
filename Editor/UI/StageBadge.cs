using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>A small pill naming how mature a feature is ("beta", "alpha", "experimental"); empty and hidden for stable.</summary>
    public sealed class StageBadge : Label
    {
        private const string StyleSheetPath = "Packages/orbiters.toolkit/Editor/UI/stage-badge.uss";
        private static GUIStyle guiStyle;

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

        // ---- The same pill for IMGUI (stage-badge.uss's colours), e.g. after a toggle's label -----------------------------

        private static GUIStyle GuiStyle => guiStyle ??= new GUIStyle(EditorStyles.miniBoldLabel)
        {
            fontSize = 10,
            alignment = TextAnchor.MiddleCenter,
            padding = new RectOffset(0, 0, 0, 1),
            margin = new RectOffset(0, 0, 0, 0)
        };

        public const float GuiHeight = 15f;

        /// <summary>The width <see cref="DrawGUI"/> needs for this stage; 0 for stable.</summary>
        public static float GuiWidth(FeatureStage stage) =>
            stage == FeatureStage.Stable ? 0f : GuiStyle.CalcSize(new GUIContent(Name(stage))).x + 12f;

        /// <summary>Draws the pill in <paramref name="rect"/> (height <see cref="GuiHeight"/>); nothing for stable.</summary>
        public static void DrawGUI(Rect rect, FeatureStage stage)
        {
            if (stage == FeatureStage.Stable || Event.current == null || Event.current.type != EventType.Repaint) return;
            Color text, back;
            switch (stage)
            {
                case FeatureStage.Alpha:
                    text = new Color32(138, 125, 255, 255);
                    back = new Color32(138, 125, 255, 36);
                    break;
                case FeatureStage.Experimental:
                    text = new Color32(255, 176, 32, 255);
                    back = new Color32(255, 176, 32, 36);
                    break;
                default:
                    text = new Color32(0, 218, 109, 255);
                    back = new Color32(0, 218, 109, 31);
                    break;
            }
            GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, back, 0f, rect.height * 0.5f);
            var style = GuiStyle;
            style.normal.textColor = text;
            GUI.Label(rect, Name(stage), style);
        }

        private static string Name(FeatureStage stage) => stage.ToString().ToLowerInvariant();
    }
}
