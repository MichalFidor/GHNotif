using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace GHNotif.Core;

public sealed record SearchResult(List<PullRequest> PullRequests, HashSet<string> BlockedOrganizations,
    string? ViewerLogin = null);

public sealed class GitHubClient(HttpClient http)
{
    private const string SearchQuery = """
        query($q: String!, $after: String) {
          viewer { login }
          search(query: $q, type: ISSUE, first: 100, after: $after) {
            issueCount pageInfo { hasNextPage endCursor }
            nodes { ... on PullRequest {
              id number title url createdAt updatedAt reviewDecision
              author { login avatarUrl } repository { nameWithOwner }
              additions deletions
              labels(first: 100) { nodes { name } }
              commits(last: 1) { nodes { commit { oid statusCheckRollup { state } } } }
              comments(last: 10) { nodes { createdAt author { __typename login } } }
              reviews(last: 100) { nodes { submittedAt state commit { oid } author { __typename login } } }
            } }
          }
        }
        """;

    public async Task<SearchResult> SearchAsync(string token, Config config, CancellationToken ct = default)
    {
        var prs = new Dictionary<string, PullRequest>();
        var blocked = new HashSet<string>();
        string? viewer = null;
        foreach (var (query, reason) in QueryBuilder.Searches(config))
        {
            string? cursor = null;
            for (var page = 0; ; page++)
            {
                using var response = await PostAsync(token, SearchQuery, new { q = query, after = cursor }, ct);
                if (response.Headers.TryGetValues("X-GitHub-SSO", out var headers))
                    blocked.UnionWith(ParseSso(string.Join(";", headers)));
                using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct),
                    cancellationToken: ct);
                ThrowErrors(json.RootElement);
                var data = json.RootElement.GetProperty("data");
                if (data.TryGetProperty("viewer", out var viewerNode)) viewer ??= Text(viewerNode, "login");
                var search = data.GetProperty("search");
                if (search.GetProperty("issueCount").GetInt32() > 1000)
                    throw new InvalidOperationException("GitHub ogranicza wyszukiwanie do 1000 wyników. Zawęź zakres w ustawieniach.");
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
                    ?? throw new InvalidDataException("GitHub nie zwrócił kursora następnej strony.");
                if (page >= 9) throw new InvalidDataException("Przekroczono limit stron GitHub.");
            }
        }
        var filtered = prs.Values.Where(config.Includes).ToList();
        return new(filtered, blocked, viewer);
    }

    public async Task<string> CanonicalLoginAsync(string token, string login, CancellationToken ct = default)
    {
        using var response = await PostAsync(token,
            "query($login:String!){user(login:$login){login}}", new { login }, ct);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct),
            cancellationToken: ct);
        ThrowErrors(json.RootElement);
        var user = json.RootElement.GetProperty("data").GetProperty("user");
        return Text(user, "login") ?? throw new InvalidOperationException("Nie znaleziono użytkownika.");
    }

    public async Task<List<string>> TeamMembersAsync(string token, string organization, string team,
        CancellationToken ct = default)
    {
        static bool SafeSlug(string value) => value.Length > 0
            && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');
        if (!SafeSlug(organization) || !SafeSlug(team))
            throw new InvalidOperationException("Podaj poprawną nazwę organizacji i slug zespołu.");
        var members = new List<string>();
        for (var page = 1; page <= 10; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/orgs/{organization}/teams/{team}/members?per_page=100&page={page}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.UserAgent.ParseAdd("GHNotif/1.0");
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new HttpRequestException("Token GitHub jest nieprawidłowy lub wygasł.");
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(response.StatusCode == HttpStatusCode.Forbidden
                    ? "Brak dostępu do członkostwa organizacji (read:org lub Members: read)."
                    : $"GitHub API: HTTP {(int)response.StatusCode}. Sprawdź nazwę zespołu i dostęp tokenu.");
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct),
                cancellationToken: ct);
            var logins = document.RootElement.EnumerateArray().Select(user => Text(user, "login"))
                .Where(login => login is not null).Select(login => login!).ToList();
            members.AddRange(logins);
            if (logins.Count < 100) return members;
        }
        throw new InvalidOperationException("Zespół ma ponad 1000 członków; ogranicz import.");
    }

    private async Task<HttpResponseMessage> PostAsync(string token, string query, object variables, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/graphql");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("GHNotif/1.0");
        request.Content = JsonContent.Create(new { query, variables });
        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var message = response.StatusCode == HttpStatusCode.Unauthorized
                ? "Token GitHub jest nieprawidłowy lub wygasł."
                : $"GitHub API: HTTP {(int)response.StatusCode}. Sprawdź dostęp, SSO lub limit zapytań.";
            response.Dispose();
            throw new HttpRequestException(message);
        }
        return response;
    }

    public static void ThrowErrors(JsonElement root)
    {
        if (root.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
            throw new InvalidOperationException("GitHub API: " +
                string.Join("; ", errors.EnumerateArray().Select(e => e.GetProperty("message").GetString())));
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
            Reviews = reviews
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
            yield return new PrActivity(login, DateTimeOffset.Parse(date, System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
