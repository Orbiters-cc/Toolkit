#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>One Inspector chrome and layout contract for Orbiters component tools.</summary>
public sealed class OrbitersInspectorShell : VisualElement
{
    public OrbitersGlowSurfaceElement Glow { get; }
    public VisualElement Header { get; }
    public VisualElement Banner { get; }
    public VisualElement Account { get; }
    public OrbitersInspectorShell()
    {
        AddToClassList("mcb-root");
        foreach (string name in new[] { "theme", "chrome", "account", "notice" })
        {
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/orbiters.toolkit/Runtime/EditorServices/" + name + ".uss");
            if (sheet) styleSheets.Add(sheet);
        }
        Glow = new OrbitersGlowSurfaceElement(new Color(.180f,.180f,.180f,1), new Color(.212f,.212f,.212f,1), 254, -78, 318);
        Glow.AddToClassList("mcb-chrome-surface"); Add(Glow);
        Header = new VisualElement(); Header.AddToClassList("mcb-header"); Add(Header);
        Banner = new VisualElement(); Banner.AddToClassList("mcb-banner"); Header.Add(Banner);
        Account = new VisualElement(); Header.Add(Account);
        Glow.SendToBack();
        RegisterCallback<PointerMoveEvent>(_ => Glow.WakeForSeconds(20));
    }
}
#endif
