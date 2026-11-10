using System;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using NUnit.Framework;

/// <summary>
/// A task's result read in a test without ever blocking the editor thread: a task that has not finished fails the test
/// (with <paramref name="what"/>), a fault is thrown as itself. Reading Task.Result of an unfinished task that needs the
/// main thread blocks it for ever: the test framework's timeout only leaves the nested wait, then the test goes on.
/// </summary>
public static class TaskResults
{
    public static T Done<T>(Task<T> task, string what = null)
    {
        Done((Task)task, what);
        return task.Result;
    }

    public static void Done(Task task, string what = null)
    {
        Assert.That(task.IsCompleted, Is.True, (what ?? "The task") + " did not finish in time.");
        if (task.IsFaulted) ExceptionDispatchInfo.Capture(task.Exception.GetBaseException()).Throw();
        if (task.IsCanceled) throw new OperationCanceledException((what ?? "The task") + " was cancelled.");
    }
}
