using System.Collections.Generic;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat;
using UnityEngine;

// The one listener of the SDK panel's builds: whether one is under way, and its start and end for every tool.
public sealed class VrcSdkBuildsTests
{
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
