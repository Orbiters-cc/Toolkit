using System;
using System.Collections.Generic;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// Reads the stack traces log handlers get, frame by frame: Unity's own lines ("Ns.Type:Method () (at file:12)"),
    /// "Full" stack traces (native frames and "0x… (Mono JIT Code) [file:12] Ns.Type:Method ()") and System.Diagnostics'
    /// ("at Ns.Type.Method () in file:line 12"). Says which code a log belongs to and whether a test logged it.
    /// </summary>
    internal static class IssueStackTrace
    {
        // Unity's logging itself, above the code that logged.
        private static readonly string[] LoggingFrames =
        {
            "UnityEngine.Debug", "UnityEngine.Logger", "UnityEngine.DebugLogHandler", "UnityEngine.StackTraceUtility",
            "UnityEngine.Application:CallLogCallback", "UnityEngine.Application.CallLogCallback", "System.Diagnostics.StackTrace",
            "System.Environment"
        };

        // The .NET and Unity frameworks: an exception thrown in them belongs to the code that called them.
        private static readonly string[] FrameworkFrames =
        {
            "System.", "Mono.", "Microsoft.", "UnityEngine.", "UnityEditor.", "UnityEngineInternal.", "UnityEditorInternal.", "Unity.",
            "Newtonsoft.", "(wrapper"
        };

        // Orbiters Toolkit works for the tool that called it: what it logs or throws belongs to that tool.
        private const string SharedNamespace = "Orbiters.Toolkit.";

        private static readonly string[] TestFrameworks =
        {
            "NUnit.", "UnityEngine.TestTools", "UnityEngine.TestRunner", "UnityEditor.TestTools", "UnityEditor.TestRunner"
        };

        /// <summary>The frames of <paramref name="stack"/>, as "Ns.Type:Method (…) …" or "Ns.Type.Method (…) …"; native frames are left out.</summary>
        internal static IEnumerable<string> Frames(string stack)
        {
            if (string.IsNullOrEmpty(stack))
            {
                yield break;
            }

            foreach (string line in stack.Split('\n'))
            {
                string frame = Frame(line);
                if (!string.IsNullOrEmpty(frame))
                {
                    yield return frame;
                }
            }
        }

        /// <summary>One line of a stack trace as a frame, or null for a native frame, a note or an empty line.</summary>
        internal static string Frame(string line)
        {
            string frame = (line ?? string.Empty).Trim();
            if (frame.StartsWith("0x", StringComparison.Ordinal))
            {
                // Full stack traces: "0x00007FF6A1B2C3D4 (Unity) StackWalker::GetCurrentCallstack" is native;
                // "0x000001A2B3C4D5E6 (Mono JIT Code) [Debug.bindings.cs:120] UnityEngine.Debug:LogWarning (object)" is managed.
                int open = frame.IndexOf(" (", StringComparison.Ordinal);
                int close = open < 0 ? -1 : frame.IndexOf(") ", open, StringComparison.Ordinal);
                if (close < 0 || frame.IndexOf("Mono JIT Code", open, close - open, StringComparison.Ordinal) < 0)
                {
                    return null;
                }

                frame = frame.Substring(close + 2).TrimStart();
                if (frame.StartsWith("[", StringComparison.Ordinal))
                {
                    int end = frame.IndexOf("] ", StringComparison.Ordinal);
                    frame = end < 0 ? string.Empty : frame.Substring(end + 2).TrimStart();
                }
            }

            if (frame.StartsWith("at ", StringComparison.Ordinal))
            {
                frame = frame.Substring(3).TrimStart();
            }

            // "Rethrow as …", "--- End of stack trace from previous location ---": not frames.
            if (frame.Length == 0 || frame.StartsWith("---", StringComparison.Ordinal) || frame.StartsWith("Rethrow as ", StringComparison.Ordinal))
            {
                return null;
            }

            return frame;
        }

        /// <summary>
        /// The frame a log belongs to: the code that called Debug.Log, or for an exception the first frame from where it
        /// was thrown that is not the .NET or Unity framework (an exception thrown in a Linq call belongs to its caller).
        /// Orbiters Toolkit's frames are passed over: the Toolkit works for the tool that called it. Null when none.
        /// </summary>
        internal static string Culprit(string stack, bool exception)
        {
            foreach (string frame in Frames(stack))
            {
                if (IsLogging(frame) || frame.StartsWith(SharedNamespace, StringComparison.Ordinal) || exception && IsFramework(frame))
                {
                    continue;
                }

                return frame;
            }

            return null;
        }

        /// <summary>Whether any frame of <paramref name="stack"/> is a test or a test framework.</summary>
        internal static bool HasTestFrame(string stack)
        {
            foreach (string frame in Frames(stack))
            {
                if (IsTest(frame))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether a frame is a test framework's, or of a type in a "Tests" namespace or named "…Tests" (every Orbiters
        /// test fixture, MCB's in the global namespace too).
        /// </summary>
        internal static bool IsTest(string frame)
        {
            foreach (string prefix in TestFrameworks)
            {
                if (frame.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            string type = TypeOf(frame);
            int start = 0;
            for (int i = 0; i <= type.Length; i++)
            {
                if (i < type.Length && type[i] != '.' && type[i] != '/' && type[i] != '+')
                {
                    continue;
                }

                int length = i - start;
                if (length >= 4 && string.CompareOrdinal(type, start, "Test", 0, 4) == 0 && (length == 4 || length == 5 && type[start + 4] == 's') ||
                    length > 5 && string.CompareOrdinal(type, i - 5, "Tests", 0, 5) == 0)
                {
                    return true;
                }

                start = i + 1;
            }

            return false;
        }

        // "Ns.Type/Nested:Method (args) (at …)" → "Ns.Type/Nested"; "Ns.Type+Nested.Method (args) in …" → "Ns.Type+Nested".
        private static string TypeOf(string frame)
        {
            int paren = frame.IndexOf('(');
            string head = paren > 0 ? frame.Substring(0, paren).TrimEnd() : frame;
            int colon = head.IndexOf(':');
            if (colon > 0)
            {
                return head.Substring(0, colon);
            }

            // Generic arguments ("Method[T]") hold dots of their own.
            int bracket = head.IndexOf('[');
            if (bracket > 0)
            {
                head = head.Substring(0, bracket);
            }

            int dot = head.LastIndexOf('.');
            return dot > 0 ? head.Substring(0, dot) : head;
        }

        private static bool IsLogging(string frame)
        {
            foreach (string prefix in LoggingFrames)
            {
                if (frame.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return frame.StartsWith("(wrapper managed-to-native) UnityEngine.DebugLogHandler", StringComparison.Ordinal);
        }

        private static bool IsFramework(string frame)
        {
            foreach (string prefix in FrameworkFrames)
            {
                if (frame.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
