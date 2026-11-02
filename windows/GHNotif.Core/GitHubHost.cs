namespace GHNotif.Core;

/// Endpoints for github.com, GHE.com (data residency) and GitHub Enterprise Server.
public sealed record GitHubHost
{
    public const string DotCom = "github.com";

    public string Name { get; }

    public GitHubHost(string? raw) => Name = Normalize(raw);

    /// Strips the scheme, path and trailing slashes; empty input means github.com.
    /// Anything that isn't a plain DNS hostname (userinfo, port, query, odd characters) falls back to github.com,
    /// so the token can't be redirected to an unexpected server.
    public static string Normalize(string? raw)
    {
        var value = Stripped(raw);
        return IsHostname(value) ? value : DotCom;
    }

    /// True when the input names a host GHNotif will talk to (empty means github.com).
    public static bool IsValid(string? raw)
    {
        var value = Stripped(raw);
        return value.Length == 0 || IsHostname(value);
    }

    private static string Stripped(string? raw)
    {
        var value = (raw ?? "").Trim().ToLowerInvariant();
        foreach (var scheme in new[] { "https://", "http://" })
            if (value.StartsWith(scheme, StringComparison.Ordinal)) value = value[scheme.Length..];
        var slash = value.IndexOf('/');
        if (slash >= 0) value = value[..slash];
        if (value.StartsWith("api.", StringComparison.Ordinal) && (value == "api.github.com" || value.EndsWith(".ghe.com", StringComparison.Ordinal)))
            value = value["api.".Length..];
        return value;
    }

    private static bool IsHostname(string value)
    {
        var labels = value.Split('.');
        return value.Length <= 253 && labels.Length >= 2 && labels.All(label =>
            label.Length is >= 1 and <= 63 && label.All(ch => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
            && label[0] != '-' && label[^1] != '-');
    }

    public bool IsDotCom => Name == DotCom;

    /// GitHub Enterprise Server serves the API under `/api`; github.com and GHE.com use an `api.` subdomain.
    private bool UsesApiSubdomain => IsDotCom || Name.EndsWith(".ghe.com", StringComparison.Ordinal);

    public string GraphQLUrl => UsesApiSubdomain ? $"https://api.{Name}/graphql" : $"https://{Name}/api/graphql";
    public string RestBaseUrl => UsesApiSubdomain ? $"https://api.{Name}" : $"https://{Name}/api/v3";
    public string RestUrl(string path) => RestBaseUrl + path;
    public string WebUrl(string path = "") => $"https://{Name}{path}";

    /// Page creating a read-only fine-grained PAT with the permissions GHNotif needs preselected.
    public string NewTokenUrl => WebUrl("/settings/personal-access-tokens/new?name=GHNotif&description=Read-only+access+for+GHNotif"
        + "&pull_requests=read&statuses=read&expires_in=90");

    /// Page creating a classic PAT with only the repo scope (classic tokens have no read-only private repo scope).
    public string NewClassicTokenUrl => WebUrl("/settings/tokens/new?scopes=repo&description=GHNotif");
    public string TokensUrl => WebUrl("/settings/tokens");

    /// Where the user authorizes the token for SAML SSO organizations.
    public string SsoSettingsUrl(bool usingGitHubCli) =>
        // The GitHub CLI OAuth app ID is only known on github.com.
        usingGitHubCli
            ? IsDotCom ? WebUrl("/settings/connections/applications/178c6fc778ccc68e1d6a") : WebUrl("/settings/applications")
            : TokensUrl;

    /// Environment variables gh reads a token from without knowing which server it was issued for:
    /// `gh auth token --hostname` returns GH_ENTERPRISE_TOKEN for any host other than github.com and GH_TOKEN
    /// for any GHE.com host. They are removed from the gh process unless they can only belong to github.com.
    public IReadOnlyList<string> GitHubCliVariablesToClear => IsDotCom
        ? ["GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN"]
        : ["GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN"];

    /// True for https links on this host, e.g. PR URLs opened from the list.
    public bool Owns(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && string.Equals(uri.Host, Name, StringComparison.OrdinalIgnoreCase);
}
