#if UNITY_EDITOR
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static class OrbitersApi
{
    internal static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };

    /// <summary>GET without a payload, POST with one, unless <paramref name="method"/> says otherwise (e.g. PUT).</summary>
    /// <param name="failure">The message when the server gives no reason; {0} is the HTTP status code.</param>
    public static async Task<T> SendAsync<T>(string url, string token, object payload = null, CancellationToken cancellation = default, HttpMethod method = null,
        string failure = null)
    {
        using var request = new HttpRequestMessage(method ?? (payload == null ? HttpMethod.Get : HttpMethod.Post), url);
        if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (payload != null) request.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
        using var response = await Client.SendAsync(request, cancellation);
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            string message = string.Format(failure ?? "Orbiters returned HTTP {0}. Offline matching is still available.", (int)response.StatusCode);
            try { message = JObject.Parse(body).Value<string>("error") ?? message; } catch (JsonException) { }
            throw new InvalidOperationException(message);
        }
        return string.IsNullOrWhiteSpace(body) ? default : JsonConvert.DeserializeObject<T>(body);
    }
}
#endif
