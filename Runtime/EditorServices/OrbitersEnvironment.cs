#if UNITY_EDITOR
using System;
using UnityEditor;
public static class OrbitersEnvironment
{
    public static bool IsDevelopment { get => EditorPrefs.GetBool("MCB_DevEnvironment", false); set => EditorPrefs.SetBool("MCB_DevEnvironment", value); }
    public static string ApiUrl(string scope = "", bool? development = null) => ((development ?? IsDevelopment) ? "http://localhost:4100/" : "https://api.orbiters.cc/") + scope.TrimStart('/');
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
        bool sameHost = string.Equals(uri.Host, apiRoot.Host, StringComparison.OrdinalIgnoreCase);
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
