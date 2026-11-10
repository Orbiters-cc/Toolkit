using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using Orbiters.Toolkit;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.Storage;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

// XPK-19, XPK-20, XPK-27, XPK-25: the one version of helpers the packages had each written, against the forms they replace.
public sealed class SharedHelpersTests
{
    [Test] public void HierarchyPathsMatchUnitysTransformPaths()
    {
        var root = new GameObject("Root");
        try
        {
            var a = new GameObject("A").transform; a.SetParent(root.transform);
            var b = new GameObject("B b").transform; b.SetParent(a);
            var c = new GameObject("C").transform; c.SetParent(b);
            var twin = new GameObject("A").transform; twin.SetParent(root.transform);
            var outside = new GameObject("Outside");
            try
            {
                foreach (var t in new[] { root.transform, a, b, c, twin })
                {
                    Assert.That(HierarchyPaths.Under(root.transform, t), Is.EqualTo(AnimationUtility.CalculateTransformPath(t, root.transform)), t.name);
                    Assert.That(HierarchyPaths.Find(root.transform, HierarchyPaths.Under(root.transform, t)), Is.SameAs(t == twin ? a : t), "the first of each name, as Transform.Find");
                }
                Assert.That(HierarchyPaths.Under(b, c), Is.EqualTo("C"));
                Assert.That(HierarchyPaths.Under(c, b), Is.Null, "an ancestor is outside");
                Assert.That(HierarchyPaths.Under(root.transform, outside.transform), Is.Null);
                Assert.That(HierarchyPaths.Under(null, c), Is.Null);
                Assert.That(HierarchyPaths.WithRoot(root.transform, c), Is.EqualTo("Root/A/B b/C"));
                Assert.That(HierarchyPaths.WithRoot(root.transform, root.transform), Is.EqualTo("Root"));
                Assert.That(HierarchyPaths.Full(c), Is.EqualTo("Root/A/B b/C"));
                Assert.That(HierarchyPaths.Depth(c), Is.EqualTo(4));
                Assert.That(HierarchyPaths.Depth(null), Is.Zero);
            }
            finally { Object.DestroyImmediate(outside); }
        }
        finally { Object.DestroyImmediate(root); }
    }

    [Test] public void HashingMatchesTheHexTheToolsWrote()
    {
        foreach (string text in new[] { "", "abc", "Ā ünïcode", new string('x', 10000) })
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            string old;
            using (var sha = SHA256.Create()) old = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            Assert.That(Hashing.Sha256(text), Is.EqualTo(old));
            Assert.That(Hashing.Sha256(bytes), Is.EqualTo(old));
            using (var stream = new MemoryStream(bytes)) Assert.That(Hashing.Sha256(stream), Is.EqualTo(old));
        }
        Assert.That(Hashing.Hex(new byte[] { 0, 15, 16, 255 }), Is.EqualTo("000f10ff"));
        string file = Path.Combine(Path.GetTempPath(), "OrbitersHash-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(file, "content");
            Assert.That(Hashing.Sha256File(file), Is.EqualTo(Hashing.Sha256("content")));
        }
        finally { File.Delete(file); }
    }

    [Test] public void PrefsKeepTheirScopeAndDefault()
    {
        string key = "Orbiters.Tests.Pref." + Guid.NewGuid().ToString("N");
        var user = OrbitersPrefs.User(key, 3);
        var project = OrbitersPrefs.Project(key, "none");
        var session = OrbitersPrefs.Session(key, true);
        try
        {
            Assert.That(user.Value, Is.EqualTo(3));
            Assert.That(project.Value, Is.EqualTo("none"));
            user.Value = 7;
            project.Value = "this one";
            session.Value = false;
            Assert.That(OrbitersPrefs.User(key, 3).Value, Is.EqualTo(7));
            Assert.That(EditorPrefs.GetInt(key), Is.EqualTo(7));
            Assert.That(EditorPrefs.GetString(OrbitersPrefs.ProjectKey(key)), Is.EqualTo("this one"));
            Assert.That(OrbitersPrefs.ProjectKey(key), Is.Not.EqualTo(key), "a project setting is not every project's");
            Assert.That(OrbitersPrefs.Session(key, true).Value, Is.False);
            Assert.Throws<NotSupportedException>(() => OrbitersPrefs.User(key, 1.0));
        }
        finally
        {
            user.Reset();
            project.Reset();
            session.Reset();
        }
        Assert.That(user.Value, Is.EqualTo(3));
        Assert.That(EditorPrefs.HasKey(OrbitersPrefs.ProjectKey(key)), Is.False);
        Assert.That(session.Value, Is.True);
    }

    [Test] public void ADebouncerRunsOnceAfterTheLastRequest()
    {
        int runs = 0;
        using (var debouncer = new Debouncer("Test debouncer", 3600, () => runs++))
        {
            debouncer.Request();
            debouncer.Request();
            Assert.That(debouncer.Pending, Is.True);
            Assert.That(runs, Is.Zero, "waits for quiet");
            debouncer.Flush();
            Assert.That(runs, Is.EqualTo(1));
            Assert.That(debouncer.Pending, Is.False);
            debouncer.Flush();
            Assert.That(runs, Is.EqualTo(1), "nothing pending");
            debouncer.Request();
        }
        Assert.That(runs, Is.EqualTo(1), "disposed: the pending run is dropped");
    }

    [Test] public void AssetPathsFindFilesOnDisk()
    {
        Assert.That(AssetPaths.FullPath("Assets"), Is.EqualTo(Path.GetFullPath(Application.dataPath)));
        Assert.That(AssetPaths.FullPath("ProjectSettings/ProjectVersion.txt"), Is.EqualTo(Path.Combine(ProjectPaths.ProjectSettings, "ProjectVersion.txt")));
        var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(HierarchyPaths).Assembly);
        if (package != null)
            Assert.That(AssetPaths.FullPath(package.assetPath + "/package.json"), Is.EqualTo(Path.GetFullPath(Path.Combine(package.resolvedPath, "package.json"))));
        Assert.That(ProjectPaths.OrbitersLibrary, Is.EqualTo(Path.Combine(ProjectPaths.Root, "Library", "Orbiters")));
    }
}
