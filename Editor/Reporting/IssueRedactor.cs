using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// What an issue report goes through before it leaves the computer (<see cref="Redact"/>), masking before cutting (a
    /// secret cut in half no longer matches). The rules are those of Orbiters Logger's anonymized project reports
    /// (Logger's ReportPrivacy: the Logger keeps its own copy, it depends on no package and the Toolkit not on it):
    /// tokens (Orbiters' own whoever's they are, Git hosts', API keys), sign-in headers, cookies, VRChat auth cookies,
    /// passwords and keys in addresses, settings, JSON and command lines, Discord webhooks, JSON web tokens, private and
    /// store license keys, e-mails (%40 too), VRChat ids, machine ids and serials, the user and computer names, and the
    /// folders of absolute paths, spaces and all (a file name stays). On top of them, for reports about a tool: web
    /// addresses other than Orbiters' own, the folders of project paths (Assets/[path]/Body.fbx, file and line kept;
    /// packages' paths stay), the signed-in member's name and the names of other people and their avatars that tools
    /// quote (“…”, "published by …").
    /// </summary>
    internal sealed class IssueRedactor
    {
        internal const string UserFolder = "%USERPROFILE%";
        internal const string ProjectFolder = "%PROJECT%";
        internal const string UnityFolder = "%UNITY%";
        internal const string UserName = "%USERNAME%";
        internal const string ComputerName = "%COMPUTERNAME%";
        internal const string Masked = "[masked]";
        internal const string HiddenPath = "[path]";
        internal const string HiddenEmail = "[email]";
        internal const string HiddenRemote = "[remote]";
        internal const string HiddenName = "[name]";
        internal const string HiddenUrl = "[url]";

        // Interpreted, not compiled: a handful of reports a minute, and no start-up cost for editors that never send one.
        private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        private const RegexOptions CaseSensitive = RegexOptions.CultureInvariant;
        private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

        private static Regex Rx(string pattern, RegexOptions options = Options) => new Regex(pattern, options, Timeout);

        // Names that only ever hold a secret: whatever follows them is masked, spaces included.
        private const string SecretNames = @"access[_\-]?token|refresh[_\-]?token|id[_\-]?token|auth[_\-]?token|api[_\-]?key|apikey|client[_\-]?secret|" +
                                           @"secret|private[_\-]?key|password|passwd|passphrase|pwd|two[_\-]?factor[_\-]?auth|authcookie|session[_\-]?id|" +
                                           @"license[_\-]?key|webhook(?:[_\-]?url)?";
        // Names ordinary text uses too ("Unexpected token: 'foreach'"): masked after '=', and after ':' only when what follows looks like a key.
        private const string WordNames = @"token|auth";
        private const string SecretFlags = @"access[_\-]?token|refresh[_\-]?token|auth[_\-]?token|token|password|passwd|api[_\-]?key|client[_\-]?secret|secret";
        private const string IdentityFlags = @"hubSessionId|cloudOrganization|cloudEnvironment|licensingIpc|hubIPC|username|userId";
        private const string PlainJsonValue = @"(?:[^""\\]|\\.)*";
        private const string EscapedJsonValue = @"(?:\\\\\\""|\\\\\\\\|\\[^""\\]|[^""\\])*";

        // ---- Secrets ------------------------------------------------------------------------------------------------

        // Any account's Orbiters token (orbit-xxxxxxxx-xxxxxxxx-xxxxxxxx-dd-mm-yyyy), and the start of one.
        private static readonly Regex OrbitersToken = Rx(@"\borbit-[0-9a-f]{8}[0-9a-f\-]*");
        private static readonly Regex DiscordWebhook = Rx(@"(discord(?:app)?\.com(?:\\?/)+api(?:\\?/)+(?:v\d+(?:\\?/)+)?webhooks(?:\\?/)+)\d+(?:\\?/)+[\w\-]+");
        private static readonly Regex UrlCredentials = Rx(
            @"(\b[a-z][a-z0-9+.\-]*:(?:\\?/){2})(?!git@)(?:[^\s/@\\""'<>:]+:[^\s\\""'<>]*?|[^\s/@\\""'<>]+?)@(?=[^\s@/\\""'<>]+(?:[:/\\""'\s?#]|$))");
        private static readonly Regex AuthorizationHeader = Rx(
            @"(\b(?:Proxy-)?Authorization\\?[""']?\s*[:=]\s*\\?[""']?\s*(?:(?:Bearer|Basic|Token|Digest|Bot|Negotiate|NTLM)\s+)?)(?!\[masked\])[^\s""'\\,;]+");
        private static readonly Regex Bearer = Rx(@"(\bBearer\s+)(?!\[masked\])[A-Za-z0-9\-._~+/]{8,}=*");
        private static readonly Regex SchemeToken = Rx(@"(\b(?:token|Basic)\s+)[A-Za-z0-9\-._~+/]{16,}=*");
        private static readonly Regex KnownTokens = Rx(
            @"\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|glpat-[A-Za-z0-9_\-]{20,}|xox[abprs]-[A-Za-z0-9\-]{10,}|sk-(?:proj-|ant-)?[A-Za-z0-9_\-]{32,}|" +
            @"AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_\-]{35}|npm_[A-Za-z0-9]{36})\b", CaseSensitive);
        private static readonly Regex Jwt = Rx(@"\beyJ[A-Za-z0-9_\-]{6,}\.[A-Za-z0-9_\-]{6,}\.[A-Za-z0-9_\-]*", CaseSensitive);
        private static readonly Regex QuerySecret = Rx(
            @"([?&](?:access_token|refresh_token|id_token|token|auth|auth_token|authtoken|key|api_key|apikey|api-key|client_secret|secret|password|passwd|pwd|" +
            @"signature|sig|code|session|sessionid|session_id|t|x-amz-signature|x-amz-credential|x-amz-security-token|x-goog-signature|x-goog-credential)=)[^&\s""'#<>\\]+");
        private static readonly Regex JsonSecret = Rx(
            @"((?<!\\)""[\w.\-]{0,40}?(?:" + SecretNames + "|" + WordNames + @"|cookie|authorization)""\s*:\s*"")" + PlainJsonValue);
        private static readonly Regex EscapedJsonSecret = Rx(
            @"(\\""[\w.\-]{0,40}?(?:" + SecretNames + "|" + WordNames + @"|cookie|authorization)\\""\s*:\s*\\"")" + EscapedJsonValue);
        private static readonly Regex KeyValueSecret = Rx(
            @"((?:" + SecretNames + @")[ \t]*[:=][ \t]*)(?:([""'])(?:(?!\2)[^\r\n])*\2?|(?![""'\s])(?!\[masked\])[^\r\n]+)");
        private static readonly Regex WordAssignment = Rx(@"((?:" + WordNames + @")[ \t]*=[ \t]*[""']?)(?!\[masked\])[^\s""',;&<>\\]{3,}");
        private static readonly Regex WordKey = Rx(@"((?:" + WordNames + @")[ \t]*:[ \t]*[""']?)(?!\[masked\])[A-Za-z0-9\-._~+/]{16,}=*");
        private static readonly Regex CommandLineSecret = Rx(@"((?<![\w\-])--?(?:" + SecretFlags + @")\s+)(?!-)\S+");
        private static readonly Regex SecretFlagValue = Rx(@"(?m)(^[ \t]*--?(?:" + SecretFlags + @")[ \t]*\r?\n[ \t]*)(?!-)(?!\[masked\])[^\r\n]+");
        private static readonly Regex IdentityFlagValue = Rx(@"(?m)(^[ \t]*-(?:" + IdentityFlags + @")[ \t]*\r?\n[ \t]*)(?!-)(?!\[masked\])[^\r\n]+");
        private static readonly Regex PrivateKeyBlock = Rx(@"(-----BEGIN [A-Z ]*PRIVATE KEY-----)(?:[\s\S]*?(-----END [A-Z ]*PRIVATE KEY-----)|\r?\n[\s\S]*$)");
        private static readonly Regex CookieHeader = Rx(@"(\b(?:Set-)?Cookie:[ \t]*)(?!\[masked\])[^\r\n""]+");
        // VRChat's sign-in cookie, wherever it is written.
        private static readonly Regex AuthCookie = Rx(@"\bauthcookie_[0-9a-z\-]{8,}");
        // Store license keys: Gumroad's four groups of eight, either case.
        private static readonly Regex LicenseKey = Rx(@"(?<![0-9A-Fa-f\-])[0-9A-Fa-f]{8}-[0-9A-Fa-f]{8}-[0-9A-Fa-f]{8}-[0-9A-Fa-f]{8}(?![0-9A-Fa-f\-])");
        private static readonly Regex ShortUserFolder = Rx(
            @"\b[A-Za-z](?::|%3A)(?:[\\/]{1,4}|%5C|%2F)(?:Users|Documents and Settings|Documents%20and%20Settings)(?:[\\/]{1,4}|%5C|%2F)[^\\/\s""'<>|:*?%]{1,6}~\d{1,3}(?=[\\/""'\s:;,)%]|$)");

        // ---- Who and where ------------------------------------------------------------------------------------------

        private static readonly Regex GitTrailers = Rx(@"(\b(?:Author|Committer|Co-authored-by|Signed-off-by|Reviewed-by|Acked-by|Tested-by|Reported-by|Helped-by):[ \t]*)(?!\[masked\])[^\r\n]+");
        private static readonly Regex RemoteScp = Rx(@"(?<![\w.\-])[\w.\-]+@[\w\-]+(?:\.[\w\-]+)+:(?!\d+(?:\D|$))[\w.~\-]+(?:/[\w.~\-]+)*");
        private static readonly Regex Email = Rx(@"(?<![\w.%+\-])[A-Za-z0-9._%+\-]+?(?:@|%40)[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}(?![\w\-])");
        private static readonly Regex VrchatId = Rx(@"(?<![A-Za-z0-9])(avtr|wrld|usr|file|grp|prop|inst|not|fvrt|gpos|gmem|grol|mod|cal|unk)_[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}(?![0-9a-f])");
        private static readonly Regex MachineIds = Rx(
            @"(\b(?:Machine|Session|Correlation|External correlation|Device|Hardware|Install(?:ation)?|Hub session|Organi[sz]ation|Org|User|Account|Entitlement group|Client)[ _\-]?Id\\?[""']?\s*[:=]\s*\\?[""']?)(?!\[masked\])[^\s""',;]+");
        private static readonly Regex HubArguments = Rx(@"((?<![\w\-])-(?:" + IdentityFlags + @")\s+)(?!-)\S+");
        private static readonly Regex Serial = Rx(@"(\bSerial(?:[ _\-]?(?:number|no|key))?(?: assigned to)?\\?[""']?\s*[:=]\s*\\?[""']?)(?!\[masked\])[^\s""',;]+");
        private static readonly Regex UnitySerial = Rx(@"(?<![A-Z0-9\-])[A-Z0-9]{2}-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}-(?:[A-Z0-9]{4}|XXXX)(?![A-Z0-9\-])", CaseSensitive);
        private const string IdentityNames = @"author(?:Name|Email)?|committer(?:Name|Email)?|user(?:Name)?|displayName|email|owner(?:Name)?|creator(?:Name)?|machineName|computerName|hostName|avatarName";
        private static readonly Regex JsonIdentity = Rx(@"((?<!\\)""(?:" + IdentityNames + @")""\s*:\s*"")" + PlainJsonValue);
        private static readonly Regex EscapedJsonIdentity = Rx(@"(\\""(?:" + IdentityNames + @")\\""\s*:\s*\\"")" + EscapedJsonValue);
        private static readonly Regex WindowsPath = Rx(
            @"(?<![\w%\]~])([A-Za-z]:[\\/]{1,4}|\\\\[\w.$\-]+\\{1,2}|%USERPROFILE%[\\/]{1,4})((?:[^\\/\r\n""<>|*?:]{1,255}?[\\/]{1,4})*)((?:(?![A-Za-z]:[\\/]|%USERPROFILE%)[^\\/\r\n""'<>|*?:])*)");
        private static readonly Regex UnixPath = Rx(
            @"(?<![\w.~%\]\-:/])(/(?:Users|home|Volumes|mnt|media|run/media|private/var/folders|var/folders)/)((?:[^/\r\n""<>|*?:]{1,255}?/)*)((?:(?!\s/)[^/\r\n""'<>|*?:])*)");
        // Assets/Private Avatar/Body.fbx (in the project, absolute or not): the folders go, the file and what follows stay.
        private static readonly Regex AssetsPath = Rx(
            @"(?<![\w.\-])(Assets[\\/])((?:[^\\/\r\n""<>|*?:]{1,255}?[\\/])*)((?:[^\\/\r\n""'<>|*?:])*)", CaseSensitive);
        private static readonly Regex FileName = Rx(@"^(\S.{0,254}?\.[A-Za-z][A-Za-z0-9]{0,15})(?=$|[\s,;)\]'(])", CaseSensitive);
        private static readonly Regex UserRootFolder = Rx(@"^(?:Users|Documents and Settings|home)[\\/]{1,4}$");
        private static readonly Regex TrailingSeparator = Rx(@"[\\/]+$", CaseSensitive);
        // A query value masked already ("?t=[masked]") is part of its address.
        private static readonly Regex WebAddress = Rx(@"\b(?:https?|wss?|ftp)://(?:[^\s<>""'()\[\]{}\\]|\[masked\])+");
        // Names tools quote: an avatar's (“Fox”), another VRChat account's ("published by Some Name.").
        private static readonly Regex QuotedName = Rx(@"\u201C[^\u201D\r\n]{1,160}\u201D", CaseSensitive);
        private static readonly Regex PublishedBy = Rx(@"(\b(?:published|uploaded|owned) by\s+)(?!another\b|an?\b|the\b|you\b|\[)[^\r\n]{1,64}?(?=\.(?:\s|$)|[,;:()\r\n]|$)");

        private static readonly HashSet<string> CommonNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "user", "users", "admin", "administrator", "owner", "guest", "default", "unity", "home", "test", "dev", "root", "public", "desktop"
        };

        /// <summary>Who and where reports are made: the values masked by name.</summary>
        internal sealed class Identity
        {
            public string UserFolder;
            public string UserName;
            public string ComputerName;
            public string ProjectFolder;
            public string UnityFolder;
            /// <summary>The Orbiters API's host, whose addresses keep their path (the others keep their host at most).</summary>
            public string ApiHost;
            /// <summary>Exact values to mask (the signed-in token).</summary>
            public string[] Secrets;
            /// <summary>People's names to mask (the signed-in member's).</summary>
            public string[] Names;
        }

        private readonly List<string> secrets = new List<string>();
        private readonly List<string> userFolders = new List<string>();
        private readonly List<string> projectFolders = new List<string>();
        private readonly List<string> unityFolders = new List<string>();
        private readonly Regex computerName;
        private readonly Regex userName;
        private readonly Regex names;
        private readonly string apiHost;

        internal IssueRedactor(Identity identity)
        {
            identity = identity ?? new Identity();
            AddSpellings(userFolders, identity.UserFolder);
            AddSpellings(projectFolders, identity.ProjectFolder);
            AddSpellings(unityFolders, identity.UnityFolder);
            string computer = (identity.ComputerName ?? string.Empty).Trim();
            computerName = computer.Length >= 3 && !CommonNames.Contains(computer) ? WordOf(new[] { Regex.Escape(computer) }) : null;
            userName = WordOf(UserNameSpellings(identity.UserName));
            names = WordOf((identity.Names ?? Array.Empty<string>()).SelectMany(UserNameSpellings));
            apiHost = (identity.ApiHost ?? string.Empty).ToLowerInvariant();
            foreach (string secret in identity.Secrets ?? Array.Empty<string>())
            {
                if (!string.IsNullOrEmpty(secret) && secret.Length >= 8)
                {
                    secrets.Add(secret);
                }
            }
        }

        /// <summary>This editor's identity: its user folder and names, the project, Unity and the signed-in account. Main thread.</summary>
        internal static IssueRedactor ForThisEditor()
        {
            var identity = new Identity
            {
                UserFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                UserName = Environment.UserName,
                ComputerName = Environment.MachineName
            };
            try
            {
                identity.ProjectFolder = Path.GetDirectoryName(Application.dataPath);
                identity.UnityFolder = Path.GetDirectoryName(EditorApplication.applicationPath);
                identity.ApiHost = Uri.TryCreate(OrbitersEnvironment.ApiUrl(), UriKind.Absolute, out var api) ? api.Host : null;
                var auth = AuthenticationService.GetAuth();
                identity.Secrets = new[] { auth?.token };
                identity.Names = new[] { auth?.username };
            }
            catch (Exception)
            {
                // What could not be read is masked by its pattern (paths, tokens) like any other.
            }

            return new IssueRedactor(identity);
        }

        /// <summary>
        /// <paramref name="text"/> masked, then cut to <paramref name="limit"/> characters; null when it could not be
        /// masked in time (such a report is never sent).
        /// </summary>
        internal string Redact(string text, int limit)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            try
            {
                text = Mask(text);
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }

            return text.Length <= limit ? text : text.Substring(0, limit);
        }

        internal string Mask(string text)
        {
            foreach (string secret in secrets)
            {
                if (text.IndexOf(secret, StringComparison.Ordinal) >= 0)
                {
                    text = text.Replace(secret, Masked);
                }
            }

            bool at = text.IndexOf('@') >= 0;
            bool address = text.IndexOf("://", StringComparison.Ordinal) >= 0 || text.IndexOf(":\\/\\/", StringComparison.Ordinal) >= 0;
            bool assigns = text.IndexOf('=') >= 0;
            bool colon = text.IndexOf(':') >= 0;
            bool quote = text.IndexOf('"') >= 0;
            bool dash = text.IndexOf('-') >= 0;
            bool lines = text.IndexOf('\n') >= 0;
            text = dash && lines && Has(text, "PRIVATE KEY-----") ? PrivateKeyBlock.Replace(text, match => match.Groups[1].Value + "\n" + Masked +
                (match.Groups[2].Success ? "\n" + match.Groups[2].Value : string.Empty)) : text;
            text = dash && lines ? SecretFlagValue.Replace(text, "$1" + Masked) : text;
            text = Has(text, "orbit-") ? OrbitersToken.Replace(text, Masked) : text;
            text = Has(text, "webhooks") ? DiscordWebhook.Replace(text, "$1" + Masked) : text;
            text = at && address ? UrlCredentials.Replace(text, "$1" + Masked + "@") : text;
            text = Has(text, "uthorization") ? AuthorizationHeader.Replace(text, "$1" + Masked) : text;
            text = Has(text, "bearer") ? Bearer.Replace(text, "$1" + Masked) : text;
            text = Has(text, "token ") || Has(text, "basic ") ? SchemeToken.Replace(text, "$1" + Masked) : text;
            text = KnownTokens.Replace(text, Masked);
            text = text.IndexOf("eyJ", StringComparison.Ordinal) >= 0 ? Jwt.Replace(text, Masked) : text;
            text = assigns && (text.IndexOf('?') >= 0 || text.IndexOf('&') >= 0) ? QuerySecret.Replace(text, "$1" + Masked) : text;
            if (quote && colon)
            {
                text = JsonSecret.Replace(text, "$1" + Masked);
                text = text.IndexOf("\\\"", StringComparison.Ordinal) >= 0 ? EscapedJsonSecret.Replace(text, "$1" + Masked) : text;
            }

            text = colon || assigns ? KeyValueSecret.Replace(text, QuoteMasked) : text;
            text = assigns ? WordAssignment.Replace(text, "$1" + Masked) : text;
            text = colon ? WordKey.Replace(text, "$1" + Masked) : text;
            text = dash ? CommandLineSecret.Replace(text, "$1" + Masked) : text;
            text = Has(text, "cookie:") ? CookieHeader.Replace(text, "$1" + Masked) : text;
            text = Has(text, "authcookie_") ? AuthCookie.Replace(text, "authcookie_" + Masked) : text;
            text = dash ? LicenseKey.Replace(text, Masked) : text;

            // The project and the editor first: they may sit in the user folder.
            text = Replace(text, projectFolders, ProjectFolder);
            text = Replace(text, unityFolders, UnityFolder);
            text = Replace(text, userFolders, UserFolder);
            text = text.IndexOf('~') >= 0 ? ShortUserFolder.Replace(text, UserFolder) : text;
            return Anonymize(text);
        }

        private string Anonymize(string text)
        {
            text = text.IndexOf("://", StringComparison.Ordinal) >= 0 ? WebAddress.Replace(text, KeepOrbitersAddress) : text;
            bool at = text.IndexOf('@') >= 0;
            bool encodedAt = text.IndexOf("%40", StringComparison.Ordinal) >= 0;
            text = text.IndexOf('-') >= 0 && text.IndexOf('\n') >= 0 ? IdentityFlagValue.Replace(text, "$1" + Masked) : text;
            text = text.IndexOf(':') >= 0 ? GitTrailers.Replace(text, "$1" + Masked) : text;
            text = at ? RemoteScp.Replace(text, HiddenRemote) : text;
            text = at || encodedAt ? Email.Replace(text, HiddenEmail) : text;
            // Names before the ids and flags that may hold them.
            text = computerName != null ? computerName.Replace(text, ComputerName) : text;
            text = userName != null ? userName.Replace(text, UserName) : text;
            text = names != null ? names.Replace(text, HiddenName) : text;
            text = text.IndexOf('\u201C') >= 0 ? QuotedName.Replace(text, "\u201C" + HiddenName + "\u201D") : text;
            text = Has(text, " by ") ? PublishedBy.Replace(text, "$1" + HiddenName) : text;
            text = text.IndexOf('_') >= 0 ? VrchatId.Replace(text, "$1_" + Masked) : text;
            text = Has(text, "id") ? MachineIds.Replace(text, "$1" + Masked) : text;
            text = text.IndexOf('-') >= 0 ? HubArguments.Replace(text, "$1" + Masked) : text;
            text = Has(text, "serial") ? Serial.Replace(text, "$1" + Masked) : text;
            text = text.IndexOf('-') >= 0 ? UnitySerial.Replace(text, Masked) : text;
            if (text.IndexOf('"') >= 0)
            {
                text = JsonIdentity.Replace(text, "$1" + Masked);
                text = text.IndexOf("\\\"", StringComparison.Ordinal) >= 0 ? EscapedJsonIdentity.Replace(text, "$1" + Masked) : text;
            }

            if (text.IndexOf(":\\", StringComparison.Ordinal) >= 0 || text.IndexOf(":/", StringComparison.Ordinal) >= 0 || text.IndexOf("\\\\", StringComparison.Ordinal) >= 0 ||
                text.IndexOf(UserFolder, StringComparison.Ordinal) >= 0)
            {
                text = WindowsPath.Replace(text, HidePath);
            }

            if (text.IndexOf('/') >= 0)
            {
                text = UnixPath.Replace(text, HidePath);
            }

            return text.IndexOf("Assets", StringComparison.Ordinal) >= 0 ? AssetsPath.Replace(text, HideAssetFolders) : text;
        }

        // Orbiters' API keeps its address without the query (the route tells what failed), its other hosts and VRChat's
        // their host; any other address goes.
        private string KeepOrbitersAddress(Match match)
        {
            string value = match.Value.TrimEnd('.', ',', ';', ':', '!', '?');
            string rest = match.Value.Substring(value.Length);
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                return HiddenUrl + rest;
            }

            string host = uri.Host.ToLowerInvariant();
            string origin = uri.Scheme + "://" + uri.Authority;
            if (host == apiHost || OrbitersEnvironment.IsLoopback(host) || host == "api.orbiters.cc" || host == "dev.api.orbiters.cc")
            {
                return origin + uri.AbsolutePath + rest;
            }

            bool known = host == "orbiters.cc" || host.EndsWith(".orbiters.cc", StringComparison.Ordinal) || host == "vrchat.com" ||
                         host.EndsWith(".vrchat.com", StringComparison.Ordinal) || host == "vrchat.cloud" || host.EndsWith(".vrchat.cloud", StringComparison.Ordinal);
            return (known ? origin + "/" + HiddenPath : HiddenUrl) + rest;
        }

        // A quoted value keeps its quotes around the mask.
        private static string QuoteMasked(Match match)
        {
            string quote = match.Groups[2].Value;
            return match.Groups[1].Value + quote + Masked + quote;
        }

        // The folders of an absolute path become [path]. A file name right after them stays, with the rest of the line;
        // anything else there is a folder or a name (what follows Users/ or home/) and goes too.
        private static string HidePath(Match match)
        {
            string root = match.Groups[1].Value;
            string folders = match.Groups[2].Value;
            string last = match.Groups[3].Value;
            if (folders.Length == 0 && last.Trim().Length == 0)
            {
                return match.Value;
            }

            string separator = TrailingSeparator.Match(folders.Length > 0 ? folders : root).Value;
            if (last.Length > 0 && char.IsWhiteSpace(last[0]))
            {
                return HiddenPath + separator + last;
            }

            bool userRoot = root.StartsWith("/", StringComparison.Ordinal) && folders.Length == 0 || UserRootFolder.IsMatch(folders);
            var file = userRoot ? Match.Empty : FileName.Match(last);
            return file.Success ? HiddenPath + separator + last : HiddenPath + (last.Length == 0 ? separator : string.Empty);
        }

        // Assets/A/B/Body.fbx:12 → Assets/[path]/Body.fbx:12, with the rest of the line; a file right in Assets stays. A
        // folder last ends at its first space: "Assets/A/Temp could not be made" → "Assets/[path] could not be made".
        private static string HideAssetFolders(Match match)
        {
            string root = match.Groups[1].Value;
            string folders = match.Groups[2].Value;
            string last = match.Groups[3].Value;
            if (folders.Length == 0 && (last.Length == 0 || FileName.IsMatch(last)))
            {
                return match.Value;
            }

            string separator = root.Substring(root.Length - 1);
            if (last.Length > 0 && char.IsWhiteSpace(last[0]) || FileName.IsMatch(last))
            {
                return root + HiddenPath + separator + last;
            }

            int space = last.IndexOfAny(new[] { ' ', '\t' });
            return root + HiddenPath + (space >= 0 ? last.Substring(space) : last.Length == 0 ? separator : string.Empty);
        }

        private static bool Has(string text, string value) => text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

        private static void AddSpellings(List<string> into, string folder)
        {
            if (string.IsNullOrEmpty(folder) || folder.Trim().Length <= 3)
            {
                return;
            }

            string trimmed = folder.Trim().TrimEnd('\\', '/');
            string back = trimmed.Replace('/', '\\');
            string slash = trimmed.Replace('\\', '/');
            foreach (string spelling in new[]
                     {
                         back.Replace("\\", "\\\\\\\\"), back.Replace("\\", "\\\\"), back, slash, slash.Replace("/", "\\/"), slash.Replace(" ", "%20"),
                         Uri.EscapeDataString(back), Uri.EscapeDataString(slash)
                     })
            {
                if (!into.Contains(spelling, StringComparer.OrdinalIgnoreCase))
                {
                    into.Add(spelling);
                }
            }
        }

        // A name, its spellings in addresses and as one word (JaneDoe, jane_doe, jane.doe) and its 8.3 short form
        // (JANEDO~1); never a common word.
        private static IEnumerable<string> UserNameSpellings(string name)
        {
            name = (name ?? string.Empty).Trim();
            if (name.Length < 3 || CommonNames.Contains(name))
            {
                yield break;
            }

            yield return Regex.Escape(name);
            if (name.IndexOf(' ') >= 0)
            {
                foreach (string joined in new[] { "%20", "+", string.Empty, "_", ".", "-" })
                {
                    yield return Regex.Escape(name.Replace(" ", joined));
                }
            }

            var shortName = new StringBuilder();
            foreach (char c in name.ToUpperInvariant())
            {
                if (char.IsLetterOrDigit(c) && shortName.Length < 6)
                {
                    shortName.Append(c);
                }
            }

            if (shortName.Length >= 3)
            {
                yield return Regex.Escape(shortName.ToString()) + @"~\d{1,3}";
            }
        }

        private static Regex WordOf(IEnumerable<string> patterns)
        {
            var list = patterns.Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
            return list.Count == 0 ? null : new Regex(@"(?<![A-Za-z0-9])(?:" + string.Join("|", list) + @")(?![A-Za-z0-9])", Options, Timeout);
        }

        private static string Replace(string text, List<string> spellings, string replacement)
        {
            foreach (string spelling in spellings)
            {
                text = ReplaceIgnoringCase(text, spelling, replacement);
            }

            return text;
        }

        // Only whole folders: "C:\Users\Jane" is not replaced inside "C:\Users\Janet" or "C:\Users\Jane.old".
        private static string ReplaceIgnoringCase(string text, string value, string replacement)
        {
            int at = text.IndexOf(value, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return text;
            }

            var builder = new StringBuilder(text.Length);
            int from = 0;
            while (at >= 0)
            {
                int end = at + value.Length;
                if (end < text.Length && (char.IsLetterOrDigit(text[end]) ||
                                          (text[end] == '.' || text[end] == '_' || text[end] == '-') && end + 1 < text.Length && char.IsLetterOrDigit(text[end + 1])))
                {
                    at = text.IndexOf(value, at + 1, StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                builder.Append(text, from, at - from).Append(replacement);
                from = end;
                at = text.IndexOf(value, from, StringComparison.OrdinalIgnoreCase);
            }

            return builder.Append(text, from, text.Length - from).ToString();
        }
    }
}
