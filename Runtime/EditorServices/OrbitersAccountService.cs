#if UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;

public static class OrbitersAccountService
{
    [Serializable] public sealed class Connection { public string state; public bool aiEnabled = true; }
    [Serializable] public sealed class Profile { public string username, avatarUrl; }
    public static Task<Connection> CheckAsync(string endpoint, string token, CancellationToken cancellation = default) =>
        OrbitersApi.SendAsync<Connection>(OrbitersEnvironment.ApiUrl(endpoint), token, cancellation: cancellation);
    public static Task<Profile> ProfileAsync(string user, string token, CancellationToken cancellation = default) =>
        OrbitersApi.SendAsync<Profile>(OrbitersEnvironment.ApiUrl("mcb/user") + "?u=" + Uri.EscapeDataString(user), token, cancellation: cancellation);
}
#endif
