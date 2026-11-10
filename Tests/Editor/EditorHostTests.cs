using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat.Checks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

// The editor host's timetable (budget, order, idle, one-shots), its change bus (one value per frame, decoded once) and
// the avatar signature measuring again only what a change touched.
public sealed class EditorHostTests
{
    // ---- Timetable, on a clock of its own ----

    [Test] public void TasksDueRunMostOverdueFirstUntilTheBudgetIsSpent()
    {
        var scheduler = new HostScheduler { BudgetMilliseconds = 4 };
        var ran = new List<string>();
        double spent = 0;
        // Each task "takes" 3 ms.
        scheduler.Add("late", 1, 5, HostWhen.Always, false, () => { ran.Add("late"); spent += 3; });
        scheduler.Add("later", 1, 2, HostWhen.Always, false, () => { ran.Add("later"); spent += 3; });
        scheduler.Add("latest", 1, 1, HostWhen.Always, false, () => { ran.Add("latest"); spent += 3; });

        Assert.AreEqual(2, scheduler.Run(10, true, () => spent), "the budget stops the frame after the task that spent it");
        CollectionAssert.AreEqual(new[] { "latest", "later" }, ran);

        ran.Clear(); spent = 0;
        Assert.AreEqual(1, scheduler.Run(10.1, true, () => spent), "the one left waits for the next frame, not for its next interval");
        CollectionAssert.AreEqual(new[] { "late" }, ran);
    }

    [Test] public void AtLeastOneTaskRunsPerFrameEvenOverBudget()
    {
        var scheduler = new HostScheduler { BudgetMilliseconds = 1 };
        int runs = 0;
        scheduler.Add("slow", 0, 0, HostWhen.Always, false, () => runs++);
        Assert.AreEqual(1, scheduler.Run(1, true, () => 100));
        Assert.AreEqual(1, runs);
    }

    [Test] public void APeriodicTaskThatFellBehindRunsOnceNotOncePerIntervalMissed()
    {
        var scheduler = new HostScheduler();
        int runs = 0;
        scheduler.Add("every second", 1, 1, HostWhen.Always, false, () => runs++);
        scheduler.Run(10, true, () => 0);
        scheduler.Run(10.5, true, () => 0);
        Assert.AreEqual(1, runs, "due again one interval after it ran");
        scheduler.Run(11, true, () => 0);
        Assert.AreEqual(2, runs);
    }

    [Test] public void IdleTasksWaitWhileTheEditorIsBusy()
    {
        var scheduler = new HostScheduler();
        int idle = 0, always = 0;
        scheduler.Add("idle", 0, 0, HostWhen.Idle, false, () => idle++);
        scheduler.Add("always", 0, 0, HostWhen.Always, false, () => always++);
        scheduler.Run(1, false, () => 0);
        Assert.AreEqual((0, 1), (idle, always));
        scheduler.Run(2, true, () => 0);
        Assert.AreEqual((1, 2), (idle, always));
    }

    [Test] public void OneShotsRunOnceAndDisposedTasksNever()
    {
        var scheduler = new HostScheduler();
        int once = 0, stopped = 0;
        scheduler.Add("once", 0, 0, HostWhen.Always, true, () => once++);
        var task = scheduler.Add("stopped", 0, 0, HostWhen.Always, false, () => stopped++);
        task.Dispose();
        scheduler.Run(1, true, () => 0);
        scheduler.Run(2, true, () => 0);
        Assert.AreEqual((1, 0), (once, stopped));
        Assert.AreEqual(0, scheduler.Count);
    }

    [Test] public void ATaskThatThrowsIsReportedAndTheOthersStillRun()
    {
        var failed = new List<string>();
        var scheduler = new HostScheduler { Failed = (name, _) => failed.Add(name) };
        int runs = 0;
        scheduler.Add("broken", 0, 0, HostWhen.Always, false, () => throw new InvalidOperationException("test"));
        scheduler.Add("fine", 0, 0, HostWhen.Always, false, () => runs++);
        scheduler.Run(1, true, () => 0);
        CollectionAssert.AreEqual(new[] { "broken" }, failed);
        Assert.AreEqual(1, runs);
    }

    [Test] public void TimingsAddUpByName()
    {
        var scheduler = new HostScheduler { BudgetMilliseconds = 1000 };
        double clock = 0;
        scheduler.Add("one-shot", 0, 0, HostWhen.Always, true, () => clock += 2);
        scheduler.Add("one-shot", 0, 0, HostWhen.Always, true, () => clock += 3);
        scheduler.Run(1, true, () => clock);
        var timing = scheduler.Timings().Single();
        Assert.AreEqual("one-shot", timing.Name);
        Assert.AreEqual(2, timing.Runs);
        Assert.AreEqual(5, timing.TotalMilliseconds, 1e-9);
        Assert.AreEqual(3, timing.LongestMilliseconds, 1e-9);
    }

