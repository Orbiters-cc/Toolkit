using System.Runtime.CompilerServices;

// The reload sequence (EditorProcessRunner.StopAllThenResume) is for tests only: the runner's own tests, and Mixer Git's,
// which check that a Git change in flight at a reload finishes.
[assembly: InternalsVisibleTo("Orbiters.Toolkit.Editor.Processes.Tests")]
[assembly: InternalsVisibleTo("Orbiters.Mixer.Git.Editor.Tests")]
