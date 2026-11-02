using System.Text.Json;

namespace GHNotif.Core;

public enum OrgAccess { Ok, SsoRequired, Blocked, Error }

public sealed record OrgStatus(OrgAccess Access, string? Detail = null);

public sealed record OrganizationDiagnosis(string Login, OrgStatus Status);

/// Result of "Check connection": who the token belongs to, its scopes and which organizations it can read.
/// `Scopes` is null for fine-grained tokens and GitHub Apps (no `X-OAuth-Scopes` header).
public sealed record ConnectionDiagnosis(string Login, IReadOnlyList<string>? Scopes, IReadOnlyList<OrganizationDiagnosis> Organizations)
{
    /// Required scopes missing from a classic token; broader scopes that imply them count as present.
    public IReadOnlyList<string> MissingScopes
    {
        get
        {
            if (Scopes is null) return [];
            var granted = Scopes.Select(scope => scope.ToLowerInvariant()).ToHashSet();
            var missing = new List<string>();
            if (!granted.Contains("repo")) missing.Add("repo");
            if (!granted.Overlaps(["read:org", "write:org", "admin:org"])) missing.Add("read:org");
            return missing;
        }
    }

    public bool HasProblems => MissingScopes.Count > 0 || Organizations.Any(org => org.Status.Access != OrgAccess.Ok);

    /// First SSO authorization link, if any organization needs one.
    public string? SsoAuthorizationUrl => Organizations
        .FirstOrDefault(org => org.Status.Access == OrgAccess.SsoRequired && org.Status.Detail is not null)?.Status.Detail;

    /// Plain-text report shown in Settings, one finding per line.
    public List<string> Lines()
    {
        var result = new List<string> { L($"✓ Zalogowano jako {Login}", $"✓ Signed in as {Login}") };
        if (Scopes is not null)
        {
            var list = Scopes.Count == 0 ? L("(brak)", "(none)") : string.Join(", ", Scopes);
            result.Add(L($"Uprawnienia tokena: {list}", $"Token scopes: {list}"));
            foreach (var scope in MissingScopes)
                result.Add(scope == "repo"
                    ? L("✗ Brak uprawnienia repo - nie zobaczysz PR-ów z prywatnych repozytoriów.", "✗ Missing repo scope - PRs from private repositories won't show.")
                    : L("✗ Brak uprawnienia read:org - lista organizacji i import zespołów mogą być niepełne.", "✗ Missing read:org scope - the organization list and team import may be incomplete."));
        }
        else
        {
            result.Add(L("Token fine-grained lub GitHub App - upewnij się, że ma dostęp do repozytoriów organizacji (Pull requests: read).",
                "Fine-grained token or GitHub App - make sure it can read the organization's repositories (Pull requests: read)."));
        }
        if (Organizations.Count == 0)
            result.Add(L("Nie widać żadnej organizacji - jeśli należysz do jakiejś, sprawdź read:org i autoryzację SSO.",
                "No organizations visible - if you belong to one, check read:org and SSO authorization."));
        foreach (var org in Organizations)
            result.Add(org.Status.Access switch
            {
                OrgAccess.Ok => L($"✓ {org.Login} - dostęp OK", $"✓ {org.Login} - access OK"),
                OrgAccess.SsoRequired => L($"✗ {org.Login} - wymaga autoryzacji SSO (Configure SSO → Authorize)",
                    $"✗ {org.Login} - needs SSO authorization (Configure SSO → Authorize)"),
                OrgAccess.Blocked => L($"✗ {org.Login} - organizacja blokuje ten token: {org.Status.Detail}",
                    $"✗ {org.Login} - the organization blocks this token: {org.Status.Detail}"),
                _ => L($"? {org.Login} - nie udało się sprawdzić: {org.Status.Detail}", $"? {org.Login} - couldn't check: {org.Status.Detail}")
            });
        return result;
    }

    /// Parses the `X-OAuth-Scopes` header; null when the header is absent.
    public static List<string>? ParseScopes(string? header) => header?
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    public static bool IsSsoRequired(string? header) =>
        header?.TrimStart().StartsWith("required", StringComparison.OrdinalIgnoreCase) == true;

    /// Parses e.g. `required; url=https://github.com/orgs/acme/sso?authorization_request=...`.
    public static string? SsoUrl(string? header) => header?
        .Split(';', StringSplitOptions.TrimEntries)
        .FirstOrDefault(part => part.StartsWith("url=", StringComparison.OrdinalIgnoreCase))?[4..];

    /// Classifies a probe of `GET /orgs/{org}/repos`.
    public static OrgStatus ClassifyOrg(int statusCode, string? ssoHeader, string body)
    {
        if (statusCode is >= 200 and < 300) return new(OrgAccess.Ok);
        string message;
        try
        {
            using var json = JsonDocument.Parse(body);
            message = json.RootElement.TryGetProperty("message", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()! : body;
        }
        catch (JsonException)
        {
            message = body.Length > 200 ? body[..200] : body;
        }
        if (statusCode == 403)
            return IsSsoRequired(ssoHeader) ? new(OrgAccess.SsoRequired, SsoUrl(ssoHeader)) : new(OrgAccess.Blocked, message);
        return new(OrgAccess.Error, $"HTTP {statusCode}: {message}");
    }
}
