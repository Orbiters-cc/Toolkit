using NUnit.Framework;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine.UIElements;

/// <summary>The UI kit's stylesheets: the sheet the asset database has, loaded once, attached once per element.</summary>
public sealed class UiStylesTests
{
    private const string Path = "Packages/orbiters.toolkit/Editor/UI/toggle-switch.uss";

    [Test]
    public void LoadsTheAssetOnceAndAttachesItOnce()
    {
        var sheet = UiStyles.Load(Path);
        Assert.That(sheet, Is.SameAs(AssetDatabase.LoadAssetAtPath<StyleSheet>(Path)));
        Assert.That(UiStyles.Load(Path), Is.SameAs(sheet));
        Assert.That(UiStyles.Load("Packages/orbiters.toolkit/Editor/UI/none.uss"), Is.Null);

        var element = new VisualElement();
        UiStyles.Attach(element, Path);
        UiStyles.Attach(element, Path);
        Assert.That(element.styleSheets.count, Is.EqualTo(1));
        Assert.That(new ToggleSwitch(false, null).styleSheets.Contains(sheet), Is.True, "the controls attach their sheet as before");
    }
}
