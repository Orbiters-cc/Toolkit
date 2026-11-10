using System.Collections.Generic;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat;
using UnityEngine;

// The one listener of the SDK panel's builds: whether one is under way, and its start and end for every tool.
public sealed class VrcSdkBuildsTests
{
    // The panel closed during a first upload: the builder is still followed until the upload ends, so its id (the
    // manifest's) arrives; a builder of the reopened panel is followed after that.
    [Test] public void APublishKeepsItsBuilderUntilTheUploadEnds()
    {
        var operation = new VrcSdkBuilds.Operation();
        operation.BuildStarted(publish: true);
        operation.BuildEnded(failed: false);
        Assert.That(operation.PanelClosed(), Is.False, "built, not uploaded yet");
        Assert.That(operation.Settle(), Is.EqualTo((false, false)));
        operation.UploadStarted();
        Assert.That(operation.NewBuilder(idle: false), Is.False, "the panel reopened during the upload");
        operation.UploadEnded();
        Assert.That(operation.Settle(), Is.EqualTo((true, true)), "let go once uploaded, then the new one followed");
        Assert.That(operation.Settle(), Is.EqualTo((false, false)));
    }

    [Test] public void ATestBuildKeepsItsBuilderUntilItsEndOnly()
    {
        var operation = new VrcSdkBuilds.Operation();
        Assert.That(operation.PanelClosed(), Is.True, "nothing under way: let go at once");
        operation.BuildStarted(publish: false);
        Assert.That(operation.PanelClosed(), Is.False);
        operation.BuildEnded(failed: false);
        Assert.That(operation.Settle(), Is.EqualTo((true, false)));

        // A publish build that never uploads (its build failed, or the Build API only built) holds nothing for good.
        operation.BuildStarted(publish: true);
        operation.BuildEnded(failed: true);
        Assert.That(operation.Busy, Is.False);
        operation.BuildStarted(publish: true);
        operation.BuildEnded(failed: false);
        Assert.That(operation.NewBuilder(idle: true), Is.True, "the builder itself has nothing under way");
    }

    [Test] public void ABuildIsUnderWayFromItsStartToItsEnd()
    {
        var avatar = new GameObject("Built avatar");
        var started = new List<GameObject>();
        var finished = new List<string>();
        void Start(GameObject a) => started.Add(a);
        void Finish(string message) => finished.Add(message);
        VrcSdkBuilds.Starting += Start;
        VrcSdkBuilds.Finished += Finish;
        try
        {
            VrcSdkBuilds.OnBuildStart(null, avatar);
            Assert.That(VrcSdkBuilds.Building, Is.True);
            Assert.That(started, Is.EqualTo(new[] { avatar }));
            VrcSdkBuilds.OnBuildFinish(null, "done");
            Assert.That(VrcSdkBuilds.Building, Is.False, "ended, succeeded or not");
            Assert.That(finished, Is.EqualTo(new[] { "done" }));
        }
        finally
        {
            VrcSdkBuilds.Starting -= Start;
            VrcSdkBuilds.Finished -= Finish;
            Object.DestroyImmediate(avatar);
        }
    }
}
