using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// Background work the user is waiting on (a drop being opened, files being listed), on a thread of its own. The shared
    /// thread pool may be full of other tools' work (hashing, mesh comparisons), and a Task.Run then waits its turn for
    /// seconds before it even starts.
    /// </summary>
    public static class DedicatedTask
    {
        public static Task<T> Run<T>(Func<T> work, CancellationToken cancellation = default) =>
            Task.Factory.StartNew(work, cancellation, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);

        public static Task Run(Action work, CancellationToken cancellation = default) =>
            Task.Factory.StartNew(work, cancellation, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
    }
}
