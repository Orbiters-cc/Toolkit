#if UNITY_EDITOR
using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Signs a Unity tool in through the browser: Orbiters creates a one-time link, the browser opens it on the chosen login
/// provider, the person confirms on Orbiters, and this polls until the editor credential arrives. Nothing to copy.
/// </summary>
public static class OrbitersBrowserLogin
{
    private sealed class Link { public string linkUrl, pollToken, code; }

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <param name="provider">"discord" or "telegram".</param>
    /// <param name="client">The tool asking, shown on the confirmation page ("myavatar", "mcb"...).</param>
    /// <param name="opened">Receives the pairing code shown on the page and the link, once the browser is opened.</param>
    public static async Task LoginAsync(string provider, string client, Action<string, string> opened, CancellationToken cancellation)
    {
        var link = await OrbitersApi.SendAsync<Link>(OrbitersEnvironment.ApiUrl("editor-links"), null,
            new { provider, client, device = SystemInfo.deviceName }, cancellation);
        if (string.IsNullOrEmpty(link?.linkUrl) || string.IsNullOrEmpty(link.pollToken)) throw new InvalidOperationException("Orbiters did not return a login link.");
        Application.OpenURL(link.linkUrl);
        opened?.Invoke(link.code, link.linkUrl);

        var deadline = DateTime.UtcNow + Lifetime;
        string body = JsonConvert.SerializeObject(new { pollToken = link.pollToken });
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(2000, cancellation);
            HttpResponseMessage response;
            try
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                response = await OrbitersApi.Client.PostAsync(OrbitersEnvironment.ApiUrl("editor-links/poll"), content, cancellation);
            }
            catch (HttpRequestException) { continue; } // A dropped connection does not end the login; try again.
            using (response)
            {
                string text = await response.Content.ReadAsStringAsync();
                int status = (int)response.StatusCode;
                if (status == 425 || status == 429 || status >= 500) continue;
                if (!response.IsSuccessStatusCode)
                {
                    string message = "The login expired. Click Login again.";
                    try { message = JObject.Parse(text).Value<string>("error") ?? message; } catch (JsonException) { }
                    throw new InvalidOperationException(message);
                }
                var auth = JsonConvert.DeserializeObject<AuthenticationService.AuthData>(text);
                if (string.IsNullOrWhiteSpace(auth?.token)) throw new InvalidOperationException("Orbiters did not return an account.");
                AuthenticationService.SaveAuth(auth);
                return;
            }
        }
        throw new TimeoutException("The login took too long. Click Login again.");
    }
}
#endif
