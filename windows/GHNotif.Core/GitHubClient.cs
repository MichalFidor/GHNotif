using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace GHNotif.Core;

public sealed record SearchResult(List<PullRequest> PullRequests, HashSet<string> BlockedOrganizations,
    string? ViewerLogin = null);

/// A token together with the server it was resolved for. Every request made with it goes to that server,
/// so changing the host while an operation is running can't pair the token with another host.
public sealed record GitHubCredential(GitHubHost Host, string Token)
{
    public override string ToString() => $"GitHubCredential {{ Host = {Host.Name} }}";
}

public sealed class GitHubClient(HttpClient http)
{
    private const string SearchQuery = """
        query($q: String!, $after: String) {
          viewer { login }
          search(query: $q, type: ISSUE, first: 100, after: $after) {
            issueCount pageInfo { hasNextPage endCursor }
            nodes { ... on PullRequest {
              id number title url createdAt updatedAt reviewDecision isDraft
              author { login avatarUrl } repository { nameWithOwner }
              additions deletions
              labels(first: 100) { nodes { name } }
              commits(last: 1) { nodes { commit { oid author { user { login } } statusCheckRollup { state } } } }
              comments(last: 10) { nodes { id createdAt author { __typename login } } }
              reviews(last: 100) { nodes { id submittedAt state commit { oid } author { __typename login } } }
            } }
          }
        }
        """;

