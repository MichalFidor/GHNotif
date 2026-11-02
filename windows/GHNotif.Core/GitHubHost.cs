namespace GHNotif.Core;

/// Endpoints for github.com, GHE.com (data residency) and GitHub Enterprise Server.
public sealed record GitHubHost
{
    public const string DotCom = "github.com";

    public string Name { get; }

    public GitHubHost(string? raw) => Name = Normalize(raw);

    /// Strips the scheme, path and trailing slashes; empty input means github.com.
    public static string Normalize(string? raw)
    {
        var value = (raw ?? "").Trim().ToLowerInvariant();
        foreach (var scheme in new[] { "https://", "http://" })
            if (value.StartsWith(scheme, StringComparison.Ordinal)) value = value[scheme.Length..];
        var slash = value.IndexOf('/');
        if (slash >= 0) value = value[..slash];
        if (value.StartsWith("api.", StringComparison.Ordinal) && (value == "api.github.com" || value.EndsWith(".ghe.com", StringComparison.Ordinal)))
            value = value["api.".Length..];
        return value.Length == 0 ? DotCom : value;
    }

    public bool IsDotCom => Name == DotCom;

    /// GitHub Enterprise Server serves the API under `/api`; github.com and GHE.com use an `api.` subdomain.
    private bool UsesApiSubdomain => IsDotCom || Name.EndsWith(".ghe.com", StringComparison.Ordinal);

    public string GraphQLUrl => UsesApiSubdomain ? $"https://api.{Name}/graphql" : $"https://{Name}/api/graphql";
    public string RestBaseUrl => UsesApiSubdomain ? $"https://api.{Name}" : $"https://{Name}/api/v3";
    public string RestUrl(string path) => RestBaseUrl + path;
    public string WebUrl(string path = "") => $"https://{Name}{path}";

    /// Page creating a classic PAT with the scopes GHNotif needs preselected.
    public string NewTokenUrl => WebUrl("/settings/tokens/new?scopes=repo,read:org&description=GHNotif");
    public string TokensUrl => WebUrl("/settings/tokens");

    /// Where the user authorizes the token for SAML SSO organizations.
    public string SsoSettingsUrl(bool usingGitHubCli) =>
        // The GitHub CLI OAuth app ID is only known on github.com.
        usingGitHubCli
            ? IsDotCom ? WebUrl("/settings/connections/applications/178c6fc778ccc68e1d6a") : WebUrl("/settings/applications")
            : TokensUrl;

    /// True for https links on this host, e.g. PR URLs opened from the list.
    public bool Owns(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && string.Equals(uri.Host, Name, StringComparison.OrdinalIgnoreCase);
}
