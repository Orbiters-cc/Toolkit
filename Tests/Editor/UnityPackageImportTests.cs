using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine.TestTools;

public sealed class UnityPackageImportTests
{
    private static TaskCompletionSource<bool> Signal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    [TestCase(false, false)][TestCase(false, true)][TestCase(true, false)][TestCase(true, true)]
    public void NativeCallbackIdentityAcceptsPathAndSuffixFormsWithoutLosingDottedNames(bool fullPath, bool suffix)
    {
        string path = Path.Combine(Path.GetTempPath(), "first", "Hat.v1.2.unitypackage");
        string callback = fullPath ? Path.Combine(Path.GetTempPath(), "first", "Hat.v1.2") : "Hat.v1.2";
        if (suffix) callback += ".unitypackage";
        Assert.That(UnityPackageImport.MatchesPackage(path, callback), Is.True);
        Assert.That(UnityPackageImport.MatchesPackage(path, callback.Replace('\\', '/')), Is.True);
    }

    [TestCase(false)][TestCase(true)]
    public void CallbackWithAnUnrelatedFullPathNeverUsesTheBasenameFallback(bool suffix)
    {
        string path = Path.Combine(Path.GetTempPath(), "first", "Hat.v1.2.unitypackage");
        string callback = Path.Combine(Path.GetTempPath(), "second", "Hat.v1.2") + (suffix ? ".unitypackage" : "");
        Assert.That(UnityPackageImport.MatchesPackage(path, callback), Is.False);
        Assert.That(UnityPackageImport.MatchesPackage(path, callback.Replace('\\', '/')), Is.False);
    }

    [TestCase(false)][TestCase(true)]
    public void AStemEndingInUnitypackageIsNotStrippedTwice(bool fullPath)
    {
        string path = Path.Combine(Path.GetTempPath(), "Hat.unitypackage.unitypackage");
        string callback = fullPath ? Path.Combine(Path.GetTempPath(), "Hat.unitypackage") : "Hat.unitypackage";
        Assert.That(UnityPackageImport.MatchesPackage(path, callback), Is.True);
        Assert.That(UnityPackageImport.MatchesPackage(path, callback + ".unitypackage"), Is.True);
    }

    [TestCase("")][TestCase("Hat.v1")][TestCase("Other.v1.2")][TestCase("Hat.v1.2.zip")]
    public void UnrelatedBareCallbacksAreIgnored(string callback)
    {
        Assert.That(UnityPackageImport.MatchesPackage(Path.Combine(Path.GetTempPath(), "Hat.v1.2.unitypackage"), callback), Is.False);
    }

