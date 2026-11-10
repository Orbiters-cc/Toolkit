#if UNITY_EDITOR
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Toolkit;

/// <summary>
/// The Orbiters API as it was called before <see cref="OrbitersHttp"/>: kept for the packages that call it, as a thin call
/// into <see cref="OrbitersHttp.JsonAsync{T}"/>. An error answer throws an InvalidOperationException (an
/// <see cref="OrbitersHttpException"/>) with the server's message; no answer throws the HttpRequestException behind it, and a
/// timeout a TaskCanceledException, as before.
/// </summary>
public static class OrbitersApi
{
    /// <summary>GET without a payload, POST with one, unless <paramref name="method"/> says otherwise (e.g. PUT).</summary>
    /// <param name="failure">The message when the server gives no reason; {0} is the HTTP status code.</param>
    public static async Task<T> SendAsync<T>(string url, string token, object payload = null, CancellationToken cancellation = default, HttpMethod method = null,
        string failure = null)
    {
        try
        {
            return await OrbitersHttp.JsonAsync<T>(method ?? (payload == null ? HttpMethod.Get : HttpMethod.Post), url, payload, token, cancellation,
                failure ?? "Orbiters returned HTTP {0}. Offline matching is still available.");
        }
        catch (OrbitersHttpException error) when (error.Offline)
        {
            if (error.InnerException is HttpRequestException network) ExceptionDispatchInfo.Capture(network).Throw();
            throw new TaskCanceledException(error.Message);
        }
    }
}
#endif
