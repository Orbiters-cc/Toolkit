using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// N4N0's logo for the tools' cards about N4N0: its four letters, light grey on transparent, centred in a 1024 × 1024
    /// picture (a power of two, one colour under the alpha: Unity's default import keeps its shape and clean edges).
    /// </summary>
    public static class N4N0Logo
    {
        public const string Path = "Packages/orbiters.toolkit/Editor/UI/N4N0Logo.png";

        public static Texture2D Texture => AssetDatabase.LoadAssetAtPath<Texture2D>(Path);

        /// <summary>The logo tinted <paramref name="colour"/>, fitted into its element.</summary>
        public static Image Image(Color colour)
        {
            var image = new Image { image = Texture, scaleMode = ScaleMode.ScaleToFit, tintColor = colour, pickingMode = PickingMode.Ignore };
            image.AddToClassList("orb-n4n0-logo");
            return image;
        }
    }
}