    private static IEnumerator Run(Task task)
    {
        double deadline = EditorApplication.timeSinceStartup + 20;
        while (!task.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
        Assert.That(task.IsCompleted, Is.True, "Queue verification did not finish.");
        task.GetAwaiter().GetResult();
    }

    [UnityTest] public IEnumerator SameNamedPackagesStartAndFinishIndependently() => Run(SameNamedPackagesStartAndFinishIndependentlyAsync());
    [UnityTest] public IEnumerator CancellingQueuedImportDoesNotStartOrAdvanceItsRecoveryCursor() => Run(CancellingQueuedImportDoesNotStartOrAdvanceItsRecoveryCursorAsync());
    [UnityTest] public IEnumerator CancellingStartedImportKeepsTheSlotUntilNativeCompletion() => Run(CancellingStartedImportKeepsTheSlotUntilNativeCompletionAsync());
    [UnityTest] public IEnumerator FailedImportAndFailedCheckpointReleaseTheSlot() => Run(FailedImportAndFailedCheckpointReleaseTheSlotAsync());
    [UnityTest] public IEnumerator BurstOfImportsWithCancelledWaitersNeverOverlaps() => Run(BurstOfImportsWithCancelledWaitersNeverOverlapsAsync());

    private static async Task Wait(Task task)
    {
        Assert.That(await Task.WhenAny(task, Task.Delay(5000)), Is.SameAs(task), "Import queue did not make progress.");
        await task;
    }

    private static async Task SameNamedPackagesStartAndFinishIndependentlyAsync()
    {
        var started = new List<string>();
        var firstDone = Signal(); var secondDone = Signal(); var secondStarted = Signal();
        var queue = new UnityPackageImport.Queue(path => {
            started.Add(path);
            if (started.Count == 1) return firstDone.Task;
            secondStarted.TrySetResult(true); return secondDone.Task;
        });
        var first = queue.ImportAsync("first/Hat.unitypackage");
        var second = queue.ImportAsync("second/Hat.unitypackage");
        CollectionAssert.AreEqual(new[] { "first/Hat.unitypackage" }, started);
        Assert.That(second.IsCompleted, Is.False);
        firstDone.SetResult(true);
        await Wait(first); await Wait(secondStarted.Task);
        Assert.That(second.IsCompleted, Is.False, "The first package's completion must not complete the second.");
        secondDone.SetResult(true); await Wait(second);
        CollectionAssert.AreEqual(new[] { "first/Hat.unitypackage", "second/Hat.unitypackage" }, started);
    }

    private static async Task CancellingQueuedImportDoesNotStartOrAdvanceItsRecoveryCursorAsync()
    {
        var firstDone = Signal(); int starts = 0, queuedCursor = 0;
        var queue = new UnityPackageImport.Queue(_ => { starts++; return firstDone.Task; });
        var first = queue.ImportAsync("first");
        using var cancellation = new CancellationTokenSource();
        var second = queue.ImportAsync("second", cancellation.Token, () => queuedCursor++);
        cancellation.Cancel();
        Assert.That(await Task.WhenAny(second, Task.Delay(5000)), Is.SameAs(second));
        Assert.That(second.IsCanceled, Is.True);
        Assert.That(starts, Is.EqualTo(1)); Assert.That(queuedCursor, Is.Zero);
        firstDone.SetResult(true); await Wait(first);
        await Wait(queue.ImportAsync("third"));
        Assert.That(starts, Is.EqualTo(2));
    }

    private static async Task CancellingStartedImportKeepsTheSlotUntilNativeCompletionAsync()
    {
        var firstDone = Signal(); var secondStarted = Signal(); int starts = 0;
        var queue = new UnityPackageImport.Queue(_ => {
            if (++starts == 1) return firstDone.Task;
            secondStarted.TrySetResult(true); return Task.CompletedTask;
        });
        using var cancellation = new CancellationTokenSource();
        var first = queue.ImportAsync("first", cancellation.Token);
        cancellation.Cancel();
        var second = queue.ImportAsync("second");
        Assert.That(first.IsCompleted, Is.False); Assert.That(starts, Is.EqualTo(1));
        firstDone.SetResult(true);
        await Wait(secondStarted.Task); await Wait(second);
        Assert.That(await Task.WhenAny(first, Task.Delay(5000)), Is.SameAs(first));
        Assert.That(first.IsCanceled, Is.True); Assert.That(starts, Is.EqualTo(2));
    }

    private static async Task FailedImportAndFailedCheckpointReleaseTheSlotAsync()
    {
        int starts = 0;
        var queue = new UnityPackageImport.Queue(_ => ++starts == 1 ? Task.FromException(new InvalidOperationException("fixture failure")) : Task.CompletedTask);
        var failed = queue.ImportAsync("first");
        try { await Wait(failed); Assert.Fail("The failed native import must fail its caller."); }
        catch (InvalidOperationException) { }
        var failedCheckpoint = queue.ImportAsync("second", starting: () => throw new InvalidOperationException("fixture checkpoint"));
        try { await Wait(failedCheckpoint); Assert.Fail("The failed checkpoint must prevent import."); }
        catch (InvalidOperationException) { }
        await Wait(queue.ImportAsync("third"));
        Assert.That(starts, Is.EqualTo(2));
    }

    private static async Task BurstOfImportsWithCancelledWaitersNeverOverlapsAsync()
    {
        int active = 0, peak = 0, started = 0;
        var release = Signal();
        var queue = new UnityPackageImport.Queue(async _ => {
            peak = Math.Max(peak, ++active); started++;
            try { await release.Task; await Task.Yield(); }
            finally { active--; }
        });
        var cancellations = Enumerable.Range(0, 32).Select(_ => new CancellationTokenSource()).ToArray();
        try
        {
            var jobs = cancellations.Select((c, i) => queue.ImportAsync(i + "/Hat.unitypackage", c.Token)).ToArray();
            for (int i = 1; i < cancellations.Length; i += 2) cancellations[i].Cancel();
            Assert.That(started, Is.EqualTo(1));
            release.SetResult(true);
            await Wait(Task.WhenAll(jobs.Select(async job => { try { await job; } catch (OperationCanceledException) { } })));
            Assert.That(peak, Is.EqualTo(1)); Assert.That(active, Is.Zero);
            Assert.That(started, Is.EqualTo(16));
            Assert.That(jobs.Count(j => j.IsCanceled), Is.EqualTo(16));
        }
        finally { foreach (var cancellation in cancellations) cancellation.Dispose(); }
    }
}
