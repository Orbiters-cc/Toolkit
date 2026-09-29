using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>Serializes native Unity package imports, whose completion events may identify a basename or full path.</summary>
    public static class UnityPackageImport
    {
        private static readonly Queue Imports = new Queue(ImportNativeAsync);

        /// <summary>
        /// Call on the editor thread. Queued cancellation prevents import; cancellation after import starts waits for Unity
        /// to finish before releasing the next caller. Persist a queued job before calling, and advance its recovery cursor
        /// in <paramref name="starting"/> only: scripts may reload the domain while other jobs are still queued.
        /// </summary>
        public static Task ImportAsync(string path, CancellationToken cancellation = default, Action starting = null) =>
            Imports.ImportAsync(path, cancellation, starting);

        // An isolated queue lets tests exercise scheduling, failures and cancellation without importing project files.
        internal sealed class Queue
        {
            private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
            private readonly Func<string, Task> import;

            internal Queue(Func<string, Task> import) { this.import = import; }

            internal async Task ImportAsync(string path, CancellationToken cancellation = default, Action starting = null)
            {
                await gate.WaitAsync(cancellation);
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    starting?.Invoke();
                    await import(path);
                    cancellation.ThrowIfCancellationRequested();
                }
                finally { gate.Release(); }
            }
        }

        private static async Task ImportNativeAsync(string path)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            string expectedPath = Path.GetFullPath(path);
            void Completed(string package) { if (MatchesPackage(expectedPath, package)) done.TrySetResult(true); }
            void Failed(string package, string error) { if (MatchesPackage(expectedPath, package)) done.TrySetException(new InvalidOperationException("Unity could not import " + package + ": " + error)); }
            void Cancelled(string package) { if (MatchesPackage(expectedPath, package)) done.TrySetCanceled(); }
            AssetDatabase.importPackageCompleted += Completed;
            AssetDatabase.importPackageFailed += Failed;
            AssetDatabase.importPackageCancelled += Cancelled;
            try
            {
                AssetDatabase.ImportPackage(path, false);
                await done.Task;
            }
            finally
            {
                AssetDatabase.importPackageCompleted -= Completed;
                AssetDatabase.importPackageFailed -= Failed;
                AssetDatabase.importPackageCancelled -= Cancelled;
            }
        }

        // Unity versions/events differ: the callback can contain a full path without its .unitypackage suffix.
        // Only bare names need the serialized-queue fallback; a path must identify this exact requested package.
        internal static bool MatchesPackage(string requestedPath, string callback)
        {
            if (string.IsNullOrEmpty(requestedPath) || string.IsNullOrEmpty(callback)) return false;
            const string suffix = ".unitypackage";
            string Stem(string value) => value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? value.Substring(0, value.Length - suffix.Length) : value;
            string Separators(string value) => value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            try
            {
                string expected = Path.GetFullPath(Separators(Stem(requestedPath)));
                string received = Separators(callback);
                bool hasPath = callback.IndexOf('/') >= 0 || callback.IndexOf('\\') >= 0 || Path.IsPathRooted(callback);
                if (hasPath) received = Path.GetFullPath(received);
                else expected = Path.GetFileName(expected);
                // Compare the extensionless form before stripping: the legitimate stem can itself end in .unitypackage.
                return string.Equals(expected, received, comparison) || string.Equals(expected, Stem(received), comparison);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return false;
            }
        }
    }
}
