using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

/// <summary>The saved Orbiters account, kept in a temporary folder: these tests never touch the real sign-in.</summary>
public sealed class AuthenticationTests
{
    private string folder, previousFolder;

    [SetUp]
    public void SetUp()
    {
        folder = Path.Combine(Path.GetTempPath(), "OrbitersAccountTest-" + Guid.NewGuid().ToString("N"));
        previousFolder = AuthenticationService.FolderOverride;
        AuthenticationService.FolderOverride = folder;
    }

    [TearDown]
    public void TearDown()
    {
        AuthenticationService.FolderOverride = previousFolder;
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    [Test]
    public void TheAccountIsSavedForThisUserOnlyAndReadsBack()
    {
        var account = Account();
        string path = AuthenticationService.GetAuthFilePath(false);
        Assert.That(path, Does.StartWith(folder));
        AuthenticationService.Write(path, account);
        byte[] stored = File.ReadAllBytes(path);
        bool windows = Application.platform == RuntimePlatform.WindowsEditor;
        if (windows)
        {
            Assert.That(Encoding.UTF8.GetString(stored), Does.Not.Contain(account.token), "Encrypted with DPAPI.");
            byte[] oldKey = Encoding.UTF8.GetBytes("MCBMagicSync");
            Assert.That(Encoding.UTF8.GetString(stored.Select((b, i) => (byte)(b ^ oldKey[i % oldKey.Length])).ToArray()),
                Does.Not.Contain(account.token), "Not the old reversible obfuscation.");
        }
        else Assert.That(ListedMode(path), Does.StartWith("-rw-------"), "Readable by this user only.");

        var read = AuthenticationService.Read(path);
        Assert.That(read.token, Is.EqualTo(account.token));
        Assert.That(read.user, Is.EqualTo(account.user));
        Assert.That(read.username, Is.EqualTo(account.username));

        if (!windows) return;
        stored[stored.Length / 2] ^= 0xFF;
        File.WriteAllBytes(path, stored);
        Assert.That(AuthenticationService.Read(path), Is.Null, "A changed file is not accepted.");
    }

    [Test]
    public void TheReversibleCopyOfEarlierVersionsIsDeleted()
    {
        Directory.CreateDirectory(folder);
        foreach (string name in new[] { "auth.dat", "auth_dev.dat", "account.dat" }) File.WriteAllText(Path.Combine(folder, name), "x");
        AuthenticationService.DeleteReversibleCopies(folder);
        Assert.That(Directory.GetFiles(folder).Select(Path.GetFileName), Is.EqualTo(new[] { "account.dat" }));
        Assert.DoesNotThrow(() => AuthenticationService.DeleteReversibleCopies(Path.Combine(folder, "missing")));
    }

    [Test]
    public void ACancelledReplacedOrMovedLoginNeverSaves()
    {
        int changes = 0;
        Action changed = () => changes++;
        AuthenticationService.Changed += changed;
        try
        {
            bool development = OrbitersEnvironment.IsDevelopment;
            var account = Account();
            using (var cancellation = new CancellationTokenSource())
            {
                var cancelled = new OrbitersBrowserLogin.Attempt(development, cancellation.Token);
                cancellation.Cancel();
                Assert.Throws<OperationCanceledException>(() => cancelled.Save(account), "Cancelled.");
            }
            var older = new OrbitersBrowserLogin.Attempt(development, CancellationToken.None);
            var newer = new OrbitersBrowserLogin.Attempt(development, CancellationToken.None);
            Assert.Throws<OperationCanceledException>(() => older.Save(account), "A newer login started.");
            Assert.DoesNotThrow(newer.ThrowIfStale, "The newest login can still finish.");

            var moved = new OrbitersBrowserLogin.Attempt(!development, CancellationToken.None);
            Assert.That(moved.ApiUrl("editor-links"), Is.EqualTo(OrbitersEnvironment.ApiUrl("editor-links", !development)),
                "A login keeps the server it started on.");
            Assert.Throws<InvalidOperationException>(() => moved.Save(account), "The tools switched server during the login.");

            Assert.That(Directory.Exists(folder) ? Directory.GetFiles(folder) : new string[0], Is.Empty);
            Assert.That(changes, Is.Zero);
        }
        finally { AuthenticationService.Changed -= changed; }
    }

    private static AuthenticationService.AuthData Account() => new AuthenticationService.AuthData
    {
        token = "orbit-test-" + Guid.NewGuid().ToString("N"), user = "7", username = "Tester",
    };

    // "-rw-------  1 user  staff  …": the file's permissions as ls shows them (macOS, Linux).
    private static string ListedMode(string path)
    {
        var start = new ProcessStartInfo("/bin/ls", "-l \"" + Path.GetFileName(path) + "\"")
        {
            WorkingDirectory = Path.GetDirectoryName(path), UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
        };
        using (var ls = Process.Start(start))
        {
            string output = ls.StandardOutput.ReadToEnd();
            ls.WaitForExit();
            return output;
        }
    }
}
