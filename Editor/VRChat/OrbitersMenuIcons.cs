using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// The icons Orbiters gives VRChat menu items that have none of their own: a white dot for toggles and buttons, a gauge
    /// for sliders, a folder for submenus and an arrow for "Next" pages. They are 64×64 pictures written once per project into
    /// <see cref="Folder"/>: Toolkit's own files get new GUIDs in every project, so menus, avatars and gallery packages
    /// refer to these copies instead. A picture replaced there is kept.
    /// </summary>
    public static class OrbitersMenuIcons
    {
        public const string Folder = "Assets/Orbiters/Menu Icons";

        /// <summary>A white dot: toggles and buttons.</summary>
        public static Texture2D Toggle => Get("Toggle", DotPng);
        /// <summary>A white gauge: sliders (radial puppets).</summary>
        public static Texture2D Slider => Get("Slider", SliderPng);
        /// <summary>A white folder: submenus.</summary>
        public static Texture2D Submenu => Get("Folder", FolderPng);
        /// <summary>A white arrow: the "Next" page VRCFury (or "More", Modular Avatar) adds to a menu of more than 8 items.</summary>
        public static Texture2D Next => Get("Next", NextPng);

        /// <summary>Whether <paramref name="icon"/> is one of these.</summary>
        public static bool Is(Texture2D icon) => icon != null && AssetDatabase.GetAssetPath(icon).StartsWith(Folder + "/", StringComparison.Ordinal);

        private static Texture2D Get(string name, string png)
        {
            string path = Folder + "/" + name + ".png";
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (texture != null) return texture;
            string parent = "Assets";
            foreach (var part in Folder.Substring("Assets/".Length).Split('/'))
            {
                if (!AssetDatabase.IsValidFolder(parent + "/" + part)) AssetDatabase.CreateFolder(parent, part);
                parent += "/" + part;
            }
            File.WriteAllBytes(path, Convert.FromBase64String(png));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            // VRChat's menu draws icons over itself: alpha is transparency, no mipmaps, compressed.
            if (AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                importer.textureType = TextureImporterType.Default;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.maxTextureSize = 64;
                importer.textureCompression = TextureImporterCompression.Compressed;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // 64×64 white pictures, drawn from Orbiters' menu icon designs.
        private const string DotPng = "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAABBklEQVR42u3ZvQ2DMBCGYQoG8BAegdJDsY0LCoajdElJcblIV6RKCDFwkPekr0FgiUcY/zUi0vxzGgAAAAAAAAAAAAAAAAAAAAAAAACAndNqkqbXZM1oyXYt2T23A4j2kkU+V7F74x0AgmbQLPJ9LfZsuCpAp5nk95qsrUsBPPvyLPVqtjYvAdBVfvlXhM47QKj02b/rDsEzwCD71+AVIG78228ZHaJHgCzHVfYG0K6c5NSqUmvGWHPYO7qSJ4D+BIDeE0A+ASB7AhhPABgBoAvwE2QYZCLEVJjFEMthNkTYEmNTlG1xDkY4GuNwlONxAAAAAAAAAAAAAAAAAAAAAAAAAIA1eQA8tnNXI1Ww4QAAAABJRU5ErkJggg==";
        private const string FolderPng = "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAABNElEQVR42u3aIQ7CMBTG8QkOMMEhJpEcYEdAcgjk5G6wA0xMTiA5AodAIBDISQQC8XhLiiFsoQslXfv/kk+RkPWXljXhJSKSxNwEAAAAAAAAAAAY61Jbao/ai9jlqj1ot9rFHAFybSe/yUm7mhPAWvuQ3+Zmvtd7gH67nsVNruZYeQ2Qi9vsfQcoxH02PgPUfwDofDgKQx808p/sYwdwkYu5s5Tf7LAQAd6PWR4zQJ/72N0jBoDXLXQRM4AMHYWYAIrYAerYARoAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAIYAqQIDKBmAXIMDOBiALECATyyGpNqDFtzJhSqz/b/0UwOL7NaQycU4wnflOaMcWLxajspn5EanM68TnVuZZM2FWGAAAAAAAAAAAAAAAAAAA4HOfLDqWMt0n1d0AAAAASUVORK5CYII=";
        private const string SliderPng = "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAADNUlEQVR42u2arZPiMBTAERUIBGIFAoFYUYE4sfIEYsX9AYgKxAoEogKx8gQzSATiBOIEckUFAnFiRQUCsWIFAnFiBeLECQTiREUunXmd6WTz1TZpwpA382YYJn3J++XrvSQNhFDjlrXhADgADoAD4AA4AA5A3ephXWK9gC7hv5sBMEGfZXJLANYUAOtrBpAO32esEdbvWFsaALTAdgR1eTYBIB2KNQCIdYwYFc63sSYUh8YKAYwp5ROo2ziAO0SXv5wGFgHQBls0ubNlCrwyGrhglB9Ryo4YZRcM2682rQF9xjT4x1ms0oXsAPrMsX1mDP++bdvgktHQZkW7F4rdpY1xQDpXT0RDfzLK+lgHWIdYA/jdY5RdETZPKhY/XYFQF3onhj3byy2UU6y/GD2ayRn2+VFu5HhgKwbb3boDIQ96aQbbUZEApIP1B6wFReUMdbYLBmQh7CihTFtljL4QDTtifZT4LhT0tqz8gakiqu8R2paXl6oA+pyGbaCHaSHrFqmXOWeUbTjf9asAeBA06o3i/A7pkS1jyL8LvnuoAkCmgm6urC7ndwA3reNrgQ56F60DsgsZb4h1GNuVDucjYjqUmaKlt0HeIvOtRuczGVRcpEvFAR5shTPYqz3Q3zU7nzmaDe+y27SSQGhkwHlRAlUrgL0h5xHUbRRAz6DzmdybBDAx7Hzl02RRdtcjtCnI1MrKoaTztIyzSWl3uyiAFeOA40IQjxUB2ENAFZUcOfkReWGcS6xkAQSCCpNc7v6BzMtHbj1KBGUDGQAziUqHFgIYSpSdyQAYSxjyLQTgS5QdywDwOOlsQqSlOwsA5NeAOWcabGlRIm8XyM7t8tqRON+vW9aU5I1st68rDphaAGBqMhC6twCA0UiwAUGMKTnYkAyFBgGENgBoUi5E6pCTglsnZRcjgQEAgW03Q1GNzkfIwquxFuXMUIcckfgJjrG7wY5mCEeZk16TADIIew3O71U7rwtAlk8sJNJTGUnAlpaHlLrf4X0RXKqIZAM2rv6hpA+Z2lFyns95Ccw1AiBflaUZ2hMcYU3g9wApePV1DQDca3EHwAFwABwAB8ABcAAcAAfAAv0PpCZawJDi5K8AAAAASUVORK5CYII=";
        private const string NextPng = "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAABnklEQVR42u3bIW4CQRQG4JUVFT0GsqICUYHkABWIiooKBAeobIJAcICKiooeopJDVFRwgAoEAol4nUnXQEICs//M/v/MvOTZR/6PDDNkdhsza0rupgJUgApQAYB95Xro+tH1XWkAU9cbO6y163EJAK92uvauH3IGGLQhTREBMWRh5xUlAmLIp51fdAiIIe92WVEhIIY82+VFg4Da+39UEVCD/E7wq4iAHCaJgB4ohxBjqBRCrMEyCDGHSyDE/gB6hBTK1Aip1hotQspfXEqE1PsuHUIfpy8qhL7O4DQIff4T64LwlMu9QCiCr3kuFyNdEG5zuRkKRXjL6WosBGGVE8C9612pACHhfS1zAAgNv2+XjTRAaHhfM/VtsMs3P1E/CHUJL38UpgnfBwBV+NQAdOFTAlCGTwVAGz4FAHX42AD04WMCSISPBSATPgaAVHg0gFx4JIBkeBTAjerzQSiAl9Ifk/tSDY8CWKmGRwF8qIZHAYxUwyO3waViePRByO8G26Pw3/b/DlExL01dt0tigri4zOUpsQpQASpABaDtPzgMgnsFWuGgAAAAAElFTkSuQmCC";
    }
}
