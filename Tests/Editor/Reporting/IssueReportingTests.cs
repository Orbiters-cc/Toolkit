using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Tests
{
    /// <summary>The shared issue reporter: what it masks, which tool a log belongs to, how much it keeps and sends, and what a send carries.</summary>
    public sealed class IssueReportingTests
    {
        private const string CurrentToken = "orbit-11111111-22222222-33333333-01-02-2026";
        private const string OtherToken = "orbit-aaaaaaaa-bbbbbbbb-cccccccc-05-06-2026";
        private const string AvatarId = "avtr_12345678-1234-1234-1234-123456789abc";

        private static IssueRedactor Redactor() => new IssueRedactor(new IssueRedactor.Identity
        {
            UserFolder = @"C:\Users\Jane Doe",
            UserName = "Jane Doe",
            ComputerName = "JANE-PC",
            ProjectFolder = @"C:\Users\Jane Doe\Projects\Fox Avatar",
            UnityFolder = @"C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor",
            ApiHost = "api.orbiters.cc",
            Secrets = new[] { CurrentToken },
            Names = new[] { "foxmember" }
        });

        private static string Mask(string text) => Redactor().Redact(text, 4000);

        // ---- Masking ------------------------------------------------------------------------------------------------

        [TestCase("Cookie: session=one; second=two", "two")]
        [TestCase("token=secret-value", "secret-value")]
        [TestCase("Authorization: Bearer abc.def-123", "abc.def-123")]
        [TestCase("Retrying with Bearer abcdefgh12345", "abcdefgh12345")]
        [TestCase("{\"password\":\"private value\"}", "private value")]
        [TestCase("Request https://example.test/model?token=private", "example.test")]
        [TestCase("C:\\Users\\Creator\\Private Project\\Assets\\avatar.fbx", "Creator")]
        [TestCase("C:\\Users\\Creator\\Private Project\\Assets\\avatar.fbx", "Private Project")]
        [TestCase("Could not open C:\\Users\\Jane Doe\\Downloads\\Fox.unitypackage: denied", "Doe")]
        [TestCase("Could not open C:\\Users\\John Smith\\Desktop\\Fox.unitypackage: denied", "Smith")]
        [TestCase("/home/creator/private/avatar.fbx", "creator")]
        [TestCase("/Users/some person/Projects/avatar.fbx", "person")]
        [TestCase("Could not read /Users/Jane Doe/Library/Logs/Unity/Editor.log", "Doe")]
        [TestCase("Assets/Private Avatar/body.fbx", "Private Avatar")]
        [TestCase("creator@example.test", "creator@example.test")]
        [TestCase("https://example.test/u?mail=creator%40example.test", "creator")]
        [TestCase("Sent to creator%40example.test", "creator")]
        [TestCase("The signed-in token " + CurrentToken + " was refused", "11111111")]
        [TestCase("Another account's token " + OtherToken + " was refused", "aaaaaaaa")]
        [TestCase("VRChat cookie authcookie_0123abcd-4567-89ef-0123-456789abcdef", "0123abcd")]
        [TestCase("ghp_abcdefghijklmnopqrstuvwxyz0123456789 leaked", "ghp_abcdefghijklmnopqrstuvwxyz")]
        [TestCase("key sk-abcdefghijklmnopqrstuvwxyz0123456789ABCD", "abcdefghijklmnopqrstuvwxyz0123456789")]
        [TestCase("License ABCDEF12-34567890-ABCDEF12-34567890 rejected", "34567890")]
        [TestCase("license_key: abcd efgh", "abcd efgh")]
        [TestCase("The avatar " + AvatarId + " is not yours", "12345678-1234")]
        [TestCase("Owner usr_12345678-1234-1234-1234-123456789abc", "12345678-1234")]
        [TestCase("Signed in as foxmember", "foxmember")]
        [TestCase("Jane Doe on JANE-PC", "Jane")]
        [TestCase("Jane Doe on JANE-PC", "JANE-PC")]
        [TestCase("{\"displayName\":\"Somebody Else\"}", "Somebody Else")]
        public void SensitiveTextIsMasked(string text, string secret) => StringAssert.DoesNotContain(secret, Mask(text));

        [Test]
        public void AnotherAccountsNameAndAvatarAreMasked()
        {
            // My Avatar's blueprint check, word for word.
            string message = "[My Avatar] Build & Publish stopped before the build: the blueprint ID " + AvatarId + " of Fox belongs to " +
                             "\u201CEnzo's Fox\u201D published by Some Other Person. VRChat would refuse the upload. Clear the ID to upload it as a new avatar.";
            string masked = Mask(message);
            StringAssert.DoesNotContain("Some Other Person", masked);
            StringAssert.DoesNotContain("Enzo's Fox", masked);
            StringAssert.DoesNotContain("12345678-1234", masked);
            StringAssert.Contains("VRChat would refuse the upload.", masked, "The reason stays.");
            StringAssert.Contains("published by another VRChat account", Mask("It was published by another VRChat account."));
        }

        [Test]
        public void PathsKeepTheirFileLineAndReason()
        {
            Assert.AreEqual("Orbiters.MyAvatar.Editor.X:Y () (at Packages/orbiters.myavatar/Editor/X.cs:12)",
                Mask("Orbiters.MyAvatar.Editor.X:Y () (at Packages/orbiters.myavatar/Editor/X.cs:12)"), "Packages' paths are the tools' own.");
            Assert.AreEqual("Thing:Do () (at Assets/[path]/Thing.cs:3)", Mask("Thing:Do () (at Assets/Creator Tools/Scripts/Thing.cs:3)"));
            Assert.AreEqual("Assets/[path]/body.fbx could not be imported: bad header", Mask("Assets/Private Avatar/body.fbx could not be imported: bad header"));
            Assert.AreEqual("Assets/[path] could not be made: access denied", Mask("Assets/MCB/generated/Temp could not be made: access denied"));
            Assert.AreEqual(@"at MCBLogger.Report () in %PROJECT%\Packages\orbiters.mcb\Runtime\MCBLogger.cs:line 13",
                Mask(@"at MCBLogger.Report () in C:\Users\Jane Doe\Projects\Fox Avatar\Packages\orbiters.mcb\Runtime\MCBLogger.cs:line 13"));
            Assert.AreEqual(@"%PROJECT%\Assets\[path]\Body.fbx", Mask(@"C:\Users\Jane Doe\Projects\Fox Avatar\Assets\Fox\Body.fbx"));
            Assert.AreEqual("POST https://api.orbiters.cc/bugs failed", Mask("POST https://api.orbiters.cc/bugs?t=abc failed"), "Orbiters' API keeps its route.");
            Assert.AreEqual("See https://orbiters.cc/[path].", Mask("See https://orbiters.cc/u/somebody."));
        }

        [Test]
        public void TokensAreMaskedBeforeTheTextIsCut()
        {
            string text = new string('x', 3990) + " " + OtherToken + " and more";
            string masked = Redactor().Redact(text, 4000);
            Assert.LessOrEqual(masked.Length, 4000);
            StringAssert.DoesNotContain("orbit-a", masked);
            StringAssert.DoesNotContain("aaaa", masked);

            // What is kept of a long log until it is masked ends at a space: never half a token.
            string kept = OrbitersIssueReporter.Keep(new string('x', IssueReportQueue.MessageKeep - 20) + " " + OtherToken + " tail", IssueReportQueue.MessageKeep);
            Assert.AreEqual(IssueReportQueue.MessageKeep - 20, kept.Length);
            StringAssert.DoesNotContain("orbit", kept);
        }

        // ---- Whose log it is ----------------------------------------------------------------------------------------

        private static OrbitersIssueReporter Tool(string name, string space) => new OrbitersIssueReporter(new IssueReporterOptions
        {
            ToolName = name,
            SettingsKey = "OrbitersIssueReportingTests_" + Guid.NewGuid().ToString("N"),
            Namespaces = new[] { space + "." },
            TestNamespaces = new[] { space + ".Editor.Tests" }
        });

        [Test]
        public void EachLogBelongsToOneToolAtMost()
        {
            var myAvatar = Tool("My Avatar", "Orbiters.MyAvatar");
            var refit = Tool("ReFit", "Orbiters.ReFit");
            var all = new[] { myAvatar, refit };
            const string refitThrows =
                "System.Linq.Enumerable.First[TSource] (System.Collections.Generic.IEnumerable`1[T] source)\n" +
                "Orbiters.ReFit.Editor.ReFitEngine.Run () (at Packages/orbiters.refit/Editor/ReFitEngine.cs:40)\n" +
                "Orbiters.MyAvatar.Editor.Clothing.Fit () (at Packages/orbiters.myavatar/Editor/Clothing.cs:12)";
            const string vrcfuryThrows =
                "VF.Builder.VFGameObject.Find () (at Packages/com.vrcfury.vrcfury/Editor/VF/Builder.cs:3)\n" +
                "Orbiters.MyAvatar.Editor.BuildHook.OnPreprocessAvatar () (at Packages/orbiters.myavatar/Editor/BuildHook.cs:9)";
            const string toolkitLogs =
                "UnityEngine.Debug:LogWarning (object)\n" +
                "Orbiters.Toolkit.Editor.MeshAssets:Save (UnityEngine.Mesh) (at Packages/orbiters.toolkit/Editor/Meshes/MeshAssets.cs:40)\n" +
                "Orbiters.MyAvatar.Editor.Painter:Apply () (at Packages/orbiters.myavatar/Editor/Painter.cs:7)";
            const string unityLogs =
                "UnityEngine.Debug:LogWarning (object)\n" +
                "UnityEngine.Mesh:set_vertices (UnityEngine.Vector3[])\n" +
                "Orbiters.MyAvatar.Editor.Painter:Apply () (at Packages/orbiters.myavatar/Editor/Painter.cs:7)";
            Assert.AreSame(refit, IssueReporting.OwnerOf(all, refitThrows, LogType.Exception), "Thrown in ReFit: ReFit's, not My Avatar's too.");
            Assert.IsNull(IssueReporting.OwnerOf(all, vrcfuryThrows, LogType.Exception), "Thrown in VRCFury: another package's.");
            Assert.AreSame(myAvatar, IssueReporting.OwnerOf(all, toolkitLogs, LogType.Warning), "The Toolkit works for its caller.");
            Assert.IsNull(IssueReporting.OwnerOf(all, unityLogs, LogType.Warning), "Unity's own message.");
        }

        [Test]
        public void TestsLogsAreNoOnes()
        {
            var myAvatar = Tool("My Avatar", "Orbiters.MyAvatar");
            const string own = "UnityEngine.Debug:LogWarning (object)\nOrbiters.MyAvatar.Sample:Run () (at Packages/sample.cs:12)\nUnityEditor.EditorApplication:Internal_CallUpdateFunctions ()";
            Assert.True(myAvatar.IsOwnLog(own, LogType.Warning));
            Assert.False(myAvatar.IsOwnLog(own + "\nOrbiters.MyAvatar.Editor.Tests.OptimizationTests:AFailingEstimate ()", LogType.Warning),
                "A deliberate failure: a test called the code that logged.");
            Assert.False(myAvatar.IsOwnLog("UnityEngine.Debug:Log (object)\nOrbiters.MyAvatar.Editor.Tests.Sample:Case ()", LogType.Log));
            Assert.True(IssueStackTrace.HasTestFrame("MCBLogger:LogError (string)\nIssueReportingRegressionTests:Case ()"), "MCB's tests have no namespace.");
            Assert.True(IssueStackTrace.HasTestFrame("Orbiters.ReFit.Editor.Fit:Run ()\nNUnit.Framework.Internal.Reflect:InvokeMethod ()"));
            Assert.False(IssueStackTrace.HasTestFrame("Orbiters.MyAvatar.Editor.AttestationService:Run ()\nOrbiters.ReFit.Editor.TestModels:Load ()"));
        }

        [Test]
        public void FullAndSystemStackTracesAreRead()
        {
            var myAvatar = Tool("My Avatar", "Orbiters.MyAvatar");
            const string full =
                "0x00007FF6A1B2C3D4 (Unity) StackWalker::GetCurrentCallstack\n" +
                "0x00007FF6A1B2C3D5 (Unity) DebugStringToFile\n" +
                "0x000001A2B3C4D5E6 (Mono JIT Code) [Debug.bindings.cs:120] UnityEngine.Debug:LogWarning (object)\n" +
                "0x000001A2B3C4D5E7 (Mono JIT Code) [Painter.cs:12] Orbiters.MyAvatar.Editor.Painter:Apply ()\n" +
                "0x00007FFA12345678 (mono-2.0-bdwgc) mono_jit_runtime_invoke";
            Assert.True(myAvatar.IsOwnLog(full, LogType.Warning), "\"Full\" stack traces start with native frames.");
            Assert.True(myAvatar.IsOwnLog("  at Orbiters.MyAvatar.Editor.Painter.Apply () [0x00000] in <abcdef>:0\n  at UnityEditor.EditorApplication.Internal_CallUpdateFunctions () [0x00000] in <abcdef>:0",
                LogType.Exception));
            Assert.False(myAvatar.IsOwnLog(string.Empty, LogType.Error));
        }

        // ---- What is kept -------------------------------------------------------------------------------------------

        private static IssueReport Report(string message, LogType type = LogType.Warning) =>
            new IssueReport { Message = message, Type = type, Severity = type == LogType.Warning ? "WARN" : "ERROR" };

        [Test]
        public void BurstHasBoundedMemoryAndRate()
        {
            var queue = new IssueReportQueue();
            var now = new DateTime(2026, 1, 1);
            for (int i = 0; i < 10000; i++)
            {
                queue.Enqueue(Report("warning " + i + " in mesh " + (char)('a' + i % 26) + (char)('a' + i / 26 % 26) + (char)('a' + i / 676 % 26)), now);
            }

            Assert.AreEqual(IssueReportQueue.QueueLimit, queue.PendingCount);
            Assert.LessOrEqual(queue.SignatureCount, IssueReportQueue.SignatureLimit);
            Assert.True(queue.TryTake(now, out _));
            Assert.False(queue.TryTake(now.AddSeconds(9), out _));
            Assert.True(queue.TryTake(now.AddSeconds(10), out _));
            queue.Clear();
            Assert.AreEqual(0, queue.PendingCount);
            Assert.AreEqual(0, queue.SignatureCount);
        }

        [Test]
        public void AVaryingWarningIsOneProblemForAnHour()
        {
            var queue = new IssueReportQueue();
            var now = new DateTime(2026, 1, 1);
            Assert.True(queue.Enqueue(Report("Mesh 12 has 300 vertices more than \u201CBody\u201D"), now));
            Assert.False(queue.Enqueue(Report("Mesh 13 has 301 vertices more than \u201CShirt\u201D"), now.AddMinutes(5)));
            Assert.True(queue.Enqueue(Report("A different problem"), now.AddMinutes(5)));
            Assert.True(queue.Enqueue(Report("Mesh 14 has 302 vertices more than \u201CHat\u201D"), now.AddMinutes(61)));
        }

        [Test]
        public void TheWarningAfterAnExceptionIsFoldedIntoIt()
        {
            var queue = new IssueReportQueue();
            var now = new DateTime(2026, 1, 1);
            Assert.True(queue.Enqueue(Report("InvalidOperationException: The menu could not be planned.", LogType.Exception), now));
            Assert.False(queue.Enqueue(Report("[My Avatar] Menu: the upload keeps the menu as it is (The menu could not be planned.)."), now.AddSeconds(1)));
            Assert.True(queue.Enqueue(Report("[My Avatar] Something else went wrong"), now.AddSeconds(1)));
            Assert.AreEqual(2, queue.PendingCount);
        }

        // ---- The reporter -------------------------------------------------------------------------------------------

        private readonly List<string> keys = new List<string>();

        [TearDown]
        public void ForgetTestSettings()
        {
            foreach (string key in keys)
            {
                EditorPrefs.DeleteKey(key + "_ShareIssueLogs");
                EditorPrefs.DeleteKey(key + "_MinIssueSeverity");
                SessionState.EraseInt("Orbiters.IssueReports." + key + ".Sent");
            }

            keys.Clear();
        }

        private sealed class Sent
        {
            public string Url;
            public string Token;
            public IssuePayload Payload;
        }

        private OrbitersIssueReporter Reporter(List<Sent> sent, Func<Task> answer = null, Action<OrbitersIssueReporter, bool> feed = null, bool share = true)
        {
            string key = "OrbitersIssueReportingTests_" + Guid.NewGuid().ToString("N");
            keys.Add(key);
            var reporter = new OrbitersIssueReporter(new IssueReporterOptions
            {
                ToolName = "Test Tool",
                SettingsKey = key,
                Namespaces = new[] { "Orbiters.TestTool." },
                Feed = feed,
                Endpoint = () => "http://127.0.0.1:1/bugs"
            })
            {
                Redactor = Redactor,
                Token = () => CurrentToken,
                IsQuiet = _ => false,
                TestsSeen = _ => { },
                Transport = (url, token, payload, cancel) =>
                {
                    sent?.Add(new Sent { Url = url, Token = token, Payload = payload });
                    return answer != null ? answer() : Task.CompletedTask;
                }
            };
            if (share)
            {
                reporter.ShareIssueLogs = true;
            }

            return reporter;
        }

        [Test]
        public void ReportingDefaultsToOff()
        {
            var reporter = Reporter(null, share: false);
            Assert.False(reporter.ShareIssueLogs, "Never on until the person turns it on.");
            Assert.AreEqual(2, reporter.MinSeverityLevel, "Warnings and worse by default.");
            Assert.False(reporter.Accept("A warning", "Orbiters.TestTool.X:Y ()", LogType.Warning, DateTime.UtcNow));
            Assert.AreEqual(0, reporter.Queue.PendingCount);
        }

        [Test]
        public void ASendCarriesTheMaskedReport()
        {
            var sent = new List<Sent>();
            var reporter = Reporter(sent);
            var now = DateTime.UtcNow;
            string message = "Upload of " + AvatarId + " failed with " + CurrentToken + " and " + OtherToken + " reading /Users/Jane Doe/Fox/a.fbx";
            Assert.True(reporter.Accept(message, "UnityEngine.Debug:LogWarning (object)\nOrbiters.TestTool.Upload:Run () (at Packages/orbiters.testtool/Upload.cs:5)", LogType.Warning, now));
            reporter.Pump(now);
            Assert.AreEqual(1, sent.Count);
            var payload = sent[0].Payload;
            Assert.AreEqual("http://127.0.0.1:1/bugs", sent[0].Url);
            Assert.AreEqual(CurrentToken, sent[0].Token, "Reports are linked to the signed-in account, as the opt-in says.");
            Assert.AreEqual("Test Tool", payload.tool);
            Assert.AreEqual("WARN", payload.severity);
            Assert.AreEqual("Warning", payload.errorType);
            StringAssert.StartsWith("Unity ", payload.userAgent);
            Assert.AreEqual(Application.unityVersion, payload.browserInfo.unityVersion);
            foreach (string secret in new[] { "11111111", "aaaaaaaa", "12345678-1234", "Jane", "Fox/" })
            {
                StringAssert.DoesNotContain(secret, payload.message);
            }

            StringAssert.Contains("Packages/orbiters.testtool/Upload.cs:5", payload.stackTrace);
        }

        [Test]
        public void ExceptionsAreErrorsNotCrashes()
        {
            var sent = new List<Sent>();
            var reporter = Reporter(sent);
            var now = DateTime.UtcNow;
            Assert.True(reporter.Accept("NullReferenceException: x", "Orbiters.TestTool.X.Y ()", LogType.Exception, now));
            reporter.Pump(now);
            Assert.AreEqual("ERROR", sent[0].Payload.severity);
            Assert.AreEqual("Exception", sent[0].Payload.errorType);
            reporter.MinSeverityLevel = 4;
            Assert.False(reporter.Accept("An error", "Orbiters.TestTool.X:Y ()", LogType.Error, now), "Below the level sent.");
        }

        [Test]
        public void AnUnreachableServerPausesSending()
        {
            var sent = new List<Sent>();
            var reporter = Reporter(sent, () => Task.FromException(new HttpRequestException("offline")));
            var now = DateTime.UtcNow;
            Assert.True(reporter.Accept("First problem", "Orbiters.TestTool.X:Y ()", LogType.Warning, now));
            Assert.True(reporter.Accept("Second problem", "Orbiters.TestTool.X:Y ()", LogType.Warning, now));
            reporter.Pump(now);
            reporter.Pump(now.AddSeconds(30));
            Assert.AreEqual(1, sent.Count, "The second waits for the pause to end.");
        }

        [Test]
        public void AtMostSixtyReportsASession()
        {
            var sent = new List<Sent>();
            var reporter = Reporter(sent);
            SessionState.SetInt("Orbiters.IssueReports." + keys[keys.Count - 1] + ".Sent", OrbitersIssueReporter.SessionLimit);
            var now = DateTime.UtcNow;
            Assert.True(reporter.Accept("A problem", "Orbiters.TestTool.X:Y ()", LogType.Warning, now));
            reporter.Pump(now);
            Assert.AreEqual(0, sent.Count);
            Assert.AreEqual(0, reporter.Queue.PendingCount);
        }

        [Test]
        public void TestsLogsAreNeverQueued()
        {
            bool seen = false;
            var reporter = Reporter(null);
            reporter.TestsSeen = _ => seen = true;
            Assert.False(reporter.Accept("Fixture failure", "Orbiters.TestTool.X:Y ()\nOrbiters.TestTool.Editor.Tests.SomeTests:Case ()", LogType.Exception, DateTime.UtcNow));
            Assert.True(seen, "Reporting pauses for every tool.");
            Assert.AreEqual(0, reporter.Queue.PendingCount);
        }

        [Test]
        public void ALoggerFeedIsConnectedOnlyWhileSharing()
        {
            var calls = new List<bool>();
            var reporter = Reporter(null, feed: (_, on) => calls.Add(on), share: false);
            reporter.ShareIssueLogs = true;
            reporter.ShareIssueLogs = true;
            reporter.ShareIssueLogs = false;
            CollectionAssert.AreEqual(new[] { true, false }, calls);
        }

        [Test]
        public void TheOptInSaysWhatIsSent()
        {
            string text = Reporter(null, share: false).ConsentText;
            StringAssert.Contains("Test Tool", text);
            StringAssert.Contains("stack traces", text);
            StringAssert.Contains("every Unity project on this computer", text);
            StringAssert.Contains("Orbiters account", text);
            StringAssert.Contains("Off by default", text);
        }
    }
}
