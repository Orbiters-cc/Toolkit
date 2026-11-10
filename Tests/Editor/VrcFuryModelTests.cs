using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat;

// TKV-5: the VRCFury model members the Toolkit reads by name resolve on the installed VRCFury. A renamed field would
// otherwise read as null and count as nothing (a budget of zero parameters).
public sealed class VrcFuryModelTests
{
    private static FieldInfo FieldOf(Type type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var field = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field != null) return field;
        }
        return null;
    }

    private static Type ElementOf(FieldInfo list) =>
        list.FieldType.IsArray ? list.FieldType.GetElementType() : list.FieldType.GetGenericArguments().FirstOrDefault();

    [Test] public void EveryMemberTheBudgetReadsResolves()
    {
        if (!VrcFury.Installed) Assert.Ignore("VRCFury is not installed.");
        Assert.That(VrcFury.Component, Is.Not.Null, "VF.Model.VRCFury");
        Assert.That(FieldOf(VrcFury.Component, "content"), Is.Not.Null, "VRCFury.content");
        var toggle = VrcFury.Find("VF.Model.Feature.Toggle");
        var full = VrcFury.Find("VF.Model.Feature.FullController");
        Assert.That(toggle, Is.Not.Null, "VF.Model.Feature.Toggle");
        Assert.That(full, Is.Not.Null, "VF.Model.Feature.FullController");
        foreach (string name in new[] { "slider", "useInt", "holdButton", "state" })
            Assert.That(FieldOf(toggle, name), Is.Not.Null, "Toggle." + name);
        foreach (string name in new[] { "menus", "prms", "globalParams" })
            Assert.That(FieldOf(full, name), Is.Not.Null, "FullController." + name);
        var menus = FieldOf(full, "menus");
        var prms = FieldOf(full, "prms");
        Assert.That(typeof(IEnumerable).IsAssignableFrom(menus.FieldType) && FieldOf(ElementOf(menus), "menu") != null, Is.True, "FullController.menus[].menu");
        Assert.That(typeof(IEnumerable).IsAssignableFrom(prms.FieldType) && FieldOf(ElementOf(prms), "parameters") != null, Is.True, "FullController.prms[].parameters");
        Assert.That(VrcFury.Find("VF.Menu.CompressorMenuItem")?.GetMethod("Get"), Is.Not.Null, "the parameter compressor's setting");
    }
}