    // ---- Change bus ----

    [Test] public void TheBusDeliversOneValuePerFlushWithEverythingReportedSince()
    {
        var seen = new List<EditorChanges>();
        void Listen(EditorChanges changes) => seen.Add(changes);
        EditorChangeBus.Flush();
        EditorChangeBus.Changed += Listen;
        try
        {
            EditorChangeBus.Report(c => { c.AddProperties(11); c.AddProperties(11); c.AddStructure(12); });
            EditorChangeBus.Report(c => c.AddDestroyed(13));
            EditorChangeBus.Flush();
            EditorChangeBus.Flush();
            var mine = seen.Where(c => c.Properties.Contains(11)).ToList();
            Assert.AreEqual(1, mine.Count, "coalesced, and nothing delivered when nothing changed");
            var value = mine[0];
            CollectionAssert.AreEquivalent(new[] { 11 }, value.Properties);
            CollectionAssert.AreEquivalent(new[] { 12 }, value.Structure);
            CollectionAssert.AreEquivalent(new[] { 13 }, value.Destroyed);
            Assert.That(value.Subtrees, Does.Contain(13), "a destroyed hierarchy is a changed subtree too");
            Assert.True(value.ObjectsChanged && value.StructureChanged && !value.AssetsChanged);
        }
        finally { EditorChangeBus.Changed -= Listen; }
    }

    [Test] public void TouchesTellsWhetherAChangeIsUnderARoot()
    {
        var scene = EditorSceneManager.NewPreviewScene();
        try
        {
            var root = new GameObject("Root");
            var child = new GameObject("Child");
            var other = new GameObject("Other");
            SceneManager.MoveGameObjectToScene(root, scene);
            SceneManager.MoveGameObjectToScene(other, scene);
            child.transform.SetParent(root.transform);
            var collider = child.AddComponent<BoxCollider>();

            var changes = new EditorChanges();
            changes.AddProperties(collider.GetInstanceID());
            Assert.True(changes.Touches(root.transform));
            Assert.False(changes.Touches(other.transform));
            var everything = new EditorChanges();
            everything.SetUndoRedo();
            Assert.True(everything.Touches(other.transform), "an Undo may have changed anything");
        }
        finally { EditorSceneManager.ClosePreviewScene(scene); }
    }

    // ---- Avatar signature on the bus ----

    [Test] public void ASignatureMeasuresAgainOnlyTheObjectThatChanged()
    {
        var scene = EditorSceneManager.NewPreviewScene();
        try
        {
            var root = new GameObject("Avatar");
            SceneManager.MoveGameObjectToScene(root, scene);
            var colliders = new List<BoxCollider>();
            for (int i = 0; i < 40; i++)
            {
                var child = new GameObject("Part " + i);
                child.transform.SetParent(root.transform);
                colliders.Add(child.AddComponent<BoxCollider>());
            }
            EditorChangeBus.Flush();
            string first = AvatarSignature.Of(root);
            Assert.AreEqual(first, AvatarSignature.Of(root), "nothing reported, nothing measured");

            int before = AvatarSignature.MeasuredParts;
            var changed = colliders[7];
            changed.size = new Vector3(2, 2, 2);
            EditorUtility.SetDirty(changed);
            EditorChangeBus.Report(c => c.AddProperties(changed.GetInstanceID()));
            string second = AvatarSignature.Of(root);
            Assert.AreNotEqual(first, second);
            Assert.AreEqual(1, AvatarSignature.MeasuredParts - before, "only the changed object's GameObject");

            // Measuring everything again gives the value the changes added up to.
            EditorChangeBus.Report(c => c.SetEverything());
            Assert.AreEqual(second, AvatarSignature.Of(root));

            changed.size = Vector3.one;
            EditorUtility.SetDirty(changed);
            EditorChangeBus.Report(c => c.AddProperties(changed.GetInstanceID()));
            Assert.AreEqual(first, AvatarSignature.Of(root), "contents are compared: put back, the value is the first again");
        }
        finally { EditorSceneManager.ClosePreviewScene(scene); }
    }

    [Test] public void ASignatureSeesObjectsAddedAndRemoved()
    {
        var scene = EditorSceneManager.NewPreviewScene();
        try
        {
            var root = new GameObject("Avatar");
            SceneManager.MoveGameObjectToScene(root, scene);
            var kept = new GameObject("Kept");
            kept.transform.SetParent(root.transform);
            EditorChangeBus.Flush();
            string first = AvatarSignature.Of(root);

            var added = new GameObject("Added");
            added.transform.SetParent(root.transform);
            EditorChangeBus.Report(c => c.AddSubtree(added.GetInstanceID()));
            string second = AvatarSignature.Of(root);
            Assert.AreNotEqual(first, second);

            int id = added.GetInstanceID();
            Object.DestroyImmediate(added);
            EditorChangeBus.Report(c => { c.AddDestroyed(id); c.AddSubtree(root.GetInstanceID()); });
            Assert.AreEqual(first, AvatarSignature.Of(root), "the removed object's share is taken out");
        }
        finally { EditorSceneManager.ClosePreviewScene(scene); }
    }

