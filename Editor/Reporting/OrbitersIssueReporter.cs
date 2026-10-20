using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>How a tool's <see cref="OrbitersIssueReporter"/> works: who it is, which code is its own and where its choices are kept.</summary>
    public sealed class IssueReporterOptions
    {
        /// <summary>The tool's name on the server ("My Avatar", "ReFit", "MCB").</summary>
        public string ToolName;

        /// <summary>The start of its EditorPrefs keys: "MyAvatar" keeps "MyAvatar_ShareIssueLogs" and "MyAvatar_MinIssueSeverity".</summary>
        public string SettingsKey;

        /// <summary>
        /// The namespaces of its code ("Orbiters.MyAvatar."): a Console entry is the tool's when the code that logged it
        /// (for an exception, the first code it was thrown from outside .NET and Unity) is in one of them. Orbiters
        /// Toolkit's code counts as its caller's.
        /// </summary>
        public string[] Namespaces = Array.Empty<string>();

        /// <summary>Namespaces of its tests: never reported (types named "…Tests" never are either).</summary>
        public string[] TestNamespaces = Array.Empty<string>();

        /// <summary>
        /// For a tool whose own logger hands its logs to <see cref="OrbitersIssueReporter.Submit"/> instead of the
        /// Console being read: called on the main thread with true when sharing starts and false when it stops, to
        /// connect and disconnect that logger.
        /// </summary>
        public Action<OrbitersIssueReporter, bool> Feed;

        /// <summary>Where reports go; Orbiters' "bugs" route on the server in use when null.</summary>
        public Func<string> Endpoint;

        /// <summary>Whether reports may be sent now (e.g. not while the tool knows the server is unreachable); always when null.</summary>
        public Func<bool> CanSend;
    }

    /// <summary>
    /// A tool's opt-in reporting of its own warnings and errors to Orbiters, one service for every tool. Off by default
    /// and per computer (EditorPrefs); the lowest level sent is the tool's choice. A log is the tool's when its stack trace
    /// says so (<see cref="IssueReporterOptions.Namespaces"/>), or when the tool's logger hands it over
    /// (<see cref="Submit"/>); each log belongs to one tool at most, tests' logs to none. Reports wait in a bounded queue
    /// (<see cref="IssueReportQueue"/>), at most 60 go per editor session, and each is masked
    /// (<see cref="IssueRedactor"/>) on the main thread right before it is sent, with the signed-in token when there is one.
    /// </summary>
    public sealed class OrbitersIssueReporter
    {
        /// <summary>The levels of the "send from" setting, for <see cref="MinSeverityLevel"/> 1 to 4.</summary>
        public static readonly string[] LevelNames = { "Info", "Warning", "Error", "Exception" };

        // An exception a tool logs is an error it met, not a crash: the server's FATAL is not used.
        private static readonly string[] SeverityNames = { "INFO", "WARN", "ERROR", "ERROR" };
        internal const int MessageLimit = 4000, StackLimit = 12000, SessionLimit = 60;
        private static readonly TimeSpan OfflinePause = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
        private static readonly char[] Whitespace = { ' ', '\n', '\r', '\t' };

        private readonly IssueReporterOptions options;
        private readonly string[] namespaces;
        private readonly string[] testNamespaces;
        private readonly string shareKey;
        private readonly string levelKey;
        private readonly string sentKey;
        internal readonly IssueReportQueue Queue = new IssueReportQueue();
        private volatile bool sharing;
        private volatile int minSeverity = 2;
        private bool fed;
        private bool sending;
        private DateTime offlineUntil;
        private CancellationTokenSource activeRequest;

        /// <summary>Sends a report: tests replace it.</summary>
        internal Func<string, string, IssuePayload, CancellationToken, Task> Transport = (url, token, payload, cancel) =>
            OrbitersApi.SendAsync<object>(url, token, payload, cancel);

        /// <summary>The masking of a report: tests replace it.</summary>
        internal Func<IssueRedactor> Redactor = IssueRedactor.ForThisEditor;

        /// <summary>The signed-in account's token, sent with reports (main thread): tests replace it.</summary>
        internal Func<string> Token = () => AuthenticationService.GetAuth()?.token;

        /// <summary>Whether reporting is paused (a test logged lately), and what pauses it: tests replace them.</summary>
        internal Func<DateTime, bool> IsQuiet = IssueReporting.Quiet;
        internal Action<DateTime> TestsSeen = IssueReporting.TestsSeen;

        internal OrbitersIssueReporter(IssueReporterOptions options)
        {
            if (options == null || string.IsNullOrEmpty(options.ToolName) || string.IsNullOrEmpty(options.SettingsKey))
            {
                throw new ArgumentException("An issue reporter needs a tool name and a settings key.", nameof(options));
            }

            this.options = options;
            namespaces = Copy(options.Namespaces);
            testNamespaces = Copy(options.TestNamespaces);
            shareKey = options.SettingsKey + "_ShareIssueLogs";
            levelKey = options.SettingsKey + "_MinIssueSeverity";
            sentKey = "Orbiters.IssueReports." + options.SettingsKey + ".Sent";
        }

        /// <summary>The reporter of a tool, created once at load ([InitializeOnLoad]); a second one with the same tool name replaces the first.</summary>
        public static OrbitersIssueReporter Register(IssueReporterOptions options)
        {
            var reporter = new OrbitersIssueReporter(options);
            IssueReporting.Add(reporter);
            reporter.Refresh();
            return reporter;
        }

        public string ToolName => options.ToolName;

        /// <summary>Whether the tool's diagnostics are sent. Off until the person turns it on; kept for every project on this computer.</summary>
        public bool ShareIssueLogs
        {
            get => EditorPrefs.GetBool(shareKey, false);
            set
            {
                EditorPrefs.SetBool(shareKey, value);
                Refresh();
            }
        }

        /// <summary>The least severe entry sent: 1 Info, 2 Warning (the default), 3 Error, 4 Exception.</summary>
        public int MinSeverityLevel
        {
            get => Mathf.Clamp(EditorPrefs.GetInt(levelKey, 2), 1, 4);
            set
            {
                EditorPrefs.SetInt(levelKey, Mathf.Clamp(value, 1, 4));
                minSeverity = MinSeverityLevel;
            }
        }

        /// <summary>What the tool's opt-in must say: what is sent, with what, where it applies and what is removed first.</summary>
        public string ConsentText =>
            "Sends " + ToolName + "'s own warnings and errors to Orbiters, with their stack traces, so they can be fixed. Off by default; once on, it " +
            "applies to every Unity project on this computer. While you are signed in, reports are linked to your Orbiters account. Before anything is " +
            "sent, tokens, keys, passwords, cookies, e-mail addresses, web links (Orbiters' own aside), VRChat ids and account names, your user and " +
            "computer names and folder paths are removed. Repeats are folded and reports are rate limited; Unity's and other packages' logs are never sent.";

        /// <summary>Reads the settings again (another editor may have changed them). Main thread.</summary>
        public void Refresh()
        {
            minSeverity = MinSeverityLevel;
            sharing = ShareIssueLogs;
            if (!sharing)
            {
                Queue.Clear();
            }

            if (options.Feed != null && fed != sharing)
            {
                fed = sharing;
                try
                {
                    options.Feed(this, sharing);
                }
                catch (Exception)
                {
                    // A tool's logger that cannot be connected leaves only the Console.
                }
            }

            IssueReporting.Update();
        }

        /// <summary>A log from the tool's own logger, with the stack trace of the code that logged it. Any thread.</summary>
        public void Submit(string message, string stack, LogType type) => Accept(message, stack, type, DateTime.UtcNow);

        /// <summary>Whether a Console entry with this stack trace would be reported as this tool's (whatever the settings).</summary>
        public bool IsOwnLog(string stack, LogType type) =>
            !IssueStackTrace.HasTestFrame(stack) && Owns(IssueStackTrace.Culprit(stack, type == LogType.Exception));

        /// <summary>1 Info, 2 Warning, 3 Error and Assert, 4 Exception.</summary>
        public static int LevelOf(LogType type) =>
            type == LogType.Exception ? 4 : type == LogType.Error || type == LogType.Assert ? 3 : type == LogType.Warning ? 2 : 1;

        internal bool Sharing => sharing;
        internal bool ReadsConsole => namespaces.Length > 0;

        /// <summary>The length of the namespace of this tool <paramref name="frame"/> is in (the longest wins between tools), 0 when not.</summary>
        internal int Claim(string frame)
        {
            if (string.IsNullOrEmpty(frame))
            {
                return 0;
            }

            foreach (string test in testNamespaces)
            {
                if (frame.StartsWith(test, StringComparison.Ordinal))
                {
                    return 0;
                }
            }

            int best = 0;
            foreach (string own in namespaces)
            {
                if (own.Length > best && frame.StartsWith(own, StringComparison.Ordinal))
                {
                    best = own.Length;
                }
            }

            return best;
        }

        private bool Owns(string frame) => Claim(frame) > 0;

        /// <summary>
        /// Queues a log of this tool's when sharing is on, its level is sent and no test logged it (<paramref name="testsChecked"/>:
        /// the caller looked already). Any thread.
        /// </summary>
        internal bool Accept(string message, string stack, LogType type, DateTime now, bool testsChecked = false)
        {
            int level = LevelOf(type);
            if (!sharing || level < minSeverity || string.IsNullOrEmpty(message) || IsQuiet(now))
            {
                return false;
            }

            if (!testsChecked && IssueStackTrace.HasTestFrame(stack))
            {
                TestsSeen(now);
                return false;
            }

            return Queue.Enqueue(new IssueReport
            {
                Message = Keep(message, IssueReportQueue.MessageKeep),
                Stack = Keep(stack, IssueReportQueue.StackKeep),
                Type = type,
                Severity = SeverityNames[level - 1]
            }, now);
        }

        /// <summary>Sends the next report when one may go. Main thread.</summary>
        internal void Pump(DateTime now)
        {
            if (!sharing || sending || now < offlineUntil || Queue.PendingCount == 0 || !MaySend())
            {
                return;
            }

            if (SessionState.GetInt(sentKey, 0) >= SessionLimit)
            {
                Queue.Clear();
                return;
            }

            if (Queue.TryTake(now, out var report))
            {
                _ = SendAsync(report);
            }
        }

        /// <summary>Masks and sends one report. Main thread until the request goes.</summary>
        internal async Task SendAsync(IssueReport report)
        {
            sending = true;
            var cancel = new CancellationTokenSource(RequestTimeout);
            activeRequest = cancel;
            try
            {
                var redactor = Redactor();
                string message = redactor.Redact(report.Message, MessageLimit);
                string stack = redactor.Redact(report.Stack, StackLimit);
                // A report that could not be masked in time is never sent.
                if (string.IsNullOrEmpty(message) || stack == null || !sharing)
                {
                    return;
                }

                SessionState.SetInt(sentKey, SessionState.GetInt(sentKey, 0) + 1);
                var payload = new IssuePayload
                {
                    message = message,
                    severity = report.Severity,
                    errorType = report.Type.ToString(),
                    tool = ToolName,
                    stackTrace = stack,
                    userAgent = "Unity " + Application.unityVersion,
                    browserInfo = new IssuePayload.EditorInfo { unityVersion = Application.unityVersion, platform = Application.platform.ToString() }
                };
                string url = options.Endpoint?.Invoke() ?? OrbitersEnvironment.ApiUrl("bugs");
                await Transport(url, Token(), payload, cancel.Token);
            }
            // The server could not be reached: wait before the next report instead of trying each one.
            catch (HttpRequestException)
            {
                offlineUntil = DateTime.UtcNow + OfflinePause;
            }
            catch (OperationCanceledException)
            {
                if (sharing)
                {
                    offlineUntil = DateTime.UtcNow + OfflinePause;
                }
            }
            catch (Exception)
            {
                // Reporting failures must not make more reports.
            }
            finally
            {
                if (activeRequest == cancel)
                {
                    activeRequest = null;
                }

                cancel.Dispose();
                sending = false;
            }
        }

        /// <summary>Script reload or quitting: nothing waits and nothing is in flight.</summary>
        internal void Stop()
        {
            Queue.Clear();
            try
            {
                activeRequest?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // It ended meanwhile.
            }
        }

        private bool MaySend()
        {
            try
            {
                return options.CanSend == null || options.CanSend();
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// The start of <paramref name="text"/> kept until it is masked, cut at a space or a line end: a secret is never
        /// cut in half (half of one is no longer recognised).
        /// </summary>
        internal static string Keep(string text, int length)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            if (text.Length <= length)
            {
                return text;
            }

            int cut = text.LastIndexOfAny(Whitespace, length - 1, Math.Min(length, 400));
            return text.Substring(0, cut > 0 ? cut : Math.Max(0, length - 400));
        }

        private static string[] Copy(string[] values)
        {
            var list = new List<string>();
            foreach (string value in values ?? Array.Empty<string>())
            {
                if (!string.IsNullOrEmpty(value))
                {
                    list.Add(value);
                }
            }

            return list.ToArray();
        }
    }

    /// <summary>What the server's "bugs" route takes.</summary>
    internal sealed class IssuePayload
    {
        public string message;
        public string severity;
        public string errorType;
        public string tool;
        public string stackTrace;
        public string userAgent;
        public EditorInfo browserInfo;

        internal sealed class EditorInfo
        {
            public string unityVersion;
            public string platform;
        }
    }

    /// <summary>
    /// The one Console listener behind every tool's reporter. A log goes to one tool at most: the one whose namespace the
    /// code that logged it is in (<see cref="IssueStackTrace.Culprit"/>), never a test's. It listens from the editor's
    /// start, so what tools log while they load is not missed, then only while a tool shares; it stays quiet for two
    /// minutes after a test logged and while Unity's Test Runner runs (tests log their deliberate failures).
    /// </summary>
    [InitializeOnLoad]
    internal static class IssueReporting
    {
        private const int StartupLimit = 32;
        private static readonly TimeSpan QuietAfterTests = TimeSpan.FromMinutes(2);
        private static readonly object gate = new object();
        private static OrbitersIssueReporter[] reporters = Array.Empty<OrbitersIssueReporter>();
        private static List<(string Message, string Stack, LogType Type, DateTime Time)> startup =
            new List<(string, string, LogType, DateTime)>();
        private static bool listening;
        private static bool pumping;
        private static long quietUntil;
        private static double nextTestCheck;
        private static MethodInfo isRunActive;
        private static bool testRunnerLooked;

        static IssueReporting()
        {
            Application.logMessageReceivedThreaded += OnLog;
            listening = true;
            EditorApplication.update += EndStartup;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
        }

        internal static void Add(OrbitersIssueReporter reporter)
        {
            lock (gate)
            {
                var list = new List<OrbitersIssueReporter>(reporters);
                list.RemoveAll(r => r.ToolName == reporter.ToolName);
                list.Add(reporter);
                reporters = list.ToArray();
            }
        }

        internal static bool Quiet(DateTime now) => now.Ticks < Interlocked.Read(ref quietUntil);

        /// <summary>A test logged: nothing is reported for a while, and what waits is dropped.</summary>
        internal static void TestsSeen(DateTime now)
        {
            Interlocked.Exchange(ref quietUntil, (now + QuietAfterTests).Ticks);
            foreach (var reporter in reporters)
            {
                reporter.Queue.Clear();
            }
        }

        /// <summary>Listens to the Console while a tool reads it and shares, and sends while any tool shares. Main thread.</summary>
        internal static void Update()
        {
            bool any = false, console = false;
            foreach (var reporter in reporters)
            {
                any |= reporter.Sharing;
                console |= reporter.Sharing && reporter.ReadsConsole;
            }

            bool starting;
            lock (gate)
            {
                starting = startup != null;
            }

            if (!starting && console != listening)
            {
                listening = console;
                if (console)
                {
                    Application.logMessageReceivedThreaded += OnLog;
                }
                else
                {
                    Application.logMessageReceivedThreaded -= OnLog;
                }
            }

            if (any != pumping)
            {
                pumping = any;
                if (any)
                {
                    EditorApplication.update += Pump;
                }
                else
                {
                    EditorApplication.update -= Pump;
                }
            }
        }

        // Every package's start-up code has run: what Orbiters code logged meanwhile goes to its tool.
        private static void EndStartup()
        {
            EditorApplication.update -= EndStartup;
            List<(string Message, string Stack, LogType Type, DateTime Time)> held;
            lock (gate)
            {
                held = startup;
                startup = null;
            }

            foreach (var log in held ?? new List<(string, string, LogType, DateTime)>())
            {
                Route(log.Message, log.Stack, log.Type, log.Time);
            }

            Update();
        }

        private static void OnLog(string message, string stack, LogType type)
        {
            // Only Orbiters code can be a tool's; no Unity API here (any thread).
            if (string.IsNullOrEmpty(stack) || stack.IndexOf("Orbiters.", StringComparison.Ordinal) < 0)
            {
                return;
            }

            var now = DateTime.UtcNow;
            lock (gate)
            {
                if (startup != null)
                {
                    if (startup.Count < StartupLimit)
                    {
                        startup.Add((OrbitersIssueReporter.Keep(message, IssueReportQueue.MessageKeep), OrbitersIssueReporter.Keep(stack, IssueReportQueue.StackKeep), type, now));
                    }

                    return;
                }
            }

            Route(message, stack, type, now);
        }

        internal static void Route(string message, string stack, LogType type, DateTime now)
        {
            var all = reporters;
            if (all.Length == 0)
            {
                return;
            }

            if (IssueStackTrace.HasTestFrame(stack))
            {
                TestsSeen(now);
                return;
            }

            OwnerOf(all, stack, type)?.Accept(message, stack, type, now, testsChecked: true);
        }

        /// <summary>The one reporter among <paramref name="all"/> a Console entry belongs to, or null; a test's entry is no one's, which callers check first.</summary>
        internal static OrbitersIssueReporter OwnerOf(IReadOnlyList<OrbitersIssueReporter> all, string stack, LogType type)
        {
            string culprit = IssueStackTrace.Culprit(stack, type == LogType.Exception);
            OrbitersIssueReporter owner = null;
            int best = 0;
            foreach (var reporter in all)
            {
                int claim = reporter.ReadsConsole ? reporter.Claim(culprit) : 0;
                if (claim > best)
                {
                    best = claim;
                    owner = reporter;
                }
            }

            return owner;
        }

        private static void Pump()
        {
            var now = DateTime.UtcNow;
            if (TestRunActive())
            {
                TestsSeen(now);
            }

            if (Quiet(now))
            {
                return;
            }

            foreach (var reporter in reporters)
            {
                reporter.Pump(now);
            }
        }

        // Unity's Test Runner has no public "is a run going on" outside its callbacks; its internal one, looked up once
        // and asked at most once a second. Without it, tests are still recognised by their stack traces.
        private static bool TestRunActive()
        {
            double time = EditorApplication.timeSinceStartup;
            if (time < nextTestCheck)
            {
                return false;
            }

            nextTestCheck = time + 1d;
            try
            {
                if (!testRunnerLooked)
                {
                    testRunnerLooked = true;
                    var api = Type.GetType("UnityEditor.TestTools.TestRunner.Api.TestRunnerApi, UnityEditor.TestRunner");
                    isRunActive = api?.GetMethod("IsRunActive", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    if (isRunActive != null && isRunActive.ReturnType != typeof(bool))
                    {
                        isRunActive = null;
                    }
                }

                return isRunActive != null && (bool)isRunActive.Invoke(null, null);
            }
            catch (Exception)
            {
                isRunActive = null;
                return false;
            }
        }

        private static void Stop()
        {
            Application.logMessageReceivedThreaded -= OnLog;
            EditorApplication.update -= Pump;
            EditorApplication.update -= EndStartup;
            listening = pumping = false;
            lock (gate)
            {
                startup = null;
            }

            foreach (var reporter in reporters)
            {
                reporter.Stop();
            }
        }
    }
}
