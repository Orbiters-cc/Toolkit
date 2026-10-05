#if UNITY_EDITOR
using System;
using UnityEditor;
public static class OrbitersEnvironment
{
    /// <summary>Raised when the server switches between production and development (each has its own account).</summary>
    public static event Action Changed;
    public static bool IsDevelopment
    {
        get => EditorPrefs.GetBool("MCB_DevEnvironment", false);
        set { if (value == IsDevelopment) return; EditorPrefs.SetBool("MCB_DevEnvironment", value); Changed?.Invoke(); }
    }
    // The local server by address: "localhost" tries IPv6 first on Windows, which a server listening on IPv4 refuses,
    // and every connection then waits about 200 ms before falling back.
    public static string ApiUrl(string scope = "", bool? development = null) => ((development ?? IsDevelopment) ? "http://127.0.0.1:4100/" : "https://api.orbiters.cc/") + scope.TrimStart('/');
    public static string WebsiteUrl => IsDevelopment ? "https://dev.orbiters.cc/" : "https://orbiters.cc/";
    public static string ResolveApiUrl(string pathOrUrl, string scope = "", string baseUrl = null)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl))
        {
            return pathOrUrl;
        }

        Uri apiRoot;
        if (!Uri.TryCreate(EnsureTrailingSlash(baseUrl ?? ApiUrl(scope ?? string.Empty)), UriKind.Absolute, out apiRoot))
        {
            return pathOrUrl;
        }

        Uri absoluteUri;
        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out absoluteUri) &&
            (absoluteUri.Scheme == Uri.UriSchemeHttp || absoluteUri.Scheme == Uri.UriSchemeHttps))
        {
            if (IsDevelopment && string.Equals(absoluteUri.Host, "dev.api.orbiters.cc", StringComparison.OrdinalIgnoreCase))
            {
                return new UriBuilder(absoluteUri) { Scheme = apiRoot.Scheme, Host = apiRoot.Host,
                    Port = apiRoot.IsDefaultPort ? -1 : apiRoot.Port }.Uri.ToString();
            }
            return NormalizeApiOrigin(absoluteUri, apiRoot);
        }

        return new Uri(apiRoot, pathOrUrl.TrimStart('/')).ToString();
    }

    private static string NormalizeApiOrigin(Uri uri, Uri apiRoot)
    {
        bool sameHost = string.Equals(uri.Host, apiRoot.Host, StringComparison.OrdinalIgnoreCase) || (IsLoopback(uri.Host) && IsLoopback(apiRoot.Host));
        bool compatiblePort = uri.Port == apiRoot.Port || (uri.IsDefaultPort && apiRoot.IsDefaultPort);
        if (!sameHost || !compatiblePort)
        {
            return uri.ToString();
        }

        var builder = new UriBuilder(uri)
        {
            Scheme = apiRoot.Scheme,
            Host = apiRoot.Host,
            Port = apiRoot.IsDefaultPort ? -1 : apiRoot.Port
        };
        return builder.Uri.ToString();
    }

    public static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1" || host == "[::1]" || host == "::1";

    private static string EnsureTrailingSlash(string value)
    {
        if (string.IsNullOrEmpty(value) || value.EndsWith("/", StringComparison.Ordinal))
        {
            return value;
        }

        return value + "/";
    }

}
#endif