    // Review of TKV-26: after each kind of change the editor reports, the value kept up to date equals one measured from
    // nothing; a rewritten subtree (a prefab update, a hierarchy's structure) counts for everything below it.
    [Test] public void EachKindOfChangeGivesTheValueOfAFreshMeasure()
    {
        var scene = EditorSceneManager.NewPreviewScene();
        try
        {
            var root = new GameObject("Avatar");
            SceneManager.MoveGameObjectToScene(root, scene);
            var child = new GameObject("Child");
            child.transform.SetParent(root.transform);
            var grandchild = new GameObject("Grandchild");
            grandchild.transform.SetParent(child.transform);
            var collider = grandchild.AddComponent<BoxCollider>();
            var other = new GameObject("Other");
            other.transform.SetParent(root.transform);
            EditorChangeBus.Flush();
            AvatarSignature.Of(root);

            void Check(string what, Action<EditorChanges> report)
            {
                EditorChangeBus.Report(report);
                string kept = AvatarSignature.Of(root);
                AvatarSignature.Forget(root);
                Assert.AreEqual(AvatarSignature.Of(root), kept, what);
            }
            void Edit(float size) { collider.size = Vector3.one * size; EditorUtility.SetDirty(collider); }

            Edit(2);
            Check("properties", c => c.AddProperties(collider.GetInstanceID()));
            grandchild.AddComponent<SphereCollider>();
            Check("structure", c => c.AddStructure(grandchild.GetInstanceID()));
            Edit(3);
            grandchild.AddComponent<CapsuleCollider>();
            Check("a rewritten subtree, reported on its root", c => c.AddRewritten(child.GetInstanceID()));
            Edit(4);
            Check("the avatar itself rewritten", c => c.AddRewritten(root.GetInstanceID()));
            grandchild.transform.SetParent(other.transform);
            Check("parent", c => { c.AddSubtree(grandchild.GetInstanceID()); c.AddSubtree(child.GetInstanceID()); c.AddSubtree(other.GetInstanceID()); });
            var made = new GameObject("Made");
            made.AddComponent<BoxCollider>();
            made.transform.SetParent(child.transform);
            Check("created", c => c.AddSubtree(made.GetInstanceID()));
            other.transform.SetSiblingIndex(0);
            Check("children order", c => c.AddSubtree(root.GetInstanceID()));
            int id = grandchild.GetInstanceID();
            Object.DestroyImmediate(grandchild);
            Check("destroyed", c => { c.AddDestroyed(id); c.AddSubtree(other.GetInstanceID()); });
            Check("undo", c => c.SetUndoRedo());
        }
        finally { EditorSceneManager.ClosePreviewScene(scene); }
    }

    [Test] public void Hash64IsStableAndTellsTextsApart()
    {
        Assert.AreEqual(AvatarSignature.Hash64("abc"), AvatarSignature.Hash64("abc"));
        Assert.AreNotEqual(AvatarSignature.Hash64("abc"), AvatarSignature.Hash64("abd"));
        Assert.AreNotEqual(AvatarSignature.Hash64("Ā"), AvatarSignature.Hash64("\u0001"), "both bytes of a character count");
    }
    // TKV-13: an extension point says who registered what, undoes a registration on Dispose, and one failing extension
    // neither stops the others nor the feature.
    [Test] public void AnExtensionPointRegistersByOwnerAndSurvivesAFailingExtension()
    {
        var point = new ExtensionPoint<Func<int, int>>("Test extensions");
        Func<int, int> twice = x => x * 2, broken = _ => throw new InvalidOperationException("broken");
        var first = point.Register("Test", twice);
        point.Add(x => x + 1);
        using (point.Register("Broken", broken))
        {
            UnityEngine.TestTools.LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("InvalidOperationException: broken"));
            Assert.That(point.Each(f => f(5), -1), Is.EqualTo(new[] { 10, 6, -1 }));
        }
        Assert.That(point.Count, Is.EqualTo(2));
        first.Dispose();
        first.Dispose();
        Assert.That(point.Each(f => f(5)), Is.EqualTo(new[] { 6 }));
        Assert.That(point.Remove(twice), Is.False);
        Assert.That(point.ToList(), Has.Count.EqualTo(1));
    }
}