    public async Task<SearchResult> SearchAsync(GitHubCredential credential, Config config, CancellationToken ct = default)
    {
        var prs = new Dictionary<string, PullRequest>();
        var blocked = new HashSet<string>();
        string? viewer = null;
        foreach (var (query, reason) in QueryBuilder.Searches(config))
        {
            string? cursor = null;
            for (var page = 0; ; page++)
            {
                using var response = await PostAsync(credential, SearchQuery, new { q = query, after = cursor }, ct);
                if (response.Headers.TryGetValues("X-GitHub-SSO", out var headers))
                    blocked.UnionWith(ParseSso(string.Join(";", headers)));
                using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct),
                    cancellationToken: ct);
                ThrowErrors(json.RootElement, "search");
                var data = json.RootElement.GetProperty("data");
                if (data.TryGetProperty("viewer", out var viewerNode)) viewer ??= Text(viewerNode, "login");
                var search = data.GetProperty("search");
                if (search.GetProperty("issueCount").GetInt32() > 1000)
                    throw new InvalidOperationException(L("GitHub ogranicza wyszukiwanie do 1000 wyników. Zawęź zakres w ustawieniach.", "GitHub limits search to 1000 results. Narrow the scope in settings."));
                foreach (var node in search.GetProperty("nodes").EnumerateArray())
                {
                    if (node.ValueKind == JsonValueKind.Null || !node.TryGetProperty("id", out _)) continue;
                    var pr = ParsePullRequest(node);
                    var reasons = prs.TryGetValue(pr.Id, out var existing) ? existing.Reasons.ToHashSet() : [];
                    reasons.Add(reason);
                    prs[pr.Id] = pr with { Reasons = reasons };
                }
                var info = search.GetProperty("pageInfo");
                if (!info.GetProperty("hasNextPage").GetBoolean()) break;
                cursor = Text(info, "endCursor")
                    ?? throw new InvalidDataException(L("GitHub nie zwrócił kursora następnej strony.", "GitHub did not return the next page cursor."));
                if (page >= 9) throw new InvalidDataException(L("Przekroczono limit stron GitHub.", "GitHub page limit exceeded."));
            }
        }
        var filtered = prs.Values.Where(config.Includes).ToList();
        return new(filtered, blocked, viewer);
    }

    public async Task<string> CanonicalLoginAsync(GitHubCredential credential, string login, CancellationToken ct = default)
    {
        using var response = await PostAsync(credential,
            "query($login:String!){user(login:$login){login}}", new { login }, ct);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct),
            cancellationToken: ct);
        ThrowErrors(json.RootElement, "user");
        var user = json.RootElement.GetProperty("data").GetProperty("user");
        return Text(user, "login") ?? throw new InvalidOperationException(L("Nie znaleziono użytkownika.", "User not found."));
    }

    public async Task<List<string>> TeamMembersAsync(GitHubCredential credential, string organization, string team,
        CancellationToken ct = default)
    {
        static bool SafeSlug(string value) => value.Length > 0
            && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');
        if (!SafeSlug(organization) || !SafeSlug(team))
            throw new InvalidOperationException(L("Podaj poprawną nazwę organizacji i slug zespołu.", "Enter a valid organization name and team slug."));
        var members = new List<string>();
        for (var page = 1; page <= 10; page++)
        {
            using var response = await GetAsync(credential, $"/orgs/{organization}/teams/{team}/members?per_page=100&page={page}", ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new HttpRequestException(L("Token GitHub jest nieprawidłowy lub wygasł.", "The GitHub token is invalid or expired."));
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(response.StatusCode == HttpStatusCode.Forbidden
                    ? L("Brak dostępu do członkostwa organizacji (read:org lub Members: read).", "No access to organization membership (read:org or Members: read).")
                    : L($"GitHub API: HTTP {(int)response.StatusCode}. Sprawdź nazwę zespołu i dostęp tokenu.", $"GitHub API: HTTP {(int)response.StatusCode}. Check the team name and token access."));
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct),
                cancellationToken: ct);
            var logins = document.RootElement.EnumerateArray().Select(user => Text(user, "login"))
                .Where(login => login is not null).Select(login => login!).ToList();
            members.AddRange(logins);
            if (logins.Count < 100) return members;
        }
        throw new InvalidOperationException(L("Zespół ma ponad 1000 członków; ogranicz import.", "The team has over 1000 members; narrow the import."));
    }

    /// Checks the token, its scopes and access to each organization the user belongs to.
    public async Task<ConnectionDiagnosis> DiagnoseAsync(GitHubCredential credential, CancellationToken ct = default)
    {
        string login;
        List<string>? scopes;
        using (var user = await GetAsync(credential, "/user", ct))
        {
            if (user.StatusCode == HttpStatusCode.Unauthorized)
                throw new HttpRequestException(L("Token GitHub jest nieprawidłowy lub wygasł.", "The GitHub token is invalid or expired."));
            if (!user.IsSuccessStatusCode)
                throw new HttpRequestException(L($"GitHub API: HTTP {(int)user.StatusCode}.", $"GitHub API: HTTP {(int)user.StatusCode}."));
            using var json = await JsonDocument.ParseAsync(await user.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            login = Text(json.RootElement, "login") ?? "?";
            scopes = ConnectionDiagnosis.ParseScopes(user.Headers.TryGetValues("X-OAuth-Scopes", out var values)
                ? string.Join(",", values) : null);
        }
        var orgLogins = new List<string>();
        using (var orgs = await GetAsync(credential, "/user/orgs?per_page=100", ct))
        {
            if (orgs.IsSuccessStatusCode)
            {
                using var json = await JsonDocument.ParseAsync(await orgs.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                orgLogins = json.RootElement.EnumerateArray().Select(org => Text(org, "login"))
                    .Where(name => name is not null).Select(name => name!).ToList();
            }
        }
        var organizations = await Task.WhenAll(orgLogins.Select(async org =>
        {
            try
            {
                using var response = await GetAsync(credential, $"/orgs/{Uri.EscapeDataString(org)}/repos?per_page=1", ct);
                var sso = response.Headers.TryGetValues("X-GitHub-SSO", out var values) ? string.Join(";", values) : null;
                return new OrganizationDiagnosis(org, ConnectionDiagnosis.ClassifyOrg(
                    (int)response.StatusCode, sso, await response.Content.ReadAsStringAsync(ct)));
            }
            catch (HttpRequestException error)
            {
                return new OrganizationDiagnosis(org, new(OrgAccess.Error, error.Message));
            }
        }));
        return new(login, scopes, organizations.OrderBy(org => org.Login, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private async Task<HttpResponseMessage> GetAsync(GitHubCredential credential, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, credential.Host.RestUrl(path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("GHNotif/1.0");
        return await http.SendAsync(request, ct);
    }

    private async Task<HttpResponseMessage> PostAsync(GitHubCredential credential, string query, object variables, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, credential.Host.GraphQLUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Token);
        request.Headers.UserAgent.ParseAdd("GHNotif/1.0");
        request.Content = JsonContent.Create(new { query, variables });
        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var message = response.StatusCode == HttpStatusCode.Unauthorized
                ? L("Token GitHub jest nieprawidłowy lub wygasł.", "The GitHub token is invalid or expired.")
                : L($"GitHub API: HTTP {(int)response.StatusCode}. Sprawdź dostęp, SSO lub limit zapytań.", $"GitHub API: HTTP {(int)response.StatusCode}. Check access, SSO or the rate limit.");
            response.Dispose();
            throw new HttpRequestException(message);
        }
        return response;
    }

    /// Throws on GraphQL errors only when the requested field is missing; partial results (e.g. a fine-grained
    /// token without access to some repositories or checks) are kept.
    public static void ThrowErrors(JsonElement root, string field)
    {
        if (!root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array
            || errors.GetArrayLength() == 0)
            return;
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.Null)
            return;
        throw new InvalidOperationException("GitHub API: " + string.Join("; ", errors.EnumerateArray()
            .Select(e => e.TryGetProperty("message", out var message) ? message.GetString() : null)
            .Where(message => message is not null)));
    }

    public static HashSet<string> ParseSso(string header)
    {
        if (!header.Contains("partial-results", StringComparison.OrdinalIgnoreCase)) return [];
        var field = header.Split(';', StringSplitOptions.TrimEntries)
            .FirstOrDefault(p => p.StartsWith("organizations=", StringComparison.OrdinalIgnoreCase));
        return field is null ? [] : field["organizations=".Length..]
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet();
    }

    public static PullRequest ParsePullRequest(JsonElement node)
    {
        var author = node.GetProperty("author");
        string? checks = null;
        string? oid = null;
        string? headAuthor = null;
        var additions = node.TryGetProperty("additions", out var additionsNode) ? additionsNode.GetInt32() : 0;
        var deletions = node.TryGetProperty("deletions", out var deletionsNode) ? deletionsNode.GetInt32() : 0;
        var labels = node.TryGetProperty("labels", out var labelConnection)
            ? labelConnection.GetProperty("nodes").EnumerateArray().Select(label => Text(label, "name"))
                .Where(name => name is not null).Select(name => name!).ToList()
            : [];
        var reviews = new List<PullRequestReview>();
        if (node.TryGetProperty("reviews", out var reviewConnection))
            foreach (var review in reviewConnection.GetProperty("nodes").EnumerateArray())
            {
                if (!review.TryGetProperty("author", out var reviewAuthor) || Text(reviewAuthor, "__typename") == "Bot")
                    continue;
                var login = Text(reviewAuthor, "login");
                if (login is null) continue;
                var date = Text(review, "submittedAt");
                var commitOid = review.TryGetProperty("commit", out var reviewedCommit) ? Text(reviewedCommit, "oid") : null;
                reviews.Add(new(login, commitOid, date is null ? null : DateTimeOffset.Parse(date,
                    System.Globalization.CultureInfo.InvariantCulture)));
            }
        var commits = node.GetProperty("commits").GetProperty("nodes");
        if (commits.GetArrayLength() > 0)
        {
            var commit = commits[0].GetProperty("commit");
            oid = Text(commit, "oid");
            if (commit.TryGetProperty("author", out var gitAuthor) && gitAuthor.ValueKind == JsonValueKind.Object
                && gitAuthor.TryGetProperty("user", out var commitUser)) headAuthor = Text(commitUser, "login");
            if (commit.TryGetProperty("statusCheckRollup", out var rollup)) checks = Text(rollup, "state");
        }
        var activity = Activity(node, "comments", "createdAt").Concat(Activity(node, "reviews", "submittedAt")).ToList();
        return new PullRequest(node.GetProperty("id").GetString()!, node.GetProperty("number").GetInt32(),
            node.GetProperty("title").GetString()!, node.GetProperty("url").GetString()!,
            node.GetProperty("repository").GetProperty("nameWithOwner").GetString()!,
            Text(author, "login") ?? "ghost", Text(author, "avatarUrl"),
            node.GetProperty("createdAt").GetDateTimeOffset(), node.GetProperty("updatedAt").GetDateTimeOffset(),
            Text(node, "reviewDecision"), checks, oid, activity)
        {
            Additions = additions,
            Deletions = deletions,
            Labels = labels,
            Reviews = reviews,
            HeadCommitAuthor = headAuthor,
            IsDraft = node.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True
        };
    }

    private static IEnumerable<PrActivity> Activity(JsonElement node, string connection, string dateField)
    {
        if (!node.TryGetProperty(connection, out var conn) || conn.ValueKind != JsonValueKind.Object) yield break;
        foreach (var item in conn.GetProperty("nodes").EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("author", out var author)) continue;
            var login = Text(author, "login");
            var date = Text(item, dateField);
            if (login is null || date is null || Text(author, "__typename") == "Bot") continue;
            var kind = connection == "reviews" ? ActivityText.FromReviewState(Text(item, "state")) : ActivityKind.Comment;
            yield return new PrActivity(login, DateTimeOffset.Parse(date, System.Globalization.CultureInfo.InvariantCulture),
                kind, Text(item, "id"));
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
