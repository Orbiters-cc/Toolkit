using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

/// <summary>
/// The listener the browser brings the sign-in code back to (OrbitersBrowserLogin.Callback), driven with raw HTTP over
/// loopback as a browser would: no Orbiters server involved.
/// </summary>
public sealed class BrowserLoginCallbackTests
{
    [Test]
    public void OnlyTheListenersPathWithACodeCounts()
    {
        Assert.That(OrbitersBrowserLogin.Callback.CodeFor("/abc?code=one%2Dtime", "abc"), Is.EqualTo("one-time"));
        Assert.That(OrbitersBrowserLogin.Callback.CodeFor("/abc?state=x&code=c0de", "abc"), Is.EqualTo("c0de"));
        Assert.That(OrbitersBrowserLogin.Callback.CodeFor("/abcd?code=c0de", "abc"), Is.Null);
        Assert.That(OrbitersBrowserLogin.Callback.CodeFor("/x/abc?code=c0de", "abc"), Is.Null);
        Assert.That(OrbitersBrowserLogin.Callback.CodeFor("/abc", "abc"), Is.Null);
        Assert.That(OrbitersBrowserLogin.Callback.CodeFor("/abc?code=", "abc"), Is.Null);
    }

    [UnityTest]
    public IEnumerator TheBrowserIsAnsweredOnlyOnItsPathAndSentOnAfterwards()
    {
        using var callback = OrbitersBrowserLogin.Callback.Start();
        Assert.That(callback.Port, Is.GreaterThan(0));
        Assert.That(callback.Path, Does.Match("^[A-Za-z0-9_-]{32}$"));

        // A browser's spare connection that never sends a request must not hold up the real one.
        using var idle = new TcpClient();
        idle.Connect(IPAddress.Loopback, callback.Port);

        var wrong = Exchange(callback.Port, "GET /" + callback.Path + "x?code=nope HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
        while (!wrong.IsCompleted) yield return null;
        Assert.That(wrong.Result, Does.StartWith("HTTP/1.1 404"));
        var post = Exchange(callback.Port, "POST /" + callback.Path + "?code=nope HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 0\r\n\r\n");
        while (!post.IsCompleted) yield return null;
        Assert.That(post.Result, Does.StartWith("HTTP/1.1 404"));

        var waiting = callback.WaitAsync(CancellationToken.None);
        Assert.That(waiting.IsCompleted, Is.False);
        var browser = Exchange(callback.Port, "GET /" + callback.Path + "?code=one-time HTTP/1.1\r\nHost: 127.0.0.1\r\nAccept: text/html\r\n\r\n");
        while (!waiting.IsCompleted) yield return null;
        using (var arrival = waiting.Result)
        {
            Assert.That(arrival.Code, Is.EqualTo("one-time"));
            Assert.That(browser.IsCompleted, Is.False, "the browser waits until the sign-in knows where to send it");
            var answered = arrival.SendBrowserToAsync("https://orbiters.cc/link-editor/connected?client=toolkit");
            while (!answered.IsCompleted || !browser.IsCompleted) yield return null;
        }
        Assert.That(browser.Result, Does.StartWith("HTTP/1.1 302"));
        Assert.That(browser.Result, Does.Contain("\r\nLocation: https://orbiters.cc/link-editor/connected?client=toolkit\r\n"));

        // A second arrival is not taken: the sign-in has its code.
        var again = Exchange(callback.Port, "GET /" + callback.Path + "?code=again HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
        while (!again.IsCompleted) yield return null;
        Assert.That(again.Result, Does.StartWith("HTTP/1.1 404"));
    }

    // After a script reload the sign-in listens again where its link sends the browser.
    [Test]
    public void AListenerComesBackOnItsPortAndPath()
    {
        int port;
        string path;
        using (var first = OrbitersBrowserLogin.Callback.Start()) { port = first.Port; path = first.Path; }
        using var again = OrbitersBrowserLogin.Callback.Start(port, path);
        Assert.That((again.Port, again.Path), Is.EqualTo((port, path)));
    }

    [UnityTest]
    public IEnumerator CancellingStopsTheWaitAndTheListener()
    {
        var callback = OrbitersBrowserLogin.Callback.Start();
        int port = callback.Port;
        using var cancellation = new CancellationTokenSource();
        var waiting = callback.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        while (!waiting.IsCompleted) yield return null;
        Assert.That(waiting.IsCanceled, Is.True);
        callback.Dispose();
        using var late = new TcpClient();
        Assert.Throws<SocketException>(() => late.Connect(IPAddress.Loopback, port), "nothing listens once the sign-in ended");
    }

    // Sends one raw request and reads the whole answer, as a browser would.
    private static async Task<string> Exchange(int port, string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(bytes, 0, bytes.Length);
        var answer = new StringBuilder();
        var buffer = new byte[1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0) answer.Append(Encoding.ASCII.GetString(buffer, 0, read));
        return answer.ToString();
    }
}
